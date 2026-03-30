#!/usr/bin/env python3
"""
Sticker-set seeder for MyTelegram server.

Usage:
  1. Download ALL sticker files from Telegram:
       TG_API_ID=... TG_API_HASH=... TG_PHONE=... python3 seed_stickers.py --download

  2. Import downloaded files into the server:
       MONGO_URL=mongodb://localhost:27017 \
       MINIO_ENDPOINT=localhost:9000 \
       MINIO_ACCESS_KEY=... \
       MINIO_SECRET_KEY=... \
       python3 seed_stickers.py --import
"""
import asyncio
import io
import json
import os
import struct
import time
from pathlib import Path
from typing import List, Dict, Any, Optional

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_PHONE = os.environ.get("TG_PHONE", "")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = "tg-files"
DC_ID = 1

OUT_DIR = Path("stickers")
MANIFEST_FILE = Path("stickers_manifest.json")

MIME_TO_EXT = {
    "application/x-tgsticker": "tgs",
    "video/webm": "webm",
    "image/webp": "webp",
    "image/png": "png",
    "image/gif": "gif",
}


def get_file_ext(mime_type: str, file_name: str = "") -> str:
    if mime_type in MIME_TO_EXT:
        return MIME_TO_EXT[mime_type]
    if file_name:
        ext = Path(file_name).suffix.lower().lstrip(".")
        if ext in ["tgs", "webm", "webp", "png", "gif"]:
            return ext
    return "bin"


async def cmd_download():
    from telethon import TelegramClient
    from telethon.tl import functions, types

    if not TG_API_ID or not TG_API_HASH or not TG_PHONE:
        print("ERROR: Set TG_API_ID, TG_API_HASH, and TG_PHONE environment variables")
        return

    client = TelegramClient("sticker_seeder", TG_API_ID, TG_API_HASH)
    await client.start()
    await client.sign_in(TG_PHONE)

    manifest = []
    downloaded_count = 0
    skipped_count = 0

    # Get featured stickers first
    print("\n=== Fetching featured sticker sets ===")
    try:
        featured_result = await client(functions.messages.GetFeaturedStickersRequest(hash=0))
        print(f"Found {len(featured_result.sets)} featured sticker sets")
        
        all_sets = list(featured_result.sets)
    except Exception as e:
        print(f"Could not fetch featured stickers: {e}")
        all_sets = []

    # Add special system sticker sets
    special_sets = [
        types.InputStickerSetAnimatedEmoji(),
        types.InputStickerSetAnimatedEmojiAnimations(),
        types.InputStickerSetPremiumGifts(),
        types.InputStickerSetEmojiGenericAnimations(),
        types.InputStickerSetEmojiDefaultStatuses(),
        types.InputStickerSetEmojiDefaultTopicIcons(),
        types.InputStickerSetEmojiChannelDefaultStatuses(),
        types.InputStickerSetDice(emoticon="🎲"),
        types.InputStickerSetDice(emoticon="🎯"),
        types.InputStickerSetDice(emoticon="🏀"),
        types.InputStickerSetDice(emoticon="⚽"),
        types.InputStickerSetDice(emoticon="🎰"),
        types.InputStickerSetDice(emoticon="🎳"),
        types.InputStickerSetTonGifts(),
    ]
    
    # Combine all sets, removing duplicates by ID
    seen_ids = set()
    for stickerset in all_sets:
        if hasattr(stickerset, 'id') and stickerset.id not in seen_ids:
            seen_ids.add(stickerset.id)
        elif hasattr(stickerset, 'short_name'):
            seen_ids.add(stickerset.short_name)
    
    # Process all sticker sets
    print(f"\n=== Processing {len(all_sets) + len(special_sets)} sticker sets ===")
    
    for i, stickerset in enumerate(all_sets):
        short_name = getattr(stickerset, 'short_name', None)
        if not short_name:
            print(f"  [{i+1}/{len(all_sets)}] Skipping set without short_name")
            continue
            
        print(f"  [{i+1}/{len(all_sets)}] Processing: {short_name}")
        
        try:
            result = await client(functions.messages.GetStickerSetRequest(
                stickerset=types.InputStickerSetShortName(short_name=short_name),
                hash=0
            ))
        except Exception as e:
            print(f"    ERROR: {e}")
            continue

        s = result.set
        print(f"    id={s.id} short_name={s.short_name} count={s.count}")
        
        set_dir = OUT_DIR / short_name
        set_dir.mkdir(parents=True, exist_ok=True)

        docs = []
        for doc in result.documents:
            ext = get_file_ext(doc.mime_type, doc.id)
            path = set_dir / f"{doc.id}.{ext}"
            
            if not path.exists():
                data = await client.download_media(doc, file=bytes)
                if data:
                    path.write_bytes(data)
                    print(f"    Downloaded {doc.id}.{ext}")
                    downloaded_count += 1
                else:
                    print(f"    FAILED to download {doc.id}")
                    continue
            else:
                skipped_count += 1
            
            docs.append({
                "doc_id": doc.id,
                "access_hash": doc.access_hash,
                "mime": doc.mime_type,
                "size": doc.size,
                "file": str(path),
                "ext": ext,
            })

        packs = []
        for p in result.packs:
            pack_doc_ids = []
            for d in p.documents:
                doc_id = d
                if hasattr(d, "id"):
                    doc_id = d.id
                pack_doc_ids.append(doc_id)
            packs.append({
                "emoticon": p.emoticon,
                "documents": pack_doc_ids
            })

        manifest.append({
            "name": short_name,
            "slug": short_name,
            "set_id": s.id,
            "set_access_hash": s.access_hash,
            "short_name": s.short_name,
            "title": s.title,
            "documents": docs,
            "packs": packs,
        })

    # Process special sets
    print(f"\n=== Processing special sets ===")
    special_names = [
        ("animated_emoji", "AnimatedEmoji", types.InputStickerSetAnimatedEmoji()),
        ("animated_emoji_animations", "AnimatedEmojiAnimations", types.InputStickerSetAnimatedEmojiAnimations()),
        ("premium_gifts", "PremiumGifts", types.InputStickerSetPremiumGifts()),
        ("emoji_generic_animations", "EmojiGenericAnimations", types.InputStickerSetEmojiGenericAnimations()),
        ("emoji_default_statuses", "EmojiDefaultStatuses", types.InputStickerSetEmojiDefaultStatuses()),
        ("emoji_default_topic_icons", "EmojiDefaultTopicIcons", types.InputStickerSetEmojiDefaultTopicIcons()),
        ("emoji_channel_statuses", "EmojiChannelStatuses", types.InputStickerSetEmojiChannelDefaultStatuses()),
        ("dice_🎲", "Dice_🎲", types.InputStickerSetDice(emoticon="🎲")),
        ("dice_🎯", "Dice_🎯", types.InputStickerSetDice(emoticon="🎯")),
        ("dice_🏀", "Dice_🏀", types.InputStickerSetDice(emoticon="🏀")),
        ("dice_⚽", "Dice_⚽", types.InputStickerSetDice(emoticon="⚽")),
        ("dice_🎰", "Dice_🎰", types.InputStickerSetDice(emoticon="🎰")),
        ("dice_🎳", "Dice_🎳", types.InputStickerSetDice(emoticon="🎳")),
        ("ton_gifts", "TonGifts", types.InputStickerSetTonGifts()),
    ]
    
    for slug, name, input_set in special_names:
        print(f"  Processing: {name}")
        
        try:
            result = await client(functions.messages.GetStickerSetRequest(
                stickerset=input_set,
                hash=0
            ))
        except Exception as e:
            print(f"    ERROR: {e}")
            continue

        s = result.set
        print(f"    id={s.id} short_name={s.short_name} count={s.count}")
        
        set_dir = OUT_DIR / name
        set_dir.mkdir(parents=True, exist_ok=True)

        docs = []
        for doc in result.documents:
            ext = get_file_ext(doc.mime_type, doc.id)
            path = set_dir / f"{doc.id}.{ext}"
            
            if not path.exists():
                data = await client.download_media(doc, file=bytes)
                if data:
                    path.write_bytes(data)
                    print(f"    Downloaded {doc.id}.{ext}")
                    downloaded_count += 1
                else:
                    print(f"    FAILED to download {doc.id}")
                    continue
            else:
                skipped_count += 1
            
            docs.append({
                "doc_id": doc.id,
                "access_hash": doc.access_hash,
                "mime": doc.mime_type,
                "size": doc.size,
                "file": str(path),
                "ext": ext,
            })

        packs = []
        for p in result.packs:
            pack_doc_ids = []
            for d in p.documents:
                doc_id = d
                if hasattr(d, "id"):
                    doc_id = d.id
                pack_doc_ids.append(doc_id)
            packs.append({
                "emoticon": p.emoticon,
                "documents": pack_doc_ids
            })

        manifest.append({
            "name": name,
            "slug": slug,
            "set_id": s.id,
            "set_access_hash": s.access_hash,
            "short_name": s.short_name or slug,
            "title": s.title,
            "documents": docs,
            "packs": packs,
        })

    MANIFEST_FILE.write_text(json.dumps(manifest, indent=2, ensure_ascii=False))
    print(f"\nSaved manifest to {MANIFEST_FILE}")
    print(f"Downloaded: {downloaded_count} files, Skipped: {skipped_count} files")
    await client.disconnect()


def to_int64(v):
    if isinstance(v, dict):
        val = (v.get("high", 0) << 32) | (v.get("low", 0) & 0xFFFFFFFF)
        return val - (1 << 64) if val >= (1 << 63) else val
    if isinstance(v, int):
        return v
    return int(v)


def create_sticker_attribute(set_id: int, set_access_hash: int, mask: bool = False) -> bytes:
    attr_type = 0x15c4b51c
    
    # Convert to unsigned 64-bit
    set_id = set_id & 0xFFFFFFFFFFFFFFFF
    set_access_hash = set_access_hash & 0xFFFFFFFFFFFFFFFF
    
    data = bytearray()
    data.extend(struct.pack("<I", attr_type))
    
    alt_bytes = b""
    data.extend(struct.pack("<I", len(alt_bytes)))
    data.extend(alt_bytes)
    
    stickerset = bytearray()
    stickerset.extend(struct.pack("<Q", set_id))
    stickerset.extend(struct.pack("<Q", set_access_hash))
    stickerset_data = bytes(stickerset)
    data.extend(struct.pack("<I", len(stickerset_data)))
    data.extend(stickerset_data)
    
    mask_byte = b"\x01" if mask else b"\x00"
    data.extend(mask_byte)
    
    return bytes(data)


def cmd_import():
    import pymongo
    from minio import Minio

    assert MINIO_ACCESS_KEY and MINIO_SECRET_KEY, \
        "Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY env vars"

    if not MANIFEST_FILE.exists():
        print(f"ERROR: Manifest file {MANIFEST_FILE} not found. Run --download first.")
        return

    manifest = json.loads(MANIFEST_FILE.read_text())
    minio = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY,
                  secret_key=MINIO_SECRET_KEY, secure=False)
    
    try:
        if not minio.bucket_exists(MINIO_BUCKET):
            minio.make_bucket(MINIO_BUCKET)
            print(f"Created bucket: {MINIO_BUCKET}")
    except Exception as e:
        print(f"Bucket check/creation error: {e}")

    mongo = pymongo.MongoClient(MONGO_URL)
    db = mongo["tg"]
    doc_col = db["eventflow-documentreadmodel"]
    set_col = db["eventflow-stickersetreadmodel"]

    existing_docs = {
        to_int64(d["DocumentId"])
        for d in doc_col.find({}, {"DocumentId": 1})
    }
    print(f"Found {len(existing_docs)} existing documents in MongoDB")

    for entry in manifest:
        name = entry["name"]
        print(f"\n=== {name} ===")
        
        set_id = to_int64(entry["set_id"])
        set_access_hash = to_int64(entry["set_access_hash"])
        doc_ids = []

        for doc in entry["documents"]:
            doc_id = to_int64(doc["doc_id"])
            doc_id = doc_id & 0x7FFFFFFFFFFFFFFF
            doc_ids.append(doc_id)
            p = Path(doc["file"])

            if doc_id in existing_docs:
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                    print(f"  Skip doc {doc_id} (exists in MinIO)")
                except Exception:
                    if p.exists():
                        data = p.read_bytes()
                        mime = doc.get("mime", "application/octet-stream")
                        minio.put_object(MINIO_BUCKET, str(doc_id),
                                        io.BytesIO(data), length=len(data), content_type=mime)
                        print(f"  Re-uploaded doc {doc_id}")
                    else:
                        print(f"  WARNING: doc {doc_id} missing in MinIO and file not found")
                continue

            if not p.exists():
                print(f"  MISSING file {p}")
                continue

            data = p.read_bytes()
            mime = doc.get("mime", "application/octet-stream")
            file_ref = list(os.urandom(16))
            access_hash = to_int64(doc.get("access_hash", 0)) or int.from_bytes(os.urandom(8), "little", signed=True)

            ext = doc.get("ext", "bin")
            
            minio.put_object(MINIO_BUCKET, str(doc_id),
                            io.BytesIO(data), length=len(data), content_type=mime)
            
            sticker_attrs = create_sticker_attribute(set_id, set_access_hash)
            
            doc_col.insert_one({
                "_id": f"documentreadmodel-{doc_id}",
                "Id": f"documentreadmodel-{doc_id}",
                "DocumentId": doc_id,
                "LocalFile": str(p),
                "AccessHash": access_hash,
                "FileReference": file_ref,
                "Date": int(time.time()),
                "DcId": DC_ID,
                "MimeType": mime,
                "Size": len(data),
                "Name": p.name,
                "Thumbs": None,
                "VideoThumbs": None,
                "Attributes": sticker_attrs,
                "Attributes2": None,
                "CreatorId": None,
                "Fingerprint": None,
                "Md5CheckSum": None,
                "ThumbId": None,
                "VideoThumbId": None,
                "Version": 1,
            })
            existing_docs.add(doc_id)
            print(f"  Imported doc {doc_id} ({ext}, {len(data)} bytes)")

        packs = []
        for p in entry.get("packs", []):
            pack_doc_ids = [to_int64(d) & 0x7FFFFFFFFFFFFFFF for d in p.get("documents", [])]
            packs.append({
                "Emoticon": p["emoticon"],
                "Documents": pack_doc_ids
            })

        set_col.update_one(
            {"_id": f"stickersetreadmodel-{set_id}"},
            {"$set": {
                "_id": f"stickersetreadmodel-{set_id}",
                "StickerSetId": set_id,
                "AccessHash": set_access_hash & 0x7FFFFFFFFFFFFFFF,
                "ShortName": entry["short_name"],
                "Title": entry["title"],
                "Slug": entry["slug"],
                "Count": len(doc_ids),
                "DocumentIds": doc_ids,
                "Packs": packs,
                "Version": 1,
            }},
            upsert=True,
        )
        print(f"  Upserted sticker set {set_id} ({len(doc_ids)} docs)")

    print("\nDone!")


if __name__ == "__main__":
    import sys
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import()
    else:
        print(__doc__)
