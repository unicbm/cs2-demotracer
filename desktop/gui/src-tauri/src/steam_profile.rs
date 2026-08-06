/*---------------------------------------------------------------------------------------------
 * Copyright (c) 2026 unicbm. All rights reserved.
 * Licensed under the GNU Affero General Public License v3.0 only.
 * See LICENSE in the project root for license information.
 *--------------------------------------------------------------------------------------------*/

use quick_xml::{de::from_str, events::Event, Reader};
use serde::{Deserialize, Serialize};
use std::collections::BTreeSet;
use std::fs;
use std::ops::Range;
use std::path::{Path, PathBuf};
use std::time::{SystemTime, UNIX_EPOCH};

const CACHE_DIRECTORY: &str = "steam-profiles-v3";
const CACHE_TTL_MS: u64 = 24 * 60 * 60 * 1_000;
const MAX_PROFILES: usize = 32;
const MAX_PARALLEL_REQUESTS: usize = 4;
const MAX_PROFILE_XML_BYTES: usize = 512 * 1024;
const MAX_PROFILE_HTML_BYTES: usize = 1024 * 1024;

#[derive(Clone, Debug, Deserialize, Eq, PartialEq, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct SteamProfileDto {
    pub steam_id: String,
    pub persona_name: String,
    pub avatar_url: String,
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub avatar_frame_url: Option<String>,
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
    avatar_medium_url: String,
    #[serde(default, rename = "avatarFull")]
    avatar_full_url: Option<String>,
}

#[derive(Debug, Default, Eq, PartialEq)]
struct ProfileAvatarAssets {
    animated_avatar_url: Option<String>,
    avatar_frame_url: Option<String>,
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
    let avatar_url = profile
        .avatar_full_url
        .as_deref()
        .map(str::trim)
        .filter(|value| trusted_avatar_url(value))
        .or_else(|| {
            let value = profile.avatar_medium_url.trim();
            trusted_avatar_url(value).then_some(value)
        })?;
    if persona_name.is_empty() {
        return None;
    }
    Some(SteamProfileDto {
        steam_id: steam_id.to_string(),
        persona_name: persona_name.to_string(),
        avatar_url: avatar_url.to_string(),
        avatar_frame_url: None,
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

fn parse_profile_avatar_assets(html: &str) -> ProfileAvatarAssets {
    let avatar_start = [
        "profile_small_header_avatar",
        "playerAvatar profile_header_size",
    ]
    .into_iter()
    .filter_map(|marker| html.find(marker))
    .min();
    let Some(avatar_start) = avatar_start else {
        return ProfileAvatarAssets::default();
    };
    let avatar_tail = &html[avatar_start..html.len().min(avatar_start.saturating_add(8 * 1024))];
    let avatar_end = [
        "profile_header_centered_col",
        "profile_small_header_persona",
    ]
    .into_iter()
    .filter_map(|marker| avatar_tail.find(marker))
    .min()
    .unwrap_or(avatar_tail.len());
    let avatar_html = &avatar_tail[..avatar_end];
    let frame_range = div_element_range(avatar_html, "profile_avatar_frame");
    let avatar_frame_url = frame_range
        .as_ref()
        .and_then(|range| parse_image_url(&avatar_html[range.clone()], trusted_avatar_frame_url));
    let animated_avatar_url = match frame_range {
        Some(range) => parse_image_url(&avatar_html[range.end..], trusted_animated_avatar_url)
            .or_else(|| parse_image_url(&avatar_html[..range.start], trusted_animated_avatar_url)),
        None => parse_image_url(avatar_html, trusted_animated_avatar_url),
    };

    ProfileAvatarAssets {
        animated_avatar_url,
        avatar_frame_url,
    }
}

fn div_element_range(html: &str, class_marker: &str) -> Option<Range<usize>> {
    let marker = html.find(class_marker)?;
    let start = html[..marker].rfind("<div")?;
    let mut cursor = start;
    let mut depth = 0_u32;
    loop {
        let next_open = html[cursor..].find("<div").map(|offset| cursor + offset);
        let next_close = html[cursor..].find("</div>").map(|offset| cursor + offset);
        match (next_open, next_close) {
            (Some(open), Some(close)) if open < close => {
                depth = depth.saturating_add(1);
                cursor = open.saturating_add(4);
            }
            (_, Some(close)) => {
                depth = depth.checked_sub(1)?;
                cursor = close.saturating_add("</div>".len());
                if depth == 0 {
                    return Some(start..cursor);
                }
            }
            _ => return None,
        }
    }
}

fn parse_image_url(html: &str, trusted_url: fn(&str) -> bool) -> Option<String> {
    let mut cursor = 0;
    while let Some(image_offset) = html[cursor..].find("<img") {
        let image_start = cursor.saturating_add(image_offset);
        let image_end = image_start.saturating_add(html[image_start..].find('>')?);
        let mut reader = Reader::from_str(&html[image_start..=image_end]);
        let image = match reader.read_event().ok()? {
            Event::Start(image) | Event::Empty(image) => image,
            _ => return None,
        };

        for expected_name in ["srcset", "data-srcset", "src", "data-src"] {
            for attribute in image.attributes().flatten() {
                if attribute
                    .key
                    .as_ref()
                    .eq_ignore_ascii_case(expected_name.as_bytes())
                {
                    let Ok(value) = attribute.unescape_value() else {
                        continue;
                    };
                    if let Some(candidate) = value
                        .split(',')
                        .filter_map(|candidate| candidate.split_ascii_whitespace().next())
                        .find(|candidate| trusted_url(candidate))
                    {
                        return Some(candidate.to_string());
                    }
                }
            }
        }
        cursor = image_end.saturating_add(1);
    }
    None
}

fn trusted_animated_avatar_url(value: &str) -> bool {
    const PREFIX: &str = "https://shared.fastly.steamstatic.com/community_assets/images/items/";
    value.len() <= 512
        && !value.contains(['?', '#'])
        && value.starts_with(PREFIX)
        && value.ends_with(".gif")
}

fn trusted_avatar_frame_url(value: &str) -> bool {
    const PREFIX: &str = "https://shared.fastly.steamstatic.com/community_assets/images/items/";
    value.len() <= 512
        && !value.contains(['?', '#'])
        && value.starts_with(PREFIX)
        && (value.ends_with(".gif") || value.ends_with(".png"))
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
    let mut profile = parse_profile_xml(steam_id, &xml)?;
    if let Ok(bytes) =
        crate::http_client::get_https(&profile.profile_url, MAX_PROFILE_HTML_BYTES, 5_000)
    {
        if let Ok(html) = String::from_utf8(bytes) {
            let assets = parse_profile_avatar_assets(&html);
            if let Some(avatar_url) = assets.animated_avatar_url {
                profile.avatar_url = avatar_url;
            }
            profile.avatar_frame_url = assets.avatar_frame_url;
        }
    }
    Some(profile)
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
            <avatarFull><![CDATA[https://avatars.akamai.steamstatic.com/abc_full.jpg]]></avatarFull>
        </profile>"#;
        let profile = parse_profile_xml(STEAM_ID, xml).unwrap();
        assert_eq!(profile.persona_name, "21baz");
        assert_eq!(profile.steam_id, STEAM_ID);
        assert_eq!(
            profile.avatar_url,
            "https://avatars.akamai.steamstatic.com/abc_full.jpg"
        );
        assert_eq!(profile.avatar_frame_url, None);
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
    fn parses_pr_animated_avatar_from_real_profile_html_structure() {
        let html = r#"
            <div class="profile_header_content" data-panel="{&quot;flow-children&quot;:&quot;row&quot;}">
                <div class="playerAvatar profile_header_size online" data-miniprofile="350295751">
                    <div class="playerAvatarAutoSizeInner">
                        <picture>
                            <source media="(prefers-reduced-motion: reduce)" srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/af644c31a4591126ff4faf2564b88891359cbb48.jpg"></source>
                            <img srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/119373dde20ed21e9e784e98323cfd6ee4ef264d.gif" >
                        </picture>
                    </div>
                </div>
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(
            assets.animated_avatar_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/2928650/119373dde20ed21e9e784e98323cfd6ee4ef264d.gif")
        );
        assert_eq!(assets.avatar_frame_url, None);
    }

    #[test]
    fn separates_profile_frame_from_static_avatar() {
        let html = r#"
            <div class="profile_header_content">
                <div class="playerAvatar profile_header_size online">
                    <div class="playerAvatarAutoSizeInner">
                        <div class="profile_avatar_frame">
                            <picture>
                                <source media="(prefers-reduced-motion: reduce)" srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/212070/static-frame.png"></source>
                                <img src="https://shared.fastly.steamstatic.com/community_assets/images/items/212070/animated-frame.gif">
                            </picture>
                        </div>
                        <picture>
                            <img srcset="https://avatars.fastly.steamstatic.com/static_full.jpg">
                        </picture>
                    </div>
                </div>
                <div class="profile_header_centered_col"></div>
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(assets.animated_avatar_url, None);
        assert_eq!(
            assets.avatar_frame_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/212070/animated-frame.gif")
        );
    }

    #[test]
    fn supports_lazy_data_srcset_before_static_image_attributes() {
        let html = r#"
            <div class="profile_small_header_avatar">
                <img src="https://avatars.fastly.steamstatic.com/static.jpg"
                     data-src="https://avatars.fastly.steamstatic.com/lazy.jpg"
                     data-srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/1/animated.gif 1x">
            </div>
        "#;
        let assets = parse_profile_avatar_assets(html);
        assert_eq!(
            assets.animated_avatar_url.as_deref(),
            Some("https://shared.fastly.steamstatic.com/community_assets/images/items/1/animated.gif")
        );
    }

    #[test]
    fn rejects_untrusted_or_non_gif_profile_images() {
        let untrusted = r#"<div class="profile_small_header_avatar"><img srcset="https://example.com/avatar.gif"></div>"#;
        let static_image = r#"<div class="profile_small_header_avatar"><img srcset="https://shared.fastly.steamstatic.com/community_assets/images/items/1/avatar.jpg"></div>"#;
        assert_eq!(
            parse_profile_avatar_assets(untrusted),
            ProfileAvatarAssets::default()
        );
        assert_eq!(
            parse_profile_avatar_assets(static_image),
            ProfileAvatarAssets::default()
        );
    }

    #[test]
    fn validates_only_steam_id64_shaped_values() {
        assert!(valid_steam_id(STEAM_ID));
        assert!(!valid_steam_id("0"));
        assert!(!valid_steam_id("7656119814775028x"));
    }
}
