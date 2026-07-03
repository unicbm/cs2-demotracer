#!/usr/bin/env python3
"""Convert a demo round by calling the DemoTracer CLI.

This is an integration example, not a Python SDK. It shells out to
cs2-demotracer.exe, finds the generated manifest.json, and prints a CS2 console
command for playback.
"""

from __future__ import annotations

import argparse
import json
import subprocess
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Convert CS2 demo rounds with cs2-demotracer.")
    parser.add_argument("--converter", default="cs2-demotracer.exe", help="Path to cs2-demotracer.exe")
    parser.add_argument("--demo", required=True, type=Path, help="Input .dem file")
    parser.add_argument("--output", required=True, type=Path, help="Output directory")
    parser.add_argument("--rounds", default="0", help="Round selector, for example 0 or 0,1,5-8")
    parser.add_argument("--side", choices=["both", "t", "ct"], default="both", help="Side to export")
    parser.add_argument("--full-round", action="store_true", help="Keep playback past the C4 plant")
    parser.add_argument(
        "--include-suspicious",
        action="store_true",
        help="Export rounds marked suspicious by the converter",
    )
    return parser.parse_args()


def first_round(rounds: str) -> int:
    token = rounds.split(",", 1)[0].strip()
    if "-" in token:
        token = token.split("-", 1)[0].strip()
    return int(token)


def newest_manifest(output_dir: Path) -> Path:
    manifests = list(output_dir.rglob("manifest.json"))
    if not manifests:
        raise FileNotFoundError(f"no manifest.json found under {output_dir}")
    return max(manifests, key=lambda path: path.stat().st_mtime)


def console_quote_path(path: Path) -> str:
    return str(path.resolve()).replace('"', '\\"')


def main() -> None:
    args = parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    command = [
        args.converter,
        "convert",
        "--demo",
        str(args.demo),
        "--output",
        str(args.output),
        "--rounds",
        args.rounds,
    ]
    if args.side != "both":
        command.extend(["--side", args.side])
    if args.full_round:
        command.append("--full-round")
    if args.include_suspicious:
        command.append("--include-suspicious")

    print("+ " + subprocess.list2cmdline(command))
    subprocess.run(command, check=True)

    manifest = newest_manifest(args.output)
    data = json.loads(manifest.read_text(encoding="utf-8"))
    rounds = data.get("rounds", [])
    files = data.get("files", [])

    print(f"manifest: {manifest.resolve()}")
    print(f"rounds: {len(rounds)}")
    print(f"files: {len(files)}")
    print("CS2 console:")
    print(f'dtr_go round "{console_quote_path(manifest)}" {first_round(args.rounds)}; dtr_status 0')


if __name__ == "__main__":
    main()
