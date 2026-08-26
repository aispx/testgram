#!/usr/bin/env python3
"""Fill in the stickerset flags and the trending list from real Telegram.

`seed_stickers.py --import` cannot be used for this on a live deployment: it rebuilds `emoji_groups`,
`emoji_keywords`, `featured_emoji_sticker_sets` and `premium_promo_media` from its own manifest, which
would wipe the emoji taxonomy seeded separately by `seed_emoji_categories.py`. This script only ever
touches three things:

  * the set-level flags on rows that already exist in `eventflow-stickersetreadmodel`
    (`Official`, `Masks`, `Emojis`, `TextColor`, `ChannelEmojiStatus`, `ThumbDocumentId`), which the
    server needs for `stickerSet.official` / `masks` and to tell the three panels apart;
  * `featured_sticker_sets`, the trending list behind `messages.getFeaturedStickers`;
  * `Version`, which it increments so `stickerSet.hash` changes and clients pick the flags up.

Usage:
    TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
        MONGO_URL=mongodb://172.23.0.8:27017 python3 seed_sticker_set_flags.py --download

    MONGO_URL=mongodb://172.23.0.8:27017 python3 seed_sticker_set_flags.py --import [--dry-run]
"""
import argparse
import asyncio
import json
import os
import sys
from pathlib import Path

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_SESSION = os.environ.get("TG_SESSION", "sticker_set_flags")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")

CATALOGUE = "eventflow-stickersetreadmodel"
FEATURED = "featured_sticker_sets"

MANIFEST_FILE = Path(os.environ.get("FLAGS_MANIFEST", "sticker_set_flags.json"))


def open_db():
    from pymongo import MongoClient

    return MongoClient(MONGO_URL)[MONGO_DB]


def to_int(value, default=0):
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return int(value)
    return default


def catalogue_sets(db):
    """Every set we mirror, keyed by the short name Telegram knows it under."""
    rows = []
    for row in db[CATALOGUE].find({}, {"StickerSetId": 1, "ShortName": 1, "Slug": 1, "Title": 1}):
        short_name = row.get("ShortName") or row.get("Slug") or ""
        if short_name:
            rows.append({
                "set_id": to_int(row.get("StickerSetId")),
                "short_name": short_name,
            })
    return rows


def read_flags(sticker_set):
    return {
        "official": bool(getattr(sticker_set, "official", False)),
        "masks": bool(getattr(sticker_set, "masks", False)),
        "emojis": bool(getattr(sticker_set, "emojis", False)),
        "text_color": bool(getattr(sticker_set, "text_color", False)),
        "channel_emoji_status": bool(getattr(sticker_set, "channel_emoji_status", False)),
        "thumb_document_id": getattr(sticker_set, "thumb_document_id", None),
    }


async def cmd_download():
    from telethon import TelegramClient
    from telethon.errors import FloodWaitError
    from telethon.tl import functions, types

    if not TG_API_ID or not TG_API_HASH:
        print("ERROR: set TG_API_ID and TG_API_HASH", file=sys.stderr)
        return 1

    db = open_db()
    wanted = catalogue_sets(db)
    print(f"{len(wanted)} sets in the local catalogue")

    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.start()

    flags_by_short_name = {}
    missing = []

    for entry in wanted:
        short_name = entry["short_name"]
        try:
            result = await client(functions.messages.GetStickerSetRequest(
                stickerset=types.InputStickerSetShortName(short_name=short_name), hash=0))
        except FloodWaitError as error:
            print(f"  flood wait {error.seconds}s, sleeping")
            await asyncio.sleep(error.seconds + 1)
            continue
        except Exception as error:
            # A set we mirror that Telegram no longer serves, or one that never existed there
            # (anything created on this server). Its flags simply stay as they are.
            missing.append(short_name)
            print(f"  {short_name}: {type(error).__name__}")
            continue

        flags_by_short_name[short_name] = read_flags(result.set)
        print(f"  {short_name}: official={flags_by_short_name[short_name]['official']} "
              f"masks={flags_by_short_name[short_name]['masks']} "
              f"emojis={flags_by_short_name[short_name]['emojis']}")

    # The trending order, so the sets we do mirror can be shown in the same sequence Telegram uses.
    featured_order = []
    try:
        featured = await client(functions.messages.GetFeaturedStickersRequest(hash=0))
        featured_order = [covered.set.short_name for covered in featured.sets]
        print(f"Telegram trending: {len(featured_order)} sets")
    except Exception as error:
        print(f"Could not fetch the trending list: {error}")

    await client.disconnect()

    MANIFEST_FILE.write_text(json.dumps({
        "flags": flags_by_short_name,
        "featured_order": featured_order,
        "missing": missing,
    }, ensure_ascii=False, indent=1), encoding="utf-8")

    print(f"Wrote {MANIFEST_FILE} ({len(flags_by_short_name)} sets, {len(missing)} unresolved)")

    return 0


def cmd_import(dry_run: bool):
    if not MANIFEST_FILE.exists():
        print(f"ERROR: {MANIFEST_FILE} not found — run --download first", file=sys.stderr)
        return 1

    manifest = json.loads(MANIFEST_FILE.read_text(encoding="utf-8"))
    flags = manifest.get("flags", {})
    featured_order = manifest.get("featured_order", [])

    db = open_db()
    catalogue = db[CATALOGUE]

    updated = 0
    for row in catalogue.find({}, {"StickerSetId": 1, "ShortName": 1, "Slug": 1}):
        short_name = row.get("ShortName") or row.get("Slug") or ""
        entry = flags.get(short_name)
        if not entry:
            continue

        fields = {
            "Official": entry["official"],
            "Masks": entry["masks"],
            "Emojis": entry["emojis"],
            "TextColor": entry["text_color"],
            "ChannelEmojiStatus": entry["channel_emoji_status"],
        }
        if entry.get("thumb_document_id"):
            fields["ThumbDocumentId"] = to_int(entry["thumb_document_id"]) & 0x7FFFFFFFFFFFFFFF

        if dry_run:
            print(f"  {short_name}: {fields}")
        else:
            catalogue.update_one(
                {"_id": row["_id"]},
                # Version feeds stickerSet.hash, so clients only see the new flags once it moves.
                {"$set": fields, "$inc": {"Version": 1}},
            )
        updated += 1

    print(f"{'Would update' if dry_run else 'Updated'} {updated} catalogue rows")

    # Only sets we actually mirror can be trending here; the rest of Telegram's list would be ids no
    # client could resolve. Their relative order is kept.
    by_short_name = {}
    for row in catalogue.find({}, {"StickerSetId": 1, "ShortName": 1, "Slug": 1}):
        by_short_name[row.get("ShortName") or row.get("Slug") or ""] = to_int(row.get("StickerSetId"))

    featured_docs = []
    order = 1
    for short_name in featured_order:
        set_id = by_short_name.get(short_name)
        if not set_id:
            continue
        entry = flags.get(short_name, {})
        if entry.get("emojis"):
            # Custom emoji sets have their own trending collection, seeded elsewhere.
            continue
        featured_docs.append({
            "_id": f"featured-set-{set_id}",
            "StickerSetId": set_id,
            "Unread": False,
            "Archived": False,
            "Order": order,
            "Version": 1,
        })
        order += 1

    print(f"{'Would write' if dry_run else 'Writing'} {len(featured_docs)} rows to {FEATURED}"
          f" (of {len(featured_order)} trending sets on Telegram)")

    if not dry_run:
        db[FEATURED].delete_many({})
        if featured_docs:
            db[FEATURED].insert_many(featured_docs)

    if not featured_docs:
        print("  none of Telegram's trending sets are mirrored here; getFeaturedStickers will fall back "
              "to the sets flagged Official")

    return 0


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--download", action="store_true", help="read the flags from real Telegram")
    parser.add_argument("--import", dest="do_import", action="store_true", help="apply them to MongoDB")
    parser.add_argument("--dry-run", action="store_true", help="with --import, report without writing")
    args = parser.parse_args()

    if args.download:
        return asyncio.run(cmd_download())

    if args.do_import:
        return cmd_import(args.dry_run)

    parser.print_help()

    return 1


if __name__ == "__main__":
    sys.exit(main())
