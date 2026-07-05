#!/usr/bin/env python3
"""
Download Telegram language packs from https://translations.telegram.org/.

The website exposes unauthenticated XML exports at:
  /{language_code}/{platform}/export

This script converts those XML exports into the JSON snapshot format consumed by
MyTelegram.DataSeeder.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import sys
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable


DEFAULT_BASE_URL = "https://translations.telegram.org"
DEFAULT_PLATFORMS = (
    "android",
    "android_x",
    "ios",
    "macos",
    "tdesktop",
    "unigram",
    "weba",
    "webk",
)


@dataclass(frozen=True)
class WebLanguage:
    code: str
    name: str
    native_name: str


def normalize_platform(value: str) -> str:
    normalized = value.strip().lower()
    aliases = {
        "androidx": "android_x",
        "android-x": "android_x",
        "desktop": "tdesktop",
        "telegramdesktop": "tdesktop",
        "mac-os": "macos",
        "mac_os": "macos",
        "macosx": "macos",
        "web-a": "weba",
        "web_a": "weba",
        "web-k": "webk",
        "web_k": "webk",
    }
    return aliases.get(normalized, normalized)


def repo_root() -> Path:
    return Path(__file__).resolve().parents[1]


def default_output_dir() -> Path:
    return repo_root() / "source" / "src" / "MyTelegram.DataSeeder" / "downloads" / "langpacks"


def default_compose_output_dir() -> Path:
    return repo_root() / "docker" / "compose" / "data" / "mytelegram" / "data-seeder" / "downloads" / "langpacks"


def load_existing_version(file_name: Path) -> int | None:
    if not file_name.exists():
        return None

    try:
        with file_name.open("r", encoding="utf-8") as stream:
            data = json.load(stream)
        version = data.get("version")
        return int(version) if version is not None else None
    except (OSError, ValueError, TypeError, json.JSONDecodeError):
        return None


def request_text(url: str, timeout: int) -> tuple[str, dict[str, str]]:
    request = urllib.request.Request(
        url,
        headers={
            "User-Agent": "MyTelegramLangpackImporter/1.0",
            "Accept": "text/html,application/xml,text/xml,*/*",
        },
    )
    with urllib.request.urlopen(request, timeout=timeout) as response:
        charset = response.headers.get_content_charset() or "utf-8"
        return response.read().decode(charset, errors="replace"), dict(response.headers.items())


def parse_languages_from_homepage(html: str) -> list[WebLanguage]:
    match = re.search(r'"langList"\s*:\s*\[(?P<body>.*?)\]\s*,\s*"curLang"', html, re.S)
    if not match:
        raise RuntimeError("Cannot find langList in translations homepage")

    codes = re.findall(r'"([^"\\]+)"', match.group("body"))
    languages: list[WebLanguage] = []
    for code in codes:
        languages.append(WebLanguage(code=code, name=code, native_name=code))
    return languages


def get_remote_languages(base_url: str, timeout: int) -> list[WebLanguage]:
    html, _ = request_text(base_url.rstrip("/") + "/", timeout)
    return parse_languages_from_homepage(html)


def selected_languages(
    languages: Iterable[WebLanguage],
    requested: set[str] | None,
    limit: int | None,
) -> list[WebLanguage]:
    result: list[WebLanguage] = []
    for language in languages:
        if requested is not None and language.code not in requested:
            continue
        result.append(language)
        if limit is not None and len(result) >= limit:
            break
    return result


def parse_version(headers: dict[str, str]) -> int:
    disposition = headers.get("Content-Disposition") or headers.get("content-disposition") or ""
    match = re.search(r"_v(?P<version>\d+)\.xml", disposition)
    if match:
        return int(match.group("version"))
    return 1


def parse_xml_strings(xml_text: str) -> list[dict[str, Any]]:
    root = ET.fromstring(xml_text)
    strings: list[dict[str, Any]] = []
    plural_map = {
        "zero": "zeroValue",
        "one": "oneValue",
        "two": "twoValue",
        "few": "fewValue",
        "many": "manyValue",
        "other": "otherValue",
    }

    for child in root:
        if child.tag == "string":
            key = child.attrib.get("name")
            if not key:
                continue
            strings.append({"key": key, "section": "all", "value": child.text or ""})
            continue

        if child.tag == "plurals":
            key = child.attrib.get("name")
            if not key:
                continue
            item: dict[str, Any] = {"key": key, "section": "all"}
            for plural in child:
                if plural.tag != "item":
                    continue
                target_name = plural_map.get(plural.attrib.get("quantity", ""))
                if target_name:
                    item[target_name] = plural.text or ""
            if len(item) > 2:
                strings.append(item)

    return strings


def build_snapshot(
    base_url: str,
    platform: str,
    language: WebLanguage,
    xml_text: str,
    headers: dict[str, str],
) -> dict[str, Any]:
    strings = parse_xml_strings(xml_text)
    return {
        "source": f"{base_url.rstrip('/')}/{language.code}/{platform}/export",
        "languageCode": language.code,
        "languagePack": platform,
        "name": language.name,
        "nativeName": language.native_name,
        "pluralCode": language.code,
        "rtl": False,
        "version": parse_version(headers),
        "sections": {"all": len(strings)},
        "strings": strings,
    }


def write_snapshot(output_dir: Path, snapshot: dict[str, Any], overwrite: bool) -> tuple[Path, bool]:
    language_code = snapshot["languageCode"]
    platform = normalize_platform(snapshot["languagePack"])
    snapshot["languagePack"] = platform
    file_name = output_dir / language_code / f"{platform}.json"
    existing_version = load_existing_version(file_name)
    if not overwrite and existing_version == snapshot["version"]:
        return file_name, False

    file_name.parent.mkdir(parents=True, exist_ok=True)
    with file_name.open("w", encoding="utf-8") as stream:
        json.dump(snapshot, stream, ensure_ascii=False, indent=2)
        stream.write("\n")
    return file_name, True


def fetch_export(base_url: str, language_code: str, platform: str, timeout: int) -> tuple[str, dict[str, str]]:
    path = f"/{urllib.parse.quote(language_code)}/{urllib.parse.quote(platform)}/export"
    return request_text(base_url.rstrip("/") + path, timeout)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Import Telegram language packs from translations.telegram.org.")
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL, help="Translations website base URL.")
    parser.add_argument(
        "--platforms",
        nargs="+",
        help="Telegram web platform names to import. Defaults to every supported website platform.",
    )
    parser.add_argument("--all-platforms", action="store_true", help="Import every supported website platform.")
    parser.add_argument(
        "--languages",
        nargs="+",
        help="Optional language codes to import, for example: ru en es. Omit this to import every remote language.",
    )
    parser.add_argument("--all-languages", action="store_true", help="Import every language listed by the website.")
    parser.add_argument("--limit", type=int, help="Optional max language count per platform, useful for smoke tests.")
    parser.add_argument("--output-dir", type=Path, default=default_output_dir(), help="Destination langpacks directory.")
    parser.add_argument("--copy-to-compose", action="store_true", help="Also copy snapshots to compose data-seeder volume.")
    parser.add_argument(
        "--compose-output-dir",
        type=Path,
        default=default_compose_output_dir(),
        help="Compose data-seeder langpacks directory.",
    )
    parser.add_argument("--overwrite", action="store_true", help="Rewrite files even when the version did not change.")
    parser.add_argument("--list-remote", action="store_true", help="Print remote languages and exit.")
    parser.add_argument("--dry-run", action="store_true", help="Download and parse, but do not write JSON files.")
    parser.add_argument("--timeout", type=int, default=60, help="HTTP timeout in seconds.")
    args = parser.parse_args()
    if args.platforms and args.all_platforms:
        parser.error("--platforms cannot be combined with --all-platforms")
    if args.languages and args.all_languages:
        parser.error("--languages cannot be combined with --all-languages")
    return args


def main() -> int:
    args = parse_args()
    platforms = [
        normalize_platform(platform)
        for platform in (DEFAULT_PLATFORMS if args.all_platforms or not args.platforms else args.platforms)
    ]
    requested_languages = None if args.all_languages or not args.languages else set(args.languages)
    output_dir = args.output_dir.resolve()
    compose_output_dir = args.compose_output_dir.resolve()

    try:
        remote_languages = get_remote_languages(args.base_url, args.timeout)
    except Exception as exc:
        print(f"Cannot load translations homepage: {exc}", file=sys.stderr)
        return 2

    written_count = 0
    skipped_count = 0
    failed_count = 0
    languages = selected_languages(remote_languages, requested_languages, args.limit)

    if args.list_remote:
        for language in languages:
            print(f"{language.code}\t{language.name}\t{language.native_name}")
        return 0

    for platform in platforms:
        print(f"[{platform}] languages: {len(languages)}")
        for language in languages:
            try:
                xml_text, headers = fetch_export(args.base_url, language.code, platform, args.timeout)
                snapshot = build_snapshot(args.base_url, platform, language, xml_text, headers)
                if args.dry_run:
                    print(f"  parsed {language.code}/{platform}: strings={len(snapshot['strings'])}")
                    skipped_count += 1
                    continue

                file_name, written = write_snapshot(output_dir, snapshot, args.overwrite)
                if args.copy_to_compose and written:
                    compose_file_name = compose_output_dir / language.code / file_name.name
                    compose_file_name.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copy2(file_name, compose_file_name)

                if written:
                    written_count += 1
                    print(f"  wrote {file_name}")
                else:
                    skipped_count += 1
                    print(f"  unchanged {file_name}")
            except (urllib.error.HTTPError, urllib.error.URLError, ET.ParseError, RuntimeError) as exc:
                failed_count += 1
                print(f"  failed {language.code}/{platform}: {exc}", file=sys.stderr)

    print(
        f"Done. written={written_count}, skipped={skipped_count}, failed={failed_count}, output={output_dir}"
    )
    return 1 if failed_count else 0


if __name__ == "__main__":
    raise SystemExit(main())
