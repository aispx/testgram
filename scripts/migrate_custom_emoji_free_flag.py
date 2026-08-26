#!/usr/bin/env python3
"""
Marks every stored custom-emoji document as `documentAttributeCustomEmoji.free = true`.

A client honours `free` by locking the tile behind Premium, and Android marks the *whole* stickerset
premium as soon as one of its documents is not free (`MessageObject.isPremiumEmojiPack` ->
`isFreeEmoji`). Telegram serves `free = false` for its Premium-only emoji, and seed_stickers.py used
to copy that through, so on this deployment `StatusPack` (11 documents) and `Topics` (112) came out
locked for every account without Premium.

This is the same decision already made in seed_emoji_categories.py and seed_default_emoji_lists.py,
and now in seed_stickers.py; this script repairs rows written before that.

Usage:
  MONGO_URL=mongodb://172.23.0.8:27017 python3 migrate_custom_emoji_free_flag.py --dry-run
  MONGO_URL=mongodb://172.23.0.8:27017 python3 migrate_custom_emoji_free_flag.py

Idempotent: re-running matches nothing.
"""
import os
import sys

from pymongo import MongoClient

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")
COLLECTION = "eventflow-documentreadmodel"
SET_COLLECTION = "eventflow-stickersetreadmodel"


def main(dry_run: bool) -> None:
    database = MongoClient(MONGO_URL)[MONGO_DB]
    documents = database[COLLECTION]

    not_free = {"Attributes2": {"$elemMatch": {"_t": {"$regex": "CustomEmoji"}, "Free": False}}}
    affected = list(documents.find(not_free, {"DocumentId": 1}))
    if not affected:
        print("Nothing to do: no custom-emoji document carries free = false")
        return

    document_ids = [row["DocumentId"] for row in affected]
    sets = database[SET_COLLECTION].find({"DocumentIds": {"$in": document_ids}}, {"ShortName": 1})
    print(f"{len(document_ids)} documents with free = false, in sets: "
          f"{', '.join(sorted(row.get('ShortName') or '?' for row in sets))}")

    if dry_run:
        print("--dry-run: nothing written")
        return

    result = documents.update_many(
        not_free,
        {"$set": {"Attributes2.$[attribute].Free": True}},
        array_filters=[{"attribute._t": {"$regex": "CustomEmoji"}}])
    print(f"Updated {result.modified_count} documents")

    remaining = documents.count_documents(not_free)
    print(f"Remaining with free = false: {remaining}")
    if remaining:
        sys.exit("Some documents were not updated")


if __name__ == "__main__":
    main("--dry-run" in sys.argv)
