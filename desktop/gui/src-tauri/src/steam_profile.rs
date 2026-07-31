/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use quick_xml::de::from_str;
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const CACHE_DIRECTORY: &str = "steam-profiles-v1";
const CACHE_TTL_MS: u64 = 24 * 60 * 60 * 1_000;
const MAX_PROFILES: usize = 32;
const MAX_PARALLEL_REQUESTS: usize = 4;
const MAX_PROFILE_XML_BYTES: usize = 512 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SteamProfileDto {
    pub steam_id: String,
    pub persona_name: String,
    pub avatar_url: String,
    pub profile_url: String,
}

#[derive(Debug, Deserialize, Serialize)]
struct CachedSteamProfile {
    fetched_at_ms: u64,
    profile: SteamProfileDto,
}

#[derive(Debug, Deserialize)]
struct SteamCommunityProfileXml {
    #[serde(rename = "steamID64")]
    steam_id: String,
    #[serde(rename = "steamID")]
    persona_name: String,
    #[serde(rename = "avatarMedium")]
    avatar_url: String,
}

pub(crate) fn resolve_profiles(
    local_data_root: Option<PathBuf>,
    steam_ids: Vec<String>,
) -> Vec<SteamProfileDto> {
    let mut seen = BTreeSet::new();
    let steam_ids = steam_ids
        .into_iter()
        .map(|value| value.trim().to_string())
        .filter(|value| valid_steam_id(value) && seen.insert(value.clone()))
        .take(MAX_PROFILES)
        .collect::<Vec<_>>();
    let cache_directory = local_data_root
        .map(|root| root.join(CACHE_DIRECTORY))
        .filter(|directory| fs::create_dir_all(directory).is_ok());

    let mut profiles = Vec::with_capacity(steam_ids.len());
    for chunk in steam_ids.chunks(MAX_PARALLEL_REQUESTS) {
        let chunk_profiles = std::thread::scope(|scope| {
            chunk
                .iter()
                .map(|steam_id| {
                    let cache_directory = cache_directory.as_deref();
                    scope.spawn(move || resolve_profile(cache_directory, steam_id))
                })
                .collect::<Vec<_>>()
                .into_iter()
                .filter_map(|worker| worker.join().ok().flatten())
                .collect::<Vec<_>>()
        });
        profiles.extend(chunk_profiles);
    }
    profiles
}

fn resolve_profile(cache_directory: Option<&Path>, steam_id: &str) -> Option<SteamProfileDto> {
    let cached = cache_directory.and_then(|directory| read_cache(directory, steam_id));
    let now = now_ms();
    if cached
        .as_ref()
        .is_some_and(|entry| now.saturating_sub(entry.fetched_at_ms) <= CACHE_TTL_MS)
    {
        return cached.map(|entry| entry.profile);
    }

    match fetch_profile(steam_id) {
        Some(profile) => {
            if let Some(directory) = cache_directory {
                let _ = write_cache(
                    directory,
                    steam_id,
                    &CachedSteamProfile {
                        fetched_at_ms: now,
                        profile: profile.clone(),
                    },
                );
            }
            Some(profile)
        }
        None => cached.map(|entry| entry.profile),
    }
}

fn read_cache(directory: &Path, steam_id: &str) -> Option<CachedSteamProfile> {
    let text = fs::read_to_string(directory.join(format!("{steam_id}.json"))).ok()?;
    let cached: CachedSteamProfile = serde_json::from_str(&text).ok()?;
    (cached.profile.steam_id == steam_id).then_some(cached)
}

fn write_cache(
    directory: &Path,
    steam_id: &str,
    cached: &CachedSteamProfile,
) -> std::io::Result<()> {
    fs::write(
        directory.join(format!("{steam_id}.json")),
        serde_json::to_vec(cached)?,
    )
}

fn parse_profile_xml(steam_id: &str, xml: &str) -> Option<SteamProfileDto> {
    let profile: SteamCommunityProfileXml = from_str(xml).ok()?;
    if profile.steam_id.trim() != steam_id {
        return None;
    }
    let persona_name = profile.persona_name.trim();
    let avatar_url = profile.avatar_url.trim();
    if persona_name.is_empty() || !trusted_avatar_url(avatar_url) {
        return None;
    }
    Some(SteamProfileDto {
        steam_id: steam_id.to_string(),
        persona_name: persona_name.to_string(),
        avatar_url: avatar_url.to_string(),
        profile_url: format!("https://steamcommunity.com/profiles/{steam_id}"),
    })
}

fn valid_steam_id(value: &str) -> bool {
    value.len() == 17
        && value.as_bytes().first().is_some_and(u8::is_ascii_digit)
        && !value.starts_with('0')
        && value.bytes().all(|byte| byte.is_ascii_digit())
}

fn trusted_avatar_url(value: &str) -> bool {
    const PREFIXES: [&str; 3] = [
        "https://avatars.akamai.steamstatic.com/",
        "https://avatars.fastly.steamstatic.com/",
        "https://steamcdn-a.akamaihd.net/steamcommunity/public/images/avatars/",
    ];
    value.len() <= 512
        && !value.contains(['?', '#'])
        && PREFIXES.iter().any(|prefix| value.starts_with(prefix))
}

fn now_ms() -> u64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|duration| u64::try_from(duration.as_millis()).unwrap_or(u64::MAX))
        .unwrap_or_default()
}

#[cfg(windows)]
fn fetch_profile(steam_id: &str) -> Option<SteamProfileDto> {
    let bytes = crate::http_client::get_https(
        &format!("https://steamcommunity.com/profiles/{steam_id}?xml=1"),
        MAX_PROFILE_XML_BYTES,
        5_000,
    )
    .ok()?;
    let xml = String::from_utf8(bytes).ok()?;
    parse_profile_xml(steam_id, &xml)
}

#[cfg(not(windows))]
fn fetch_profile(_steam_id: &str) -> Option<SteamProfileDto> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    const STEAM_ID: &str = "76561198147750283";

    #[test]
    fn parses_public_profile_identity_and_avatar() {
        let xml = r#"<?xml version="1.0"?><profile>
            <steamID64>76561198147750283</steamID64>
            <steamID><![CDATA[21baz]]></steamID>
            <avatarMedium><![CDATA[https://avatars.akamai.steamstatic.com/abc_medium.jpg]]></avatarMedium>
        </profile>"#;
        let profile = parse_profile_xml(STEAM_ID, xml).unwrap();
        assert_eq!(profile.persona_name, "21baz");
        assert_eq!(profile.steam_id, STEAM_ID);
        assert_eq!(
            profile.profile_url,
            "https://steamcommunity.com/profiles/76561198147750283"
        );
    }

    #[test]
    fn rejects_mismatched_identity_or_untrusted_avatar_host() {
        let mismatch = r#"<profile><steamID64>76561198000000000</steamID64><steamID>x</steamID><avatarMedium>https://avatars.akamai.steamstatic.com/a.jpg</avatarMedium></profile>"#;
        let untrusted = r#"<profile><steamID64>76561198147750283</steamID64><steamID>x</steamID><avatarMedium>https://example.com/a.jpg</avatarMedium></profile>"#;
        assert!(parse_profile_xml(STEAM_ID, mismatch).is_none());
        assert!(parse_profile_xml(STEAM_ID, untrusted).is_none());
    }

    #[test]
    fn validates_only_steam_id64_shaped_values() {
        assert!(valid_steam_id(STEAM_ID));
        assert!(!valid_steam_id("0"));
        assert!(!valid_steam_id("7656119814775028x"));
    }
}
