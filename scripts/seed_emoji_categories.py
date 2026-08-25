#!/usr/bin/env python3
"""
Seeds the emoji-category taxonomy and its icon set from the official Telegram servers.

Three things are wrong without this, all of them visible in the client:

  * The category bar above sticker/emoji/GIF search draws its icons from custom-emoji documents
    (`emojiGroup.icon_emoji_id`). Official Telegram serves all of them from one dedicated set,
    `EmojiCategories`. Without that set the icons have to be borrowed from whatever custom emoji
    happens to be seeded, which is why "Travel & Places" showed an aeroplane and
    "Smileys & People" a clown.

  * Our category list was the emoji *keyboard* taxonomy (Smileys & People, Animals & Nature, ...).
    Official Telegram serves a *search* taxonomy for messages.getEmojiGroups - Love, Approval,
    Disapproval, Cheers, Laughter, ... - and a different one again per method. This copies each of
    the four verbatim.

  * `documentAttributeSticker.alt` lost the U+FE0F variation selector on 69 of the 599 documents in
    AnimatedEmojies, because the seeder derived alt from the pack emoticon (where Telegram itself
    does not carry FE0F) instead of from the document. Android strips FE0F on both sides so it
    copes, but tdlib-based clients compare the raw string.

Usage:
  1. Download from Telegram (needs an account; reuse an existing Telethon session to skip login):
       TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
       python3 seed_emoji_categories.py --download

  2. Import into the server:
       MONGO_URL=mongodb://172.23.0.8:27017 \\
       MINIO_ENDPOINT=172.23.0.10:9000 \\
       MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \\
       python3 seed_emoji_categories.py --import

  3. Repair the alt values (same credentials as --import):
       python3 seed_emoji_categories.py --fix-alts

  --dry-run may be added to --import and --fix-alts to print what would change and write nothing.

Both steps are idempotent: re-running replaces the category documents and re-uploads only bodies
missing from the object store.
"""
import asyncio
import io
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_SESSION = os.environ.get("TG_SESSION", "emoji_categories_seeder")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = os.environ.get("MINIO_BUCKET", "tg-files")

# Bodies the server stores itself are unencrypted and live on the media DC, like the sticker files
# the rest of the seeder writes.
DC_ID = 1

OUT_DIR = Path("emoji_categories")
MANIFEST_FILE = Path("emoji_categories_manifest.json")
ALT_MANIFEST_FILE = Path("emoji_alts_manifest.json")

async def fetch_category_icon_set(client) -> Optional[Dict[str, Any]]:
    """
    Downloads the set the category icons live in. It is not reachable through a dedicated
    InputStickerSet constructor, so it is discovered the way a client does: ask for the emoji groups,
    then resolve one of the icon documents to learn which set it belongs to.
    """
    from telethon.tl import functions, types

    groups = await client(functions.messages.GetEmojiGroupsRequest(hash=0))
    icon_ids = sorted({group.icon_emoji_id for group in groups.groups})
    if not icon_ids:
        print("ERROR: the server returned no emoji groups, so the icon set cannot be located")
        return None

    documents = await client(functions.messages.GetCustomEmojiDocumentsRequest(
        document_id=icon_ids[:1]))
    if not documents:
        print("ERROR: the category icon document could not be resolved")
        return None

    stickerset = None
    for attribute in documents[0].attributes:
        if isinstance(attribute, types.DocumentAttributeCustomEmoji):
            stickerset = attribute.stickerset
            break

    if not isinstance(stickerset, types.InputStickerSetID):
        print("ERROR: the icon document does not reference a resolvable stickerset")
        return None

    print(f"Category icons live in set {stickerset.id}")
    return await fetch_sticker_set(client, stickerset)


async def fetch_sticker_set(client, input_set) -> Optional[Dict[str, Any]]:
    """Downloads a whole set: every body, every thumbnail, and the metadata to describe them."""
    from telethon.tl import functions, types

    result = await client(functions.messages.GetStickerSetRequest(stickerset=input_set, hash=0))
    short_name = result.set.short_name
    output_dir = OUT_DIR / short_name
    output_dir.mkdir(parents=True, exist_ok=True)

    documents = []
    for document in result.documents:
        doc_id = positive_id(document.id)
        alt = ""
        for attribute in document.attributes:
            if isinstance(attribute, (types.DocumentAttributeCustomEmoji,
                                      types.DocumentAttributeSticker)):
                alt = attribute.alt or ""
                break

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
                    print(f"  WARNING: thumb {thumb.type} of {doc_id} failed: {error}")
                    continue
            thumb_files[thumb.type] = str(thumb_path)

        documents.append({
            "doc_id": doc_id,
            "access_hash": to_int64(document.access_hash),
            "mime": document.mime_type,
            "size": document.size,
            "file": str(path),
            "alt": alt,
            "thumbs": serialize_thumbs(document),
            "thumb_files": thumb_files,
            "attributes": serialize_supporting_attributes(document),
        })

    print(f"  {short_name}: {len(documents)} documents")

    return {
        "set_id": positive_id(result.set.id),
        "access_hash": to_int64(result.set.access_hash),
        "short_name": short_name,
        "title": result.set.title,
        "count": result.set.count,
        "emojis": bool(result.set.emojis),
        "text_color": bool(result.set.text_color),
        "channel_emoji_status": bool(getattr(result.set, "channel_emoji_status", False)),
        "packs": [{"emoticon": pack.emoticon,
                   "documents": [positive_id(x) for x in pack.documents]}
                  for pack in result.packs],
        "documents": documents,
    }


async def fetch_groups(client) -> Dict[str, List[Dict[str, Any]]]:
    """
    The four category taxonomies, one per method. They are genuinely different lists - the status
    picker offers "DND" and "Sleep", the profile-photo picker "Faces" and "Flags" - so each is
    copied as served rather than derived from one another.
    """
    from telethon.tl import functions, types

    requests = {
        "default": functions.messages.GetEmojiGroupsRequest(hash=0),
        "stickers": functions.messages.GetEmojiStickerGroupsRequest(hash=0),
        "status": functions.messages.GetEmojiStatusGroupsRequest(hash=0),
        "profile_photo": functions.messages.GetEmojiProfilePhotoGroupsRequest(hash=0),
    }

    taxonomies: Dict[str, List[Dict[str, Any]]] = {}
    for name, request in requests.items():
        result = await client(request)
        groups = []
        for group in result.groups:
            if isinstance(group, types.EmojiGroupPremium):
                kind = "premium"
            elif isinstance(group, types.EmojiGroupGreeting):
                kind = "greeting"
            else:
                kind = "default"
            groups.append({
                "kind": kind,
                "title": group.title,
                "icon_emoji_id": positive_id(group.icon_emoji_id),
                # emojiGroupPremium carries no emoticons: clients select Premium content instead.
                "emoticons": list(getattr(group, "emoticons", []) or []),
            })
        taxonomies[name] = groups
        print(f"  {name}: {len(groups)} categories")

    return taxonomies


async def cmd_download() -> None:
    from telethon import TelegramClient

    if not TG_API_ID or not TG_API_HASH:
        print("ERROR: set TG_API_ID and TG_API_HASH")
        return

    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.connect()
    if not await client.is_user_authorized():
        print(f"ERROR: session {TG_SESSION} is not authorized; log it in first")
        await client.disconnect()
        return

    print("=== emoji category taxonomies ===")
    taxonomies = await fetch_groups(client)

    print("=== category icon set ===")
    icon_set = await fetch_category_icon_set(client)
    if icon_set is None:
        await client.disconnect()
        return

    MANIFEST_FILE.write_text(json.dumps({"icon_set": icon_set, "groups": taxonomies},
                                        ensure_ascii=False, indent=1))
    print(f"Wrote {MANIFEST_FILE}")

    print("=== alt values ===")
    alts = await fetch_alts(client)
    ALT_MANIFEST_FILE.write_text(json.dumps(alts, ensure_ascii=False, indent=1))
    print(f"Wrote {ALT_MANIFEST_FILE} ({len(alts)} documents)")

    await client.disconnect()


async def fetch_alts(client) -> Dict[str, str]:
    """
    The authoritative alt of every document in the sets the emoji panel looks up by emoji. Read from
    the document's own attribute, not from the pack emoticon: Telegram's packs carry no U+FE0F while
    the documents do, and it was deriving alt from the pack that dropped it for 69 documents.
    """
    from telethon.tl import functions, types

    constructors = {
        "AnimatedEmojies": types.InputStickerSetAnimatedEmoji(),
        "EmojiAnimations": types.InputStickerSetAnimatedEmojiAnimations(),
        "StatusPack": types.InputStickerSetEmojiDefaultStatuses(),
        "Topics": types.InputStickerSetEmojiDefaultTopicIcons(),
        "EmojiGenericAnimations": types.InputStickerSetEmojiGenericAnimations(),
        "GiftsPremium": types.InputStickerSetPremiumGifts(),
    }

    alts: Dict[str, str] = {}
    for name, input_set in constructors.items():
        try:
            result = await client(functions.messages.GetStickerSetRequest(stickerset=input_set,
                                                                         hash=0))
        except Exception as error:  # noqa: BLE001 - one unavailable set must not abort the rest
            print(f"  WARNING: {name} unavailable: {error}")
            continue

        found = 0
        for document in result.documents:
            for attribute in document.attributes:
                if isinstance(attribute, (types.DocumentAttributeCustomEmoji,
                                          types.DocumentAttributeSticker)):
                    if attribute.alt:
                        alts[str(positive_id(document.id))] = attribute.alt
                        found += 1
                    break
        print(f"  {name}: {found} alts")

    return alts


def connect_storage():
    import pymongo
    from minio import Minio

    if not MINIO_ACCESS_KEY or not MINIO_SECRET_KEY:
        raise SystemExit("Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY")

    minio = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY, secret_key=MINIO_SECRET_KEY,
                  secure=False)
    if not minio.bucket_exists(MINIO_BUCKET):
        minio.make_bucket(MINIO_BUCKET)
        print(f"Created bucket {MINIO_BUCKET}")

    return pymongo.MongoClient(MONGO_URL)[MONGO_DB], minio


def upload_body(minio, doc_id: int, path: Path, mime: str, dry_run: bool) -> bool:
    """Uploads a body only when the object store does not already have it."""
    try:
        minio.stat_object(MINIO_BUCKET, str(doc_id))
        return False
    except Exception:  # noqa: BLE001 - the SDK raises for "no such object"
        pass

    if not path.exists():
        print(f"  WARNING: body for {doc_id} not downloaded ({path})")
        return False
    if dry_run:
        print(f"  would upload body {doc_id} ({path.stat().st_size} bytes)")
        return True

    data = path.read_bytes()
    minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data),
                     content_type=mime)
    return True


def upload_thumbs(minio, doc_id: int, thumb_files: Dict[str, str], dry_run: bool) -> int:
    """Thumbnails are served as `{fileId}_{sizeType}`, which is what document.thumbs points at."""
    uploaded = 0
    for thumb_type, file_path in (thumb_files or {}).items():
        path = Path(file_path)
        if not path.exists():
            continue
        object_name = f"{doc_id}_{thumb_type}"
        try:
            minio.stat_object(MINIO_BUCKET, object_name)
            continue
        except Exception:  # noqa: BLE001
            pass
        if dry_run:
            print(f"  would upload thumb {object_name}")
            uploaded += 1
            continue
        data = path.read_bytes()
        minio.put_object(MINIO_BUCKET, object_name, io.BytesIO(data), length=len(data))
        uploaded += 1
    return uploaded


def import_icon_set(db, minio, icon_set: Dict[str, Any], dry_run: bool) -> None:
    """
    Writes the icon set's documents and its stickerset row. The documents must carry
    `documentAttributeCustomEmoji`, because `emojiGroup.icon_emoji_id` is resolved through
    `messages.getCustomEmojiDocuments`, which only answers for custom emoji.
    """
    doc_col = db["eventflow-documentreadmodel"]
    set_col = db["eventflow-stickersetreadmodel"]

    set_id = icon_set["set_id"]
    access_hash = icon_set["access_hash"] & 0x7FFFFFFFFFFFFFFF
    doc_ids: List[int] = []
    bodies = thumbs = written = 0

    for document in icon_set["documents"]:
        doc_id = document["doc_id"]
        doc_ids.append(doc_id)

        if upload_body(minio, doc_id, Path(document["file"]), document["mime"], dry_run):
            bodies += 1
        thumbs += upload_thumbs(minio, doc_id, document["thumb_files"], dry_run)

        # text_color comes from the set: these icons are monochrome and clients recolour them to the
        # theme, which is what makes the category bar legible in both light and dark mode.
        primary = {
            "_t": "TDocumentAttributeCustomEmoji",
            "Free": True,
            "TextColor": icon_set["text_color"],
            "Alt": document["alt"],
            "Stickerset": {"_t": "TInputStickerSetID", "Id": set_id, "AccessHash": access_hash},
        }
        attributes = [primary, *document["attributes"]]

        row = {
            "_id": f"documentreadmodel-{doc_id}",
            "Id": f"documentreadmodel-{doc_id}",
            "DocumentId": doc_id,
            "AccessHash": document["access_hash"] & 0x7FFFFFFFFFFFFFFF,
            # Non-empty on purpose: a client that receives an empty file_reference treats the
            # document as stale and tries to refresh it before downloading anything.
            "FileReference": list(os.urandom(16)),
            "Date": int(time.time()),
            "DcId": DC_ID,
            "MimeType": document["mime"],
            "Name": Path(document["file"]).name,
            "Size": document["size"],
            "Thumbs": document["thumbs"] or None,
            "VideoThumbs": None,
            "Attributes": None,
            "Attributes2": attributes,
            "CreatorId": None,
            "Fingerprint": None,
            "Md5CheckSum": None,
            "ThumbId": None,
            "VideoThumbId": None,
            "Version": 1,
        }

        if dry_run:
            written += 1
            continue

        existing = doc_col.find_one({"DocumentId": doc_id}, {"FileReference": 1})
        if existing is not None:
            # Keep the reference a client may already hold rather than invalidating its cache.
            row["FileReference"] = existing.get("FileReference") or row["FileReference"]
        doc_col.replace_one({"DocumentId": doc_id}, row, upsert=True)
        written += 1

    set_row = {
        "_id": f"stickersetreadmodel-{set_id}",
        "StickerSetId": set_id,
        "AccessHash": access_hash,
        "ShortName": icon_set["short_name"],
        "Title": icon_set["title"],
        "Slug": icon_set["short_name"],
        "Count": len(doc_ids),
        "DocumentIds": doc_ids,
        "Packs": [{"Emoticon": pack["emoticon"], "Documents": pack["documents"]}
                  for pack in icon_set["packs"]],
        "Keywords": [],
        "Emojis": icon_set["emojis"],
        "TextColor": icon_set["text_color"],
        "ChannelEmojiStatus": icon_set["channel_emoji_status"],
        "Version": 1,
    }

    if not dry_run:
        set_col.replace_one({"_id": set_row["_id"]}, set_row, upsert=True)

    verb = "would write" if dry_run else "wrote"
    print(f"  {verb} set {icon_set['short_name']} ({set_id}): {written} documents, "
          f"{bodies} bodies uploaded, {thumbs} thumbs uploaded")


def import_groups(db, groups: Dict[str, List[Dict[str, Any]]], dry_run: bool,
                  pending_icon_ids: Optional[set] = None) -> None:
    """
    Replaces the whole taxonomy. Categories are written in the order Telegram served them, and Order
    carries that order because EmojiGroupsAppService sorts on it - the client shows the bar in
    exactly this sequence.

    A category whose icon document is missing is dropped: TDLib discards any category whose icon
    cannot be resolved (EmojiGroupList::get_emoji_categories_object), so keeping it would leave
    iOS/Desktop with a category that silently vanishes, and Android with a blank tile.

    `pending_icon_ids` are the icons the same run is importing. Without them a --dry-run would report
    every category as dropped, because the icon documents are only written on a real run.
    """
    doc_col = db["eventflow-documentreadmodel"]
    group_col = db["emoji_groups"]
    pending = pending_icon_ids or set()

    documents: List[Dict[str, Any]] = []
    dropped: List[str] = []

    for group_for, categories in groups.items():
        order = 1
        for category in categories:
            icon_id = category["icon_emoji_id"]
            if (icon_id and icon_id not in pending
                    and doc_col.count_documents({"DocumentId": icon_id}, limit=1) == 0):
                dropped.append(f"{group_for}/{category['title']} (icon {icon_id} missing)")
                continue

            slug = category["title"].lower()
            for old, new in ((" & ", "-"), (" / ", "-"), (" ", "-")):
                slug = slug.replace(old, new)

            documents.append({
                "_id": f"emoji-group-{group_for.replace('_', '-')}-{slug}",
                "For": group_for,
                "Kind": category["kind"],
                "Title": category["title"],
                "IconEmojiId": icon_id,
                "Emoticons": category["emoticons"],
                "Order": order,
                "Version": 1,
            })
            order += 1

    for entry in dropped:
        print(f"  WARNING: dropped category {entry}")

    if dry_run:
        existing = group_col.count_documents({})
        print(f"  would replace {existing} category documents with {len(documents)}")
        for document in documents:
            print(f"    {document['For']:14} {document['Kind']:8} {document['Title']!r} "
                  f"icon={document['IconEmojiId']} emoticons={len(document['Emoticons'])}")
        return

    group_col.delete_many({})
    if documents:
        group_col.insert_many(documents)
    print(f"  wrote {len(documents)} category documents")


def cmd_import(dry_run: bool) -> None:
    if not MANIFEST_FILE.exists():
        raise SystemExit(f"{MANIFEST_FILE} not found; run --download first")

    payload = json.loads(MANIFEST_FILE.read_text())
    db, minio = connect_storage()

    print("=== icon set ===")
    import_icon_set(db, minio, payload["icon_set"], dry_run)
    print("=== categories ===")
    import_groups(db, payload["groups"], dry_run,
                  {document["doc_id"] for document in payload["icon_set"]["documents"]})
    print("Done." if not dry_run else "Dry run, nothing written.")


def cmd_fix_alts(dry_run: bool) -> None:
    """
    Rewrites `documentAttributeSticker.alt` / `documentAttributeCustomEmoji.alt` to the value
    Telegram itself serves. Only the alt is touched; every other attribute and field is left alone.
    """
    if not ALT_MANIFEST_FILE.exists():
        raise SystemExit(f"{ALT_MANIFEST_FILE} not found; run --download first")

    alts: Dict[str, str] = json.loads(ALT_MANIFEST_FILE.read_text())
    db, _ = connect_storage()
    doc_col = db["eventflow-documentreadmodel"]

    changed = missing = unchanged = 0
    for raw_id, alt in alts.items():
        doc_id = int(raw_id)
        document = doc_col.find_one({"DocumentId": doc_id}, {"Attributes2": 1})
        if document is None:
            missing += 1
            continue

        attributes = document.get("Attributes2") or []
        updated = False
        for attribute in attributes:
            if not isinstance(attribute, dict):
                continue
            if attribute.get("_t") not in ("TDocumentAttributeSticker",
                                           "TDocumentAttributeCustomEmoji"):
                continue
            if attribute.get("Alt") != alt:
                attribute["Alt"] = alt
                updated = True

        if not updated:
            unchanged += 1
            continue

        changed += 1
        if dry_run:
            print(f"  would set alt of {doc_id} to {alt!r}")
            continue
        doc_col.update_one({"DocumentId": doc_id}, {"$set": {"Attributes2": attributes}})

    verb = "would correct" if dry_run else "corrected"
    print(f"{verb} {changed} alts; {unchanged} already correct, {missing} not seeded here")


def to_int64(value: Any) -> int:
    if isinstance(value, dict):
        raw = (value.get("high", 0) << 32) | (value.get("low", 0) & 0xFFFFFFFF)
        return raw - (1 << 64) if raw >= (1 << 63) else raw
    return int(value)


def positive_id(value: Any) -> int:
    """Document ids are stored unsigned in the read model, as the rest of the seeder does."""
    return to_int64(value) & 0x7FFFFFFFFFFFFFFF


def serialize_thumbs(document) -> List[Dict[str, Any]]:
    """The BSON shape MyTelegram's DocumentReadModel expects for document.thumbs."""
    from telethon.tl.types import (
        PhotoCachedSize, PhotoPathSize, PhotoSize, PhotoSizeEmpty, PhotoSizeProgressive,
        PhotoStrippedSize,
    )

    serialized: List[Dict[str, Any]] = []
    for thumb in getattr(document, "thumbs", None) or []:
        if isinstance(thumb, PhotoSize):
            serialized.append({"_t": "TPhotoSize", "Type": thumb.type, "W": thumb.w, "H": thumb.h,
                               "Size": thumb.size})
        elif isinstance(thumb, PhotoCachedSize):
            serialized.append({"_t": "TPhotoCachedSize", "Type": thumb.type, "W": thumb.w,
                               "H": thumb.h, "Bytes": list(thumb.bytes)})
        elif isinstance(thumb, PhotoSizeProgressive):
            serialized.append({"_t": "TPhotoSizeProgressive", "Type": thumb.type, "W": thumb.w,
                               "H": thumb.h, "Sizes": list(thumb.sizes)})
        elif isinstance(thumb, PhotoStrippedSize):
            serialized.append({"_t": "TPhotoStrippedSize", "Type": thumb.type,
                               "Bytes": list(thumb.bytes)})
        elif isinstance(thumb, PhotoPathSize):
            serialized.append({"_t": "TPhotoPathSize", "Type": thumb.type,
                               "Bytes": list(thumb.bytes)})
        elif isinstance(thumb, PhotoSizeEmpty):
            serialized.append({"_t": "TPhotoSizeEmpty", "Type": thumb.type})
    return serialized


def serialize_supporting_attributes(document) -> List[Dict[str, Any]]:
    """
    Everything except the sticker/custom-emoji attribute, which the importer rebuilds so it carries
    this server's own stickerset reference.
    """
    from telethon.tl.types import DocumentAttributeFilename, DocumentAttributeImageSize

    attributes: List[Dict[str, Any]] = []
    for attribute in document.attributes:
        if isinstance(attribute, DocumentAttributeImageSize):
            attributes.append({"_t": "TDocumentAttributeImageSize", "W": attribute.w,
                               "H": attribute.h})
        elif isinstance(attribute, DocumentAttributeFilename):
            attributes.append({"_t": "TDocumentAttributeFilename",
                               "FileName": attribute.file_name})
    return attributes


if __name__ == "__main__":
    dry = "--dry-run" in sys.argv
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import(dry)
    elif "--fix-alts" in sys.argv:
        cmd_fix_alts(dry)
    else:
        print(__doc__)
