from __future__ import annotations

import argparse
import csv
import hashlib
import json
import re
from collections import defaultdict
from collections import Counter
from pathlib import Path
from typing import Any
from urllib.parse import quote


CONFIRMED_STATUSES = {
    "confirmed_id_exact_name",
    "confirmed_id_name_variant",
}


def json_array(value: str, context: str) -> list[Any]:
    try:
        parsed = json.loads(value or "[]")
    except json.JSONDecodeError as exc:
        raise ValueError(f"{context} is not valid JSON") from exc
    if not isinstance(parsed, list):
        raise ValueError(f"{context} must be a JSON array")
    return parsed


def string_value(value: Any, context: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise ValueError(f"{context} must be a non-empty string")
    return value.strip()


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def load_enrichment(path: Path | None) -> dict[str, dict[str, Any]]:
    if path is None:
        return {}
    root = json.loads(path.read_text(encoding="utf-8"))
    if root.get("schemaVersion") != 1 or not isinstance(root.get("profiles"), list):
        raise ValueError("enrichment must use schemaVersion 1 with a profiles array")
    profiles: dict[str, dict[str, Any]] = {}
    for index, candidate in enumerate(root["profiles"]):
        if not isinstance(candidate, dict):
            raise ValueError(f"profiles[{index}] must be an object")
        steam_id = string_value(candidate.get("steamId"), f"profiles[{index}].steamId")
        if steam_id in profiles:
            raise ValueError(f"duplicate enrichment SteamID64 {steam_id}")
        profiles[steam_id] = {key: value for key, value in candidate.items() if key != "steamId"}
    return profiles


def normalized_handle(value: str) -> str:
    return re.sub(r"[^0-9a-z]+", "", value.casefold())


def load_hltv_links(
    demo_memberships_path: Path | None,
    hltv_scoreboards_path: Path | None,
    verified_at: str | None,
) -> dict[str, dict[str, Any]]:
    if demo_memberships_path is None and hltv_scoreboards_path is None:
        return {}
    if demo_memberships_path is None or hltv_scoreboards_path is None or verified_at is None:
        raise ValueError(
            "--demo-memberships, --hltv-scoreboards, and --hltv-verified-at "
            "must be supplied together"
        )

    demo_series: dict[str, dict[str, str]] = defaultdict(dict)
    with demo_memberships_path.open("r", encoding="utf-8-sig", newline="") as source:
        for row_number, row in enumerate(csv.DictReader(source), start=2):
            if row.get("valid_steamid64", "").strip().lower() != "true":
                continue
            if row.get("competitive_team", "").strip().lower() != "true":
                continue
            series = string_value(
                row.get("match_slug"),
                f"demo memberships row {row_number} match_slug",
            )
            steam_id = string_value(
                row.get("steamid64"),
                f"demo memberships row {row_number} steamid64",
            )
            handle = string_value(
                row.get("player_name"),
                f"demo memberships row {row_number} player_name",
            )
            normalized = normalized_handle(handle)
            if not normalized:
                continue
            previous = demo_series[series].get(normalized)
            if previous is not None and previous != steam_id:
                # A duplicated display name cannot provide a safe roster key.
                demo_series[series][normalized] = ""
            else:
                demo_series[series][normalized] = steam_id

    hltv_matches: dict[str, dict[str, tuple[int, str]]] = defaultdict(dict)
    with hltv_scoreboards_path.open("r", encoding="utf-8-sig", newline="") as source:
        for row_number, row in enumerate(csv.DictReader(source), start=2):
            if row.get("map_name") != "All maps" or row.get("side") != "all":
                continue
            match_id = string_value(
                row.get("match_id"),
                f"HLTV scoreboard row {row_number} match_id",
            )
            handle = string_value(
                row.get("player_name"),
                f"HLTV scoreboard row {row_number} player_name",
            )
            normalized = normalized_handle(handle)
            player_id_text = string_value(
                row.get("player_id"),
                f"HLTV scoreboard row {row_number} player_id",
            )
            if not player_id_text.isdigit() or int(player_id_text) <= 0:
                raise ValueError(
                    f"HLTV scoreboard row {row_number} has invalid player_id "
                    f"{player_id_text!r}"
                )
            if not normalized:
                continue
            player_id = int(player_id_text)
            previous = hltv_matches[match_id].get(normalized)
            if previous is not None and previous[0] != player_id:
                hltv_matches[match_id][normalized] = (0, "")
            else:
                hltv_matches[match_id][normalized] = (player_id, handle)

    candidate_ids: dict[str, list[int]] = defaultdict(list)
    candidate_handles: dict[int, list[str]] = defaultdict(list)
    candidate_series: dict[tuple[str, int], set[str]] = defaultdict(set)
    for series, demo_roster in demo_series.items():
        if len(demo_roster) != 10 or any(not steam_id for steam_id in demo_roster.values()):
            continue
        roster_key = set(demo_roster)
        for match_id, hltv_roster in hltv_matches.items():
            if len(hltv_roster) != 10 or any(player_id <= 0 for player_id, _ in hltv_roster.values()):
                continue
            if roster_key != set(hltv_roster):
                continue
            for handle_key, steam_id in demo_roster.items():
                player_id, registered_handle = hltv_roster[handle_key]
                candidate_ids[steam_id].append(player_id)
                candidate_handles[player_id].append(registered_handle)
                candidate_series[(steam_id, player_id)].add(series)

    unique_candidates = {
        steam_id: ids[0]
        for steam_id, ids in candidate_ids.items()
        if len(set(ids)) == 1
    }
    reverse: dict[int, set[str]] = defaultdict(set)
    for steam_id, player_id in unique_candidates.items():
        reverse[player_id].add(steam_id)

    links: dict[str, dict[str, Any]] = {}
    for steam_id, player_id in unique_candidates.items():
        if len(reverse[player_id]) != 1:
            continue
        handle_counts = Counter(candidate_handles[player_id])
        registered_handle = sorted(
            handle_counts,
            key=lambda handle: (-handle_counts[handle], handle.casefold(), handle),
        )[0]
        slug = quote(registered_handle.casefold().replace(" ", "-"), safe="-")
        links[steam_id] = {
            "playerId": player_id,
            "registeredHandle": registered_handle,
            "source": {
                "url": f"https://www.hltv.org/player/{player_id}/{slug}",
                "verifiedAt": verified_at,
                "method": "exact-ten-player-roster",
                "matchedSeries": len(candidate_series[(steam_id, player_id)]),
            },
        }
    return links


def merge_hltv_profile(
    steam_id: str,
    linked: dict[str, Any] | None,
    enrichment: dict[str, Any] | None,
) -> dict[str, Any] | None:
    if enrichment is None:
        return linked
    enriched_hltv = enrichment.get("hltv")
    unexpected = sorted(set(enrichment) - {"hltv"})
    if unexpected:
        raise ValueError(
            f"enrichment for {steam_id} contains unsupported fields: "
            + ", ".join(unexpected)
        )
    if not isinstance(enriched_hltv, dict):
        raise ValueError(f"enrichment for {steam_id}.hltv must be an object")
    if linked is None:
        raise ValueError(
            f"enrichment for {steam_id} has no exact-roster HLTV Player ID evidence"
        )
    enriched_player_id = enriched_hltv.get("playerId")
    if enriched_player_id != linked["playerId"]:
        raise ValueError(
            f"enrichment for {steam_id} uses HLTV Player ID {enriched_player_id!r}, "
            f"but roster evidence resolves {linked['playerId']}"
        )

    merged = {**linked}
    for key in ("realName", "country"):
        if key in enriched_hltv:
            merged[key] = enriched_hltv[key]
    profile_source = enriched_hltv.get("profileSource")
    if profile_source is not None:
        if not isinstance(profile_source, dict):
            raise ValueError(f"enrichment for {steam_id}.hltv.profileSource must be an object")
        merged["profileSource"] = profile_source
    return merged


def build_catalog(
    comparison_path: Path,
    summary_path: Path,
    enrichment_path: Path | None,
    demo_memberships_path: Path | None,
    hltv_scoreboards_path: Path | None,
    hltv_verified_at: str | None,
) -> dict[str, Any]:
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    enrichment = load_enrichment(enrichment_path)
    hltv_links = load_hltv_links(
        demo_memberships_path,
        hltv_scoreboards_path,
        hltv_verified_at,
    )
    identities: dict[str, dict[str, Any]] = {}
    evidence_results: dict[str, list[str]] = defaultdict(list)
    aliases: dict[str, set[str]] = defaultdict(set)
    evidence_rows = 0
    confirmed_rows = 0
    corrected_rows = 0
    hltv_roster_identities = 0

    with comparison_path.open("r", encoding="utf-8-sig", newline="") as source:
        for row_number, row in enumerate(csv.DictReader(source), start=2):
            if row.get("has_crosshair_code", "").strip().lower() != "true":
                continue
            status = row.get("status", "").strip()
            if status in CONFIRMED_STATUSES:
                steam_id = string_value(
                    row.get("upstream_steamid64"),
                    f"comparison row {row_number} upstream_steamid64",
                )
                handle = string_value(
                    row.get("truth_name_for_upstream_id") or row.get("upstream_name"),
                    f"comparison row {row_number} handle",
                )
                row_aliases = json_array(
                    row.get("truth_aliases_for_upstream_id_json", "[]"),
                    f"comparison row {row_number} truth aliases",
                )
                result = "confirmed"
                confirmed_rows += 1
            elif status == "strict_wrong_id_for_known_name":
                expected_ids = json_array(
                    row.get("expected_truth_steamids_json", "[]"),
                    f"comparison row {row_number} expected SteamID64 values",
                )
                expected_names = json_array(
                    row.get("expected_truth_names_json", "[]"),
                    f"comparison row {row_number} expected names",
                )
                if len(expected_ids) != 1 or len(expected_names) != 1:
                    raise ValueError(
                        f"comparison row {row_number} corrected identity must be unambiguous"
                    )
                steam_id = string_value(
                    str(expected_ids[0]),
                    f"comparison row {row_number} corrected SteamID64",
                )
                handle = string_value(
                    str(expected_names[0]),
                    f"comparison row {row_number} corrected handle",
                )
                row_aliases = expected_names
                result = "corrected"
                corrected_rows += 1
            else:
                continue

            if len(steam_id) != 17 or not steam_id.isdigit():
                raise ValueError(f"comparison row {row_number} has invalid SteamID64 {steam_id!r}")
            upstream_name = string_value(
                row.get("upstream_name"),
                f"comparison row {row_number} upstream_name",
            )
            current = identities.get(steam_id)
            if current is None:
                identities[steam_id] = {"steamId": steam_id, "handle": handle}
            elif current["handle"].casefold() != handle.casefold():
                aliases[steam_id].add(handle)
            aliases[steam_id].update(
                string_value(str(alias), f"comparison row {row_number} alias")
                for alias in row_aliases
                if str(alias).strip()
            )
            aliases[steam_id].add(upstream_name)
            aliases[steam_id].add(identities[steam_id]["handle"])
            evidence_results[steam_id].append(result)
            evidence_rows += 1

    for steam_id, hltv in hltv_links.items():
        if steam_id in identities:
            continue
        if len(steam_id) != 17 or not steam_id.isdigit():
            raise ValueError(f"HLTV roster evidence has invalid SteamID64 {steam_id!r}")
        handle = string_value(
            hltv.get("registeredHandle"),
            f"HLTV roster evidence for {steam_id} registeredHandle",
        )
        identities[steam_id] = {"steamId": steam_id, "handle": handle}
        aliases[steam_id].add(handle)
        evidence_results[steam_id].append("hltv-roster")
        hltv_roster_identities += 1

    missing_enrichment = sorted(set(enrichment) - set(identities))
    if missing_enrichment:
        raise ValueError(
            "enrichment references identities absent from verified evidence: "
            + ", ".join(missing_enrichment)
        )

    verified_at = string_value(summary.get("generated_at_utc"), "summary.generated_at_utc")
    players = []
    for steam_id, identity in identities.items():
        results = evidence_results[steam_id]
        hltv = merge_hltv_profile(
            steam_id,
            hltv_links.get(steam_id),
            enrichment.get(steam_id),
        )
        hltv_only = results == ["hltv-roster"]
        player = {
            **identity,
            "aliases": sorted(aliases[steam_id], key=lambda value: (value.casefold(), value)),
            "evidence": {
                "result": (
                    "corrected"
                    if "corrected" in results
                    else "hltv-roster"
                    if hltv_only
                    else "confirmed"
                ),
                "records": (
                    hltv["source"]["matchedSeries"]
                    if hltv_only and hltv is not None
                    else len(results)
                ),
                "verifiedAt": hltv_verified_at if hltv_only else verified_at,
            },
        }
        if hltv:
            player["hltv"] = hltv
        players.append(player)
    players.sort(key=lambda player: (player["handle"].casefold(), player["handle"], player["steamId"]))

    source_url = string_value(summary.get("source_url"), "summary.source_url")
    source_blob = string_value(
        summary.get("upstream_git_blob_sha1"),
        "summary.upstream_git_blob_sha1",
    )
    source_sha256 = string_value(summary.get("upstream_sha256"), "summary.upstream_sha256")
    catalog_date = verified_at[:10]
    provenance: dict[str, Any] = {
        "identitySource": {
            "name": "XBribo/CS2-Bot-Hider bot_info.json",
            "url": source_url,
            "license": "AGPL-3.0-only",
            "gitBlobSha1": source_blob,
            "sha256": source_sha256,
        },
        "verification": {
            "method": "SteamID64 cross-checked against a demo-derived player identity census",
            "generatedAt": verified_at,
            "evidenceRows": evidence_rows,
            "uniqueSteamIds": len(players),
            "confirmedRows": confirmed_rows,
            "correctedRows": corrected_rows,
            "hltvRosterOnlyIdentities": hltv_roster_identities,
            "hltvPlayerIds": sum("hltv" in player for player in players),
        },
    }
    if (
        demo_memberships_path is not None
        and hltv_scoreboards_path is not None
        and hltv_verified_at is not None
    ):
        provenance["hltvLinking"] = {
            "source": "HLTV all-maps match scoreboards",
            "url": "https://www.hltv.org/",
            "method": "exact ten-player normalized roster equality",
            "verifiedAt": hltv_verified_at,
            "demoMembershipsSha256": sha256_file(demo_memberships_path),
            "scoreboardsSha256": sha256_file(hltv_scoreboards_path),
            "censusLinkedPlayerIds": len(hltv_links),
            "catalogLinkedPlayerIds": sum("hltv" in player for player in players),
        }

    return {
        "schemaVersion": 2,
        "catalogVersion": f"{catalog_date}.demo-verified",
        "provenance": provenance,
        "players": players,
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description="Build the sanitized SteamID64 professional identity catalog."
    )
    parser.add_argument("--comparison", type=Path, required=True)
    parser.add_argument("--summary", type=Path, required=True)
    parser.add_argument("--enrichment", type=Path)
    parser.add_argument("--demo-memberships", type=Path)
    parser.add_argument("--hltv-scoreboards", type=Path)
    parser.add_argument("--hltv-verified-at")
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()

    catalog = build_catalog(
        args.comparison.resolve(),
        args.summary.resolve(),
        args.enrichment.resolve() if args.enrichment else None,
        args.demo_memberships.resolve() if args.demo_memberships else None,
        args.hltv_scoreboards.resolve() if args.hltv_scoreboards else None,
        args.hltv_verified_at,
    )
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(
        json.dumps(catalog, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
    )
    verification = catalog["provenance"]["verification"]
    print(
        f"wrote {verification['uniqueSteamIds']} identities "
        f"from {verification['evidenceRows']} profile evidence rows and "
        f"{verification['hltvRosterOnlyIdentities']} HLTV roster-only identities"
    )


if __name__ == "__main__":
    main()
