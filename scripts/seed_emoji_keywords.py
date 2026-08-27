#!/usr/bin/env python3
"""
Seeds the localized emoji keyword lists from the official Telegram servers.

`messages.getEmojiKeywords` is what lets a user find an emoji - and, through it, a custom emoji -
by typing a word: "lion" offers 🦁, "меч" offers ⚔ 🗡. See
https://corefork.telegram.org/api/custom-emoji#emoji-keywords

Before this script the collection was filled as a by-product of `seed_stickers.py`, from the
stickersets' own titles and pack emoticons, which produced 124 rows in one language whose keywords
were the emoji themselves (`{Keyword: "🫦", Emoticons: ["🫦"]}`). Searching by word found nothing
and `getEmojiKeywords` for any language other than `en` came back empty. Real Telegram serves 4286
keywords for `en` and 5327 for `ru` (measured), so the list is copied verbatim rather than derived.

Two details that are easy to get wrong:

  * `emojiKeywordsDifference.version` is a **revision of the whole language**, not a per-row
    counter. The old seeder numbered rows 1..N, so the "version" a client stored was the index of
    the last keyword; a re-seed producing fewer rows could then never reach it through
    `getEmojiKeywordsDifference`. Every row of a language gets that language's single version here.

  * `messages.getEmojiKeywordsLanguages` must only name languages that actually have keywords -
    the API documents it as "the passed language codes (*if localized*) + en". A client told about
    a language with no data fetches an empty list and caches it (Android for an hour), so claiming
    support costs a real feature. `GetEmojiKeywordsLanguagesHandler` intersects with this
    collection, which is why the set of languages seeded here is the set clients will see.

Usage:
  1. Download from Telegram (reuse an authorized Telethon session to skip login):
       TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
       python3 seed_emoji_keywords.py --download

     TG_EMOJI_LANGS overrides the requested language codes (default "en,ru"); Telegram expands
     them with `en` and any similar languages it knows about.

  2. Import into the server:
       MONGO_URL=mongodb://172.23.0.8:27017 python3 seed_emoji_keywords.py --import

  --dry-run may be added to --import to print what would change and write nothing.

Both steps are idempotent: --import replaces the rows of every language present in the manifest
and leaves other languages alone.
"""
import asyncio
import json
import os
import sys
from pathlib import Path
from typing import Any, Dict, List

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_SESSION = os.environ.get("TG_SESSION", "emoji_keywords_seeder")
TG_EMOJI_LANGS = os.environ.get("TG_EMOJI_LANGS", "en,ru")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")

MANIFEST_FILE = Path("emoji_keywords_manifest.json")
COLLECTION = "emoji_keywords"


async def cmd_download() -> None:
    if not TG_API_ID or not TG_API_HASH:
        sys.exit("TG_API_ID and TG_API_HASH are required for --download")

    from telethon import TelegramClient
    from telethon.tl.functions.messages import (GetEmojiKeywordsLanguagesRequest,
                                                GetEmojiKeywordsRequest)
    from telethon.tl.types import EmojiKeywordDeleted

    requested = [code.strip().lower() for code in TG_EMOJI_LANGS.split(",") if code.strip()]
    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.start()

    languages = await client(GetEmojiKeywordsLanguagesRequest(lang_codes=requested))
    codes = [language.lang_code for language in languages]
    print(f"Telegram serves keywords for: {', '.join(codes)}")

    manifest: Dict[str, Any] = {"languages": {}}
    for code in codes:
        difference = await client(GetEmojiKeywordsRequest(lang_code=code))
        keywords = [{
            "keyword": keyword.keyword,
            "emoticons": list(keyword.emoticons),
            "deleted": isinstance(keyword, EmojiKeywordDeleted),
        } for keyword in difference.keywords]
        manifest["languages"][difference.lang_code] = {
            "version": difference.version,
            "keywords": keywords,
        }
        print(f"  {difference.lang_code}: version={difference.version} keywords={len(keywords)}")

    await client.disconnect()
    MANIFEST_FILE.write_text(json.dumps(manifest, ensure_ascii=False, indent=1))
    print(f"Wrote {MANIFEST_FILE}")


def build_documents(lang_code: str, language: Dict[str, Any]) -> List[Dict[str, Any]]:
    """
    One row per keyword, all carrying the language's single version - see the module docstring on
    why a per-row counter breaks getEmojiKeywordsDifference.

    Telegram serves a handful of keywords twice, differing only by a trailing space ("magic " and
    "magic" in `en`, "машина " in `ru`). A client trims what the user typed before looking a keyword
    up, so the two rows describe the same word; they are merged rather than kept apart, which also
    keeps the keyword usable as the document `_id`. A keyword that is live in either copy stays live.
    """
    version = int(language["version"])
    merged: Dict[str, Dict[str, Any]] = {}
    for keyword in language["keywords"]:
        text = (keyword.get("keyword") or "").strip()
        if not text:
            continue

        entry = merged.setdefault(text, {"emoticons": [], "deleted": True})
        if keyword.get("deleted"):
            continue

        entry["deleted"] = False
        for emoticon in keyword.get("emoticons") or []:
            if emoticon not in entry["emoticons"]:
                entry["emoticons"].append(emoticon)

    documents = []
    for text, entry in merged.items():
        document = {
            "_id": f"emoji-keyword-{lang_code}-{text}",
            "LangCode": lang_code,
            "Keyword": text,
            "Emoticons": entry["emoticons"],
            "Version": version,
        }
        if entry["deleted"]:
            document["Deleted"] = True
        documents.append(document)
    return documents


def cmd_import(dry_run: bool) -> None:
    from pymongo import MongoClient

    if not MANIFEST_FILE.exists():
        sys.exit(f"{MANIFEST_FILE} not found - run --download first")

    manifest = json.loads(MANIFEST_FILE.read_text())
    languages: Dict[str, Any] = manifest["languages"]
    database = MongoClient(MONGO_URL)[MONGO_DB]
    collection = database[COLLECTION]

    for lang_code, language in languages.items():
        documents = build_documents(lang_code, language)
        existing = collection.count_documents({"LangCode": lang_code})
        print(f"{lang_code}: {existing} stored -> {len(documents)} from Telegram "
              f"(version {language['version']})")
        if dry_run:
            continue
        collection.delete_many({"LangCode": lang_code})
        if documents:
            collection.insert_many(documents)

    if dry_run:
        print("--dry-run: nothing written")
        return

    # Languages not present in the manifest keep whatever they had: --import replaces a language,
    # it does not own the collection.
    remaining = sorted(collection.distinct("LangCode"))
    print(f"Languages now served: {', '.join(remaining)}")
    print(f"Total keywords: {collection.count_documents({})}")


if __name__ == "__main__":
    dry = "--dry-run" in sys.argv
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import(dry)
    else:
        print(__doc__)
