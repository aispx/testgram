#!/usr/bin/env python3
"""
Download Telegram language packs through MTProto and write data-seeder snapshots.

This is the MTProto/Telethon importer. The Telethon session is required because
Telegram language pack methods are authenticated MTProto calls; the session file
stores the login/auth key so the script does not ask for a code on every run.

Required environment:
  TG_API_ID=12345 TG_API_HASH=... python3 scripts/import_telegram_langpacks.py

Optional:
  TG_SESSION=language_pack_importer

Install dependency when needed:
  python3 -m pip install telethon

Download every known language from every supported Telegram platform:
  TG_API_ID=12345 TG_API_HASH=... python3 scripts/import_telegram_langpacks.py --all-platforms --all-languages

For the unauthenticated website exporter use:
  python3 scripts/import_telegram_langpacks_web.py --all-platforms --all-languages
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import shutil
import sys
from pathlib import Path
from typing import Any, Iterable


DEFAULT_PLATFORMS = (
    "android",
    "android_x",
    "ios",
    "macos",
    "tdesktop",
    "tdlib",
    "unigram",
    "weba",
    "webk",
)


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


def as_list(response: Any) -> list[Any]:
    if isinstance(response, list):
        return response
    if hasattr(response, "languages"):
        return list(response.languages)
    return list(response)


def langpack_string_to_dict(item: Any) -> dict[str, Any] | None:
    key = getattr(item, "key", "")
    if not key:
        return None

    type_name = type(item).__name__.lower()
    if "deleted" in type_name:
        return None

    result: dict[str, Any] = {"key": key, "section": "all"}
    if hasattr(item, "value"):
        result["value"] = getattr(item, "value")
        return result

    plural_fields = (
        ("zero_value", "zeroValue"),
        ("one_value", "oneValue"),
        ("two_value", "twoValue"),
        ("few_value", "fewValue"),
        ("many_value", "manyValue"),
        ("other_value", "otherValue"),
    )
    for source_name, target_name in plural_fields:
        value = getattr(item, source_name, None)
        if value is not None:
            result[target_name] = value

    return result


def build_snapshot(platform: str, language: Any, difference: Any) -> dict[str, Any]:
    strings = [
        value
        for value in (langpack_string_to_dict(item) for item in getattr(difference, "strings", []))
        if value is not None
    ]
    lang_code = getattr(language, "lang_code", "")
    translated_count = getattr(language, "translated_count", None)
    version = getattr(difference, "version", 0) or getattr(language, "version", 0) or 1
    source = (
        getattr(language, "translations_url", None)
        or f"https://translations.telegram.org/{lang_code}/{platform}/"
    )

    return {
        "source": source,
        "languageCode": lang_code,
        "languagePack": platform,
        "name": getattr(language, "name", lang_code),
        "nativeName": getattr(language, "native_name", lang_code),
        "pluralCode": getattr(language, "plural_code", lang_code),
        "rtl": bool(getattr(language, "rtl", False)),
        "version": int(version),
        "sections": {"all": int(translated_count or len(strings))},
        "strings": strings,
    }


async def get_languages(client: Any, functions: Any, platform: str) -> list[Any]:
    response = await client(functions.langpack.GetLanguagesRequest(lang_pack=platform))
    return as_list(response)


async def get_full_pack(client: Any, functions: Any, platform: str, language_code: str) -> Any:
    try:
        return await client(
            functions.langpack.GetDifferenceRequest(
                lang_pack=platform,
                lang_code=language_code,
                from_version=0,
            )
        )
    except Exception:
        return await client(
            functions.langpack.GetLangPackRequest(
                lang_pack=platform,
                lang_code=language_code,
            )
        )


def selected_languages(languages: Iterable[Any], requested: set[str] | None, limit: int | None) -> list[Any]:
    result = []
    for language in languages:
        lang_code = getattr(language, "lang_code", "")
        if requested is not None and lang_code not in requested:
            continue
        result.append(language)
        if limit is not None and len(result) >= limit:
            break
    return result


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


async def run(args: argparse.Namespace) -> int:
    try:
        from telethon import TelegramClient
        from telethon.tl import functions
    except ImportError:
        print("Telethon is not installed. Run: python3 -m pip install telethon", file=sys.stderr)
        return 2

    api_id = os.environ.get("TG_API_ID")
    api_hash = os.environ.get("TG_API_HASH")
    if not api_id or not api_hash:
        print("TG_API_ID and TG_API_HASH are required.", file=sys.stderr)
        return 2

    platforms = [
        normalize_platform(platform)
        for platform in (DEFAULT_PLATFORMS if args.all_platforms or not args.platforms else args.platforms)
    ]
    requested_languages = None if args.all_languages or not args.languages else set(args.languages)
    output_dir = args.output_dir.resolve()
    compose_output_dir = args.compose_output_dir.resolve()
    session = os.environ.get("TG_SESSION", "language_pack_importer")
    written_count = 0
    skipped_count = 0
    failed_count = 0

    async with TelegramClient(session, int(api_id), api_hash) as client:
        for platform in platforms:
            try:
                languages = selected_languages(
                    await get_languages(client, functions, platform),
                    requested_languages,
                    args.limit,
                )
            except Exception as exc:
                failed_count += 1
                print(f"[{platform}] cannot load languages: {exc}", file=sys.stderr)
                continue

            print(f"[{platform}] languages: {len(languages)}")
            if args.list_remote:
                for language in languages:
                    lang_code = getattr(language, "lang_code", "")
                    name = getattr(language, "name", lang_code)
                    native_name = getattr(language, "native_name", lang_code)
                    version = getattr(language, "version", "")
                    print(f"  {lang_code}\t{name}\t{native_name}\tversion={version}")
                continue

            for language in languages:
                lang_code = getattr(language, "lang_code", "")
                if not lang_code:
                    skipped_count += 1
                    continue

                try:
                    difference = await get_full_pack(client, functions, platform, lang_code)
                    snapshot = build_snapshot(platform, language, difference)
                    file_name, written = write_snapshot(output_dir, snapshot, args.overwrite)
                    if args.copy_to_compose and written:
                        compose_file_name = compose_output_dir / lang_code / file_name.name
                        compose_file_name.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(file_name, compose_file_name)

                    if written:
                        written_count += 1
                        print(f"  wrote {file_name}")
                    else:
                        skipped_count += 1
                        print(f"  unchanged {file_name}")
                except Exception as exc:
                    failed_count += 1
                    print(f"  failed {lang_code}/{platform}: {exc}", file=sys.stderr)

    print(
        f"Done. written={written_count}, skipped={skipped_count}, failed={failed_count}, output={output_dir}"
    )
    return 1 if failed_count else 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Import Telegram language packs for MyTelegram data-seeder.")
    parser.add_argument(
        "--platforms",
        nargs="+",
        help="Telegram lang_pack names to import. Defaults to every supported platform.",
    )
    parser.add_argument(
        "--all-platforms",
        action="store_true",
        help="Import every supported Telegram platform pack.",
    )
    parser.add_argument(
        "--languages",
        nargs="+",
        help="Optional language codes to import, for example: ru en es. Omit this to import every remote language.",
    )
    parser.add_argument(
        "--all-languages",
        action="store_true",
        help="Import every language returned by Telegram for each selected platform.",
    )
    parser.add_argument(
        "--limit",
        type=int,
        help="Optional max language count per platform, useful for smoke tests.",
    )
    parser.add_argument(
        "--output-dir",
        type=Path,
        default=default_output_dir(),
        help="Destination langpacks directory.",
    )
    parser.add_argument(
        "--copy-to-compose",
        action="store_true",
        help="Also copy updated snapshots to docker compose data-seeder volume.",
    )
    parser.add_argument(
        "--compose-output-dir",
        type=Path,
        default=default_compose_output_dir(),
        help="Compose data-seeder langpacks directory.",
    )
    parser.add_argument(
        "--overwrite",
        action="store_true",
        help="Rewrite files even when the version did not change.",
    )
    parser.add_argument(
        "--list-remote",
        action="store_true",
        help="Only print remote languages for selected platforms; do not download snapshots.",
    )
    args = parser.parse_args()
    if args.platforms and args.all_platforms:
        parser.error("--platforms cannot be combined with --all-platforms")
    if args.languages and args.all_languages:
        parser.error("--languages cannot be combined with --all-languages")
    return args


def main() -> int:
    return asyncio.run(run(parse_args()))


if __name__ == "__main__":
    raise SystemExit(main())
