#!/usr/bin/env python3
"""
Reaction seeder for MyTelegram server.

Steps:
  1. Download reaction files from Telegram:
       python3 seed_reactions.py --download
     (requires telethon, prompts for phone code)

  2. Import downloaded files into the server:
       MONGO_URL=mongodb://localhost:27017 \
       MINIO_ENDPOINT=localhost:9000 \
       MINIO_ACCESS_KEY=... \
       MINIO_SECRET_KEY=... \
       python3 seed_reactions.py --import

  After import, rebuild messenger-command-server and messenger-query-server
  docker images. Reactions are now loaded from MongoDB 'reactions' collection.
"""
import asyncio, json, os, io, struct, time
from pathlib import Path
from typing import Any, Dict, List

# Telegram API credentials — set via env or fill in
TG_API_ID   = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_PHONE    = os.environ.get("TG_PHONE", "")

MONGO_URL        = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MINIO_ENDPOINT   = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET     = "tg-files"
GRPC_ENDPOINT    = os.environ.get("GRPC_ENDPOINT", "localhost:8080")
DC_ID            = 2

OUT_DIR       = Path("reactions_files")
MANIFEST_FILE = Path("reactions_manifest.json")

FIELDS = ["static_icon", "appear_animation", "select_animation",
          "activate_animation", "effect_animation", "around_animation", "center_icon"]


def serialize_thumbs(document) -> List[Dict[str, Any]]:
    """Convert Telethon document thumbs into MongoDB/manifest-safe dictionaries."""
    from telethon.tl.types import (
        PhotoCachedSize,
        PhotoPathSize,
        PhotoSize,
        PhotoSizeEmpty,
        PhotoSizeProgressive,
        PhotoStrippedSize,
    )

    serialized = []
    for thumb in getattr(document, "thumbs", None) or []:
        if isinstance(thumb, PhotoSize):
            serialized.append({
                "_t": "TPhotoSize", "Type": thumb.type,
                "W": thumb.w, "H": thumb.h, "Size": thumb.size,
            })
        elif isinstance(thumb, PhotoCachedSize):
            serialized.append({
                "_t": "TPhotoCachedSize", "Type": thumb.type,
                "W": thumb.w, "H": thumb.h, "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoSizeProgressive):
            serialized.append({
                "_t": "TPhotoSizeProgressive", "Type": thumb.type,
                "W": thumb.w, "H": thumb.h, "Sizes": list(thumb.sizes),
            })
        elif isinstance(thumb, PhotoStrippedSize):
            serialized.append({
                "_t": "TPhotoStrippedSize", "Type": thumb.type,
                "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoPathSize):
            serialized.append({
                "_t": "TPhotoPathSize", "Type": thumb.type,
                "Bytes": list(thumb.bytes),
            })
        elif isinstance(thumb, PhotoSizeEmpty):
            serialized.append({"_t": "TPhotoSizeEmpty", "Type": thumb.type})
    return serialized


async def download_thumbs(client, document, field: str) -> Dict[str, str]:
    """Download server-backed document thumbs so MyTelegram can serve them."""
    from telethon.tl.types import PhotoSize, PhotoSizeProgressive

    files = {}
    for thumb in getattr(document, "thumbs", None) or []:
        if not isinstance(thumb, (PhotoSize, PhotoSizeProgressive)):
            continue
        out = OUT_DIR / f"{document.id}_{field}_thumb_{thumb.type}.bin"
        if not out.exists():
            try:
                data = await client.download_media(document, file=bytes, thumb=thumb)
            except Exception as e:
                print(f"  thumb {document.id}_{thumb.type}: ERROR {e}")
                continue
            if data:
                out.write_bytes(data)
        if out.exists():
            files[thumb.type] = str(out)
    return files


def upload_thumbs(minio, doc_id: int, thumb_files: Dict[str, str]):
    for thumb_type, file_path in (thumb_files or {}).items():
        path = Path(file_path)
        if not path.exists():
            continue
        data = path.read_bytes()
        minio.put_object(
            MINIO_BUCKET,
            f"{doc_id}_{thumb_type}",
            io.BytesIO(data),
            length=len(data),
        )


# ── Download ──────────────────────────────────────────────────────────────────

async def download_doc(client, emoji, field, doc, sem):
    from telethon.tl.types import Document, DocumentAttributeFilename
    if not isinstance(doc, Document):
        return field, None, [], {}
    thumbs = serialize_thumbs(doc)
    async with sem:
        thumb_files = await download_thumbs(client, doc, field)
        is_tgs = any(isinstance(a, DocumentAttributeFilename) and a.file_name.endswith(".tgs")
                     for a in doc.attributes) or "tgs" in (doc.mime_type or "")
        ext = "tgs" if is_tgs else ("webp" if "webp" in (doc.mime_type or "") else "bin")
        out = OUT_DIR / f"{doc.id}_{field}.{ext}"
        if out.exists():
            return field, str(out), thumbs, thumb_files
        print(f"  [{emoji}] {field}: {doc.size}b", flush=True)
        try:
            data = await client.download_media(doc, file=bytes)
        except Exception as e:
            print(f"  [{emoji}] {field}: ERROR {e}")
            return field, None, thumbs, thumb_files
        if data:
            out.write_bytes(data)
        return field, str(out) if data else None, thumbs, thumb_files


async def cmd_download():
    from telethon import TelegramClient
    from telethon.tl.functions.messages import GetAvailableReactionsRequest
    assert TG_API_ID and TG_API_HASH and TG_PHONE, \
        "Set TG_API_ID, TG_API_HASH, TG_PHONE env vars"
    OUT_DIR.mkdir(exist_ok=True)
    client = TelegramClient("reaction_seeder", TG_API_ID, TG_API_HASH)
    await client.start(phone=TG_PHONE)
    result = await client(GetAvailableReactionsRequest(hash=0))
    print(f"Got {len(result.reactions)} reactions")
    sem = asyncio.Semaphore(10)

    async def process(r):
        docs = {f: getattr(r, f, None) for f in FIELDS}
        results = await asyncio.gather(*[download_doc(client, r.reaction, f, d, sem)
                                         for f, d in docs.items()])
        files = {field: path for field, path, _, _ in results}
        thumbs = {f"{field}_thumbs": doc_thumbs for field, _, doc_thumbs, _ in results}
        thumb_files = {f"{field}_thumb_files": files for field, _, _, files in results}
        return {"emoji": r.reaction, "title": r.title,
                "inactive": r.inactive, "premium": r.premium,
                **files, **thumbs, **thumb_files}

    manifest = await asyncio.gather(*[process(r) for r in result.reactions])
    MANIFEST_FILE.write_text(json.dumps(list(manifest), indent=2, ensure_ascii=False))
    print(f"Saved {len(manifest)} reactions to {MANIFEST_FILE}")
    await client.disconnect()


# ── Import ────────────────────────────────────────────────────────────────────

def to_int64(v):
    if isinstance(v, dict):
        val = (v["high"] << 32) | (v["low"] & 0xFFFFFFFF)
        return val - (1 << 64) if val >= (1 << 63) else val
    return int(v)


def cmd_import():
    import pymongo
    from minio import Minio

    assert MINIO_ACCESS_KEY and MINIO_SECRET_KEY, \
        "Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY env vars"

    manifest = json.loads(MANIFEST_FILE.read_text())
    minio = Minio(MINIO_ENDPOINT, access_key=MINIO_ACCESS_KEY,
                  secret_key=MINIO_SECRET_KEY, secure=False)
    if not minio.bucket_exists(MINIO_BUCKET):
        minio.make_bucket(MINIO_BUCKET)

    mongo = pymongo.MongoClient(MONGO_URL)
    doc_col = mongo["tg"]["eventflow-documentreadmodel"]
    reactions_col = mongo["tg"]["reactions"]

    # Build name -> doc_id mapping from existing MongoDB records
    existing = {d["Name"]: to_int64(d["DocumentId"])
                for d in doc_col.find({}, {"Name": 1, "DocumentId": 1, "AccessHash": 1,
                                       "FileReference": 1, "MimeType": 1, "Size": 1})}

    attrs_bytes = list(struct.pack("<II", 0x15c4b51c, 0))
    imported = 0
    reaction_docs = []

    for order, reaction in enumerate(manifest):
        emoji = reaction["emoji"]
        reaction_doc = {
            "Reaction": emoji,
            "Title": reaction["title"],
            "Inactive": reaction.get("inactive", False),
            "Premium": reaction.get("premium", False),
            "Order": order
        }

        for field in FIELDS:
            file_path = reaction.get(field)
            if not file_path:
                reaction_doc[field.title().replace("_", "")] = None
                continue
            p = Path(file_path)
            if not p.exists():
                print(f"  [{emoji}] {field}: file missing {p}")
                reaction_doc[field.title().replace("_", "")] = None
                continue

            # Extract original Telegram doc ID from filename
            orig_tg_id = p.stem.split("_")[0]
            name = p.name
            thumbs = reaction.get(f"{field}_thumbs") or None
            thumb_files = reaction.get(f"{field}_thumb_files") or {}

            # Check if already in MongoDB
            if name in existing:
                doc_id = existing[name]
                if thumbs:
                    doc_col.update_one(
                        {"DocumentId": doc_id},
                        {"$set": {"Thumbs": thumbs}},
                    )
                # Check if file is in Minio
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                except Exception:
                    # Upload missing file
                    data = p.read_bytes()
                    minio.put_object(MINIO_BUCKET, str(doc_id),
                                     io.BytesIO(data), length=len(data))
                    print(f"  [{emoji}] {field}: uploaded to minio doc_id={doc_id}")
                upload_thumbs(minio, doc_id, thumb_files)
            else:
                # New document — insert into MongoDB and upload to Minio
                data = p.read_bytes()
                mime = ("application/x-tgsticker" if p.suffix == ".tgs"
                        else "image/webp" if p.suffix == ".webp"
                        else "application/octet-stream")
                file_ref = list(os.urandom(16))
                access_hash = int.from_bytes(os.urandom(8), "little", signed=True)

                # Use original Telegram ID as document ID (fits int64)
                doc_id = int(orig_tg_id) & 0x7FFFFFFFFFFFFFFF

                minio.put_object(MINIO_BUCKET, str(doc_id),
                                 io.BytesIO(data), length=len(data), content_type=mime)
                upload_thumbs(minio, doc_id, thumb_files)
                doc_col.insert_one({
                    "_id": f"documentreadmodel-{doc_id}",
                    "Id": f"documentreadmodel-{doc_id}",
                    "DocumentId": doc_id,
                    "LocalFile": f"reactions_files/{name}",
                    "AccessHash": access_hash,
                    "FileReference": file_ref,
                    "Date": int(time.time()),
                    "DcId": DC_ID, "MimeType": mime, "Size": len(data),
                    "Name": name, "Thumbs": thumbs, "VideoThumbs": None,
                    "Attributes": attrs_bytes, "Attributes2": None,
                    "CreatorId": None, "Fingerprint": None,
                    "Md5CheckSum": None, "ThumbId": None, "VideoThumbId": None,
                    "Version": 1,
                })
                print(f"  [{emoji}] {field}: imported doc_id={doc_id}")
                imported += 1
                existing[name] = doc_id

            # Get document metadata from MongoDB
            doc_meta = doc_col.find_one({"DocumentId": existing[name]})
            if doc_meta:
                field_name = field.title().replace("_", "")
                reaction_doc[field_name] = {
                    "Id": to_int64(doc_meta["DocumentId"]),
                    "AccessHash": to_int64(doc_meta["AccessHash"]),
                    "FileReference": doc_meta["FileReference"],
                    "Date": doc_meta["Date"],
                    "MimeType": doc_meta["MimeType"],
                    "Size": doc_meta["Size"],
                    "DcId": doc_meta["DcId"],
                    "Thumbs": doc_meta.get("Thumbs"),
                }

        reaction_docs.append(reaction_doc)

    # Clear existing reactions and insert new ones
    reactions_col.delete_many({})
    if reaction_docs:
        reactions_col.insert_many(reaction_docs)
        print(f"\nInserted {len(reaction_docs)} reactions into MongoDB")

    print(f"Done. Imported {imported} new documents.")
    print("Reactions are now loaded from MongoDB. Rebuild messenger servers to apply changes.")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys
    if "--import" in sys.argv:
        cmd_import()
    elif "--download" in sys.argv:
        asyncio.run(cmd_download())
    else:
        print(__doc__)
