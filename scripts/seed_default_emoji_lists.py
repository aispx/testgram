#!/usr/bin/env python3
"""
Seeds the three `emojiList` methods clients use to fill their custom-emoji pickers:

    account.getDefaultProfilePhotoEmojis   the emoji you can wear as a profile picture
    account.getDefaultGroupPhotoEmojis     the same for a group photo
    account.getDefaultBackgroundEmojis     the pattern behind an accent colour

All three answered with an empty `emojiList` here, which is why those pickers came up blank while
the rest of the emoji UI worked. The lists are curated by the server, not derivable from the
installed sets, so they are copied from Telegram verbatim: 206 / 208 / 30 document ids drawn from
41 sets, measured against the live service.

The referenced sets are mirrored **whole** rather than document-by-document, so that long-pressing a
tile and opening its pack shows the same pack Telegram shows. Two kinds of listed document are not
covered by that and are imported on their own, keeping the set reference Telegram gives them: those
belonging to a set Telegram itself refuses to serve (7173162320003085 answers STICKERSET_INVALID),
and those whose set is served but no longer lists them.

Every document is imported with `documentAttributeCustomEmoji.free = true` (see
seed_emoji_categories.import_icon_set). Telegram marks most of these `free = false`, i.e. usable
only by Premium accounts, and a client honours that by locking the tile - which would leave the
picker as unusable as an empty list on a server with no Premium.

Usage (both steps are idempotent, `--dry-run` previews the import):

    TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
      python3 scripts/seed_default_emoji_lists.py --download

    MONGO_URL=mongodb://172.23.0.8:27017 MINIO_ENDPOINT=172.23.0.10:9000 \\
    MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \\
      python3 scripts/seed_default_emoji_lists.py --import

`--download` talks to Telegram and writes bodies plus a manifest under ./default_emoji_lists;
`--import` needs only that directory, Mongo and MinIO.
"""
import asyncio
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import seed_emoji_categories as base

OUT_DIR = Path("default_emoji_lists")
MANIFEST_FILE = Path("default_emoji_lists_manifest.json")

# The collection the handlers read. One row per list, ids in the order Telegram served them.
COLLECTION = "default_emoji_lists"

LIST_KINDS = ("profile_photo", "group_photo", "background")


def list_requests():
    from telethon.tl import functions

    return {
        "profile_photo": functions.account.GetDefaultProfilePhotoEmojisRequest,
        "group_photo": functions.account.GetDefaultGroupPhotoEmojisRequest,
        "background": functions.account.GetDefaultBackgroundEmojisRequest,
    }


async def fetch_lists(client) -> Dict[str, List[int]]:
    lists: Dict[str, List[int]] = {}
    for kind, factory in list_requests().items():
        result = await client(factory(hash=0))
        ids = [base.positive_id(x) for x in (getattr(result, "document_id", None) or [])]
        lists[kind] = ids
        print(f"  {kind}: {len(ids)} document ids (hash={getattr(result, 'hash', 0)})")
    return lists


async def resolve_owning_sets(client, document_ids: List[int]) -> Tuple[Dict[int, Any], List[Any]]:
    """
    Maps every listed document to the set it belongs to. Returns the per-document owner and the
    de-duplicated set inputs, because a set is only worth downloading once.
    """
    from telethon.tl import functions, types

    owners: Dict[int, Any] = {}
    inputs: Dict[int, Any] = {}
    for start in range(0, len(document_ids), 100):
        chunk = document_ids[start:start + 100]
        for document in await client(functions.messages.GetCustomEmojiDocumentsRequest(
                document_id=chunk)):
            if not isinstance(document, types.Document):
                continue
            emoji = next((a for a in document.attributes
                          if isinstance(a, types.DocumentAttributeCustomEmoji)), None)
            stickerset = getattr(emoji, "stickerset", None)
            if not isinstance(stickerset, types.InputStickerSetID):
                continue
            owners[base.positive_id(document.id)] = stickerset
            inputs[stickerset.id] = stickerset

    print(f"  {len(owners)}/{len(document_ids)} documents resolved, "
          f"{len(inputs)} distinct sets")
    return owners, list(inputs.values())


async def fetch_orphan_document(client, document_id: int, stickerset) -> Optional[Dict[str, Any]]:
    """
    A listed document that no mirrored set contains: either its set is one Telegram itself refuses to
    serve, or the set is served but no longer lists the document. Only the document is downloaded, and
    the set reference is kept as Telegram gives it so the payload stays a faithful copy.
    """
    from telethon.tl import functions, types

    documents = await client(functions.messages.GetCustomEmojiDocumentsRequest(
        document_id=[document_id]))
    document = next((d for d in documents if isinstance(d, types.Document)), None)
    if document is None:
        return None

    output_dir = OUT_DIR / "_orphans"
    output_dir.mkdir(parents=True, exist_ok=True)
    doc_id = base.positive_id(document.id)

    path = output_dir / f"{doc_id}.tgs"
    if not path.exists():
        await client.download_media(document, file=str(path))

    thumb_files: Dict[str, str] = {}
    for thumb in getattr(document, "thumbs", None) or []:
        if not isinstance(thumb, (types.PhotoSize, types.PhotoSizeProgressive)):
            continue
        thumb_path = output_dir / f"{doc_id}_thumb_{thumb.type}.bin"
        if not thumb_path.exists():
            try:
                await client.download_media(document, file=str(thumb_path), thumb=thumb)
            except Exception as error:  # noqa: BLE001 - a missing thumb must not abort the run
                print(f"    WARNING: thumb {thumb.type} of {doc_id} failed: {error}")
                continue
        thumb_files[thumb.type] = str(thumb_path)

    emoji = next((a for a in document.attributes
                  if isinstance(a, types.DocumentAttributeCustomEmoji)), None)

    return {
        "doc_id": doc_id,
        "access_hash": base.to_int64(document.access_hash),
        "mime": document.mime_type,
        "size": document.size,
        "file": str(path),
        "alt": (emoji.alt if emoji else "") or "",
        "text_color": bool(getattr(emoji, "text_color", False)),
        "set_id": base.positive_id(stickerset.id),
        "set_access_hash": base.to_int64(stickerset.access_hash),
        "thumbs": base.serialize_thumbs(document),
        "thumb_files": thumb_files,
        "attributes": base.serialize_supporting_attributes(document),
    }


async def cmd_download() -> None:
    from telethon import TelegramClient

    if not base.TG_API_ID or not base.TG_API_HASH:
        print("ERROR: set TG_API_ID and TG_API_HASH")
        return

    # fetch_sticker_set writes under the module's own OUT_DIR; point it at ours so both steps of
    # this script agree on where the bodies live.
    base.OUT_DIR = OUT_DIR
    OUT_DIR.mkdir(parents=True, exist_ok=True)

    client = TelegramClient(base.TG_SESSION, base.TG_API_ID, base.TG_API_HASH)
    await client.start()

    print("=== lists ===")
    lists = await fetch_lists(client)
    every_id = sorted({doc_id for ids in lists.values() for doc_id in ids})
    print(f"  {len(every_id)} distinct documents referenced")

    print("=== owning sets ===")
    owners, set_inputs = await resolve_owning_sets(client, every_id)

    print("=== sets ===")
    sets: List[Dict[str, Any]] = []
    for stickerset in set_inputs:
        try:
            payload = await base.fetch_sticker_set(client, stickerset)
        except Exception as error:  # noqa: BLE001 - one hidden set must not abort the run
            print(f"  set {stickerset.id}: not served by Telegram ({error}); "
                  f"its documents will be imported on their own")
            continue
        if payload is not None:
            sets.append(payload)

    print("=== documents no mirrored set contains ===")
    mirrored = {document["doc_id"] for payload in sets for document in payload["documents"]}
    orphans: List[Dict[str, Any]] = []
    for doc_id in every_id:
        if doc_id in mirrored:
            continue
        stickerset = owners.get(doc_id)
        if stickerset is None:
            print(f"  {doc_id}: no owning set, skipped")
            continue
        document = await fetch_orphan_document(client, doc_id, stickerset)
        if document is not None:
            orphans.append(document)
    print(f"  {len(orphans)} documents downloaded on their own")

    MANIFEST_FILE.write_text(json.dumps({
        "lists": {kind: ids for kind, ids in lists.items()},
        "sets": sets,
        "orphans": orphans,
    }, ensure_ascii=False))
    print(f"Wrote {MANIFEST_FILE}")

    await client.disconnect()


def import_orphans(db, minio, orphans: List[Dict[str, Any]], dry_run: bool) -> int:
    """
    Writes documents whose set cannot be mirrored. The stickerset reference is copied verbatim, so a
    client that tries to open the pack gets the same STICKERSET_INVALID Telegram gives it.
    """
    doc_col = db["eventflow-documentreadmodel"]
    written = bodies = thumbs = 0

    for document in orphans:
        doc_id = document["doc_id"]
        if base.upload_body(minio, doc_id, Path(document["file"]), document["mime"], dry_run):
            bodies += 1
        thumbs += base.upload_thumbs(minio, doc_id, document["thumb_files"], dry_run)

        primary = {
            "_t": "TDocumentAttributeCustomEmoji",
            "Free": True,
            "TextColor": document["text_color"],
            "Alt": document["alt"],
            # "_id", not "Id": the C# driver reads a nested TL object's Id member from the _id element.
            "Stickerset": {"_t": "TInputStickerSetID",
                           "_id": document["set_id"],
                           "AccessHash": document["set_access_hash"] & 0x7FFFFFFFFFFFFFFF},
        }

        row = {
            "_id": f"documentreadmodel-{doc_id}",
            "Id": f"documentreadmodel-{doc_id}",
            "DocumentId": doc_id,
            "AccessHash": document["access_hash"] & 0x7FFFFFFFFFFFFFFF,
            "FileReference": list(os.urandom(16)),
            "Date": int(time.time()),
            "DcId": base.DC_ID,
            "MimeType": document["mime"],
            "Name": Path(document["file"]).name,
            "Size": document["size"],
            "Thumbs": document["thumbs"] or None,
            "VideoThumbs": None,
            "Attributes": None,
            "Attributes2": [primary, *document["attributes"]],
            "CreatorId": None,
            "Fingerprint": None,
            "Md5CheckSum": None,
            "ThumbId": None,
            "VideoThumbId": None,
            "Version": 1,
        }

        written += 1
        if dry_run:
            continue

        existing = doc_col.find_one({"DocumentId": doc_id}, {"FileReference": 1})
        if existing is not None:
            row["FileReference"] = existing.get("FileReference") or row["FileReference"]
        doc_col.replace_one({"DocumentId": doc_id}, row, upsert=True)

    verb = "would write" if dry_run else "wrote"
    print(f"  {verb} {written} standalone documents, {bodies} bodies, {thumbs} thumbs")
    return written


def import_lists(db, lists: Dict[str, List[int]], dry_run: bool, pending: set) -> None:
    """
    Stores the three lists. An id whose document is not present is dropped: a client that receives an
    id `messages.getCustomEmojiDocuments` cannot resolve draws a blank tile in the picker.
    """
    doc_col = db["eventflow-documentreadmodel"]
    list_col = db[COLLECTION]

    for kind in LIST_KINDS:
        ids = lists.get(kind) or []
        kept: List[int] = []
        for doc_id in ids:
            if doc_id in pending or doc_col.count_documents({"DocumentId": doc_id}, limit=1):
                kept.append(doc_id)

        dropped = len(ids) - len(kept)
        verb = "would store" if dry_run else "stored"
        print(f"  {verb} {kind}: {len(kept)} ids"
              + (f" ({dropped} dropped, document missing)" if dropped else ""))

        if dry_run:
            continue

        list_col.replace_one({"_id": kind}, {
            "_id": kind,
            "For": kind,
            "DocumentIds": kept,
            "Version": 1,
        }, upsert=True)


def cmd_import(dry_run: bool) -> None:
    if not MANIFEST_FILE.exists():
        raise SystemExit(f"{MANIFEST_FILE} not found; run --download first")

    payload = json.loads(MANIFEST_FILE.read_text())
    db, minio = base.connect_storage()

    pending = set()
    print("=== sets ===")
    for sticker_set in payload["sets"]:
        base.import_icon_set(db, minio, sticker_set, dry_run)
        pending.update(document["doc_id"] for document in sticker_set["documents"])

    print("=== standalone documents ===")
    import_orphans(db, minio, payload.get("orphans") or [], dry_run)
    pending.update(document["doc_id"] for document in (payload.get("orphans") or []))

    print("=== lists ===")
    import_lists(db, payload["lists"], dry_run, pending)
    print("Dry run, nothing written." if dry_run else "Done.")


if __name__ == "__main__":
    dry = "--dry-run" in sys.argv
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import(dry)
    else:
        print(__doc__)
