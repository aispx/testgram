#!/usr/bin/env python3
"""Move installed sticker sets out of the EventFlow read model into their own collection.

`eventflow-userinstalledstickersetreadmodel` was written to directly by the sticker handlers, which the
project forbids for `eventflow-*` collections — those belong to their aggregates. The per-user list of
installed sets has no aggregate behind it and no invariants worth one, so it now lives in
`installed_sticker_sets`, alongside `saved_gifs`, `recent_stickers` and `faved_stickers`.

This script is idempotent: re-running it re-derives the same rows.

Usage:
    MONGO_URL=mongodb://localhost:27017 python3 migrate_installed_sticker_sets.py --dry-run
    MONGO_URL=mongodb://localhost:27017 python3 migrate_installed_sticker_sets.py
"""
import argparse
import os
import sys

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")

LEGACY_COLLECTION = "eventflow-userinstalledstickersetreadmodel"
TARGET_COLLECTION = "installed_sticker_sets"
CATALOGUE_COLLECTION = "eventflow-stickersetreadmodel"


def to_int(value, default=0):
    if isinstance(value, bool):
        return default
    if isinstance(value, (int, float)):
        return int(value)
    if isinstance(value, str):
        try:
            return int(value)
        except ValueError:
            return default
    return default


def sticker_set_type(catalogue_row):
    """The three panels a client keeps separate. Derived from the catalogue flags, because the legacy rows
    never recorded which kind of set they were — that is the field whose absence made getMaskStickers and
    getEmojiStickers unable to answer at all."""
    if not catalogue_row:
        return "Regular"
    if catalogue_row.get("Emojis"):
        return "CustomEmoji"
    if catalogue_row.get("Masks"):
        return "Mask"
    return "Regular"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dry-run", action="store_true", help="report what would be written")
    parser.add_argument("--drop-legacy", action="store_true",
                        help="delete the legacy collection once the rows are migrated")
    args = parser.parse_args()

    try:
        from pymongo import MongoClient, UpdateOne
    except ImportError:
        print("ERROR: pip install pymongo", file=sys.stderr)
        return 1

    db = MongoClient(MONGO_URL)[MONGO_DB]
    legacy_rows = list(db[LEGACY_COLLECTION].find({}))
    print(f"Found {len(legacy_rows)} legacy rows in {LEGACY_COLLECTION}")

    if not legacy_rows:
        return 0

    set_ids = {to_int(row.get("StickerSetId")) for row in legacy_rows}
    catalogue = {
        to_int(row.get("StickerSetId")): row
        for row in db[CATALOGUE_COLLECTION].find({"StickerSetId": {"$in": sorted(set_ids)}})
    }

    # Order is what clients render and hash by, so it has to be a total order per user. The legacy rows
    # only carry an install timestamp; ranking by it preserves the order the user actually saw.
    by_user = {}
    for row in legacy_rows:
        by_user.setdefault(to_int(row.get("UserId")), []).append(row)

    writes = []
    for user_id, rows in by_user.items():
        rows.sort(key=lambda r: to_int(r.get("InstalledAt")))
        for order, row in enumerate(rows, start=1):
            set_id = to_int(row.get("StickerSetId"))
            if not user_id or not set_id:
                continue

            writes.append(UpdateOne(
                {"_id": f"{user_id}:{set_id}"},
                {"$set": {
                    "UserId": user_id,
                    "StickerSetId": set_id,
                    "StickerSetType": sticker_set_type(catalogue.get(set_id)),
                    "Archived": bool(row.get("Archived", False)),
                    "Order": order,
                    "Date": to_int(row.get("InstalledAt")),
                }},
                upsert=True,
            ))

    print(f"Would write {len(writes)} rows to {TARGET_COLLECTION}"
          if args.dry_run else f"Writing {len(writes)} rows to {TARGET_COLLECTION}")

    if args.dry_run:
        for write in writes[:10]:
            print(f"  {write._filter['_id']} -> {write._doc['$set']}")
        if len(writes) > 10:
            print(f"  ... and {len(writes) - 10} more")
        return 0

    if writes:
        result = db[TARGET_COLLECTION].bulk_write(writes, ordered=False)
        print(f"Upserted {result.upserted_count}, modified {result.modified_count}")

    # The counter the store uses for new installs must start above every migrated Order, or the next
    # install would land in the middle of the list instead of at the top.
    for user_id, rows in by_user.items():
        if not user_id:
            continue
        db["counters"].update_one(
            {"_id": f"installed_sticker_sets_order_{user_id}"},
            {"$max": {"seq": len(rows)}},
            upsert=True,
        )

    if args.drop_legacy:
        db[LEGACY_COLLECTION].drop()
        print(f"Dropped {LEGACY_COLLECTION}")
    else:
        print(f"Left {LEGACY_COLLECTION} in place; re-run with --drop-legacy once verified")

    return 0


if __name__ == "__main__":
    sys.exit(main())
