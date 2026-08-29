#!/usr/bin/env python3
"""
Message effect seeder for MyTelegram server. See https://corefork.telegram.org/api/effects

Steps:
  1. Download effects from Telegram:
       TG_API_ID=... TG_API_HASH=... TG_PHONE=... \
       python3 seed_effects.py --download
     (requires telethon, prompts for login code)

  2. Import downloaded files into the server:
       MONGO_URL=mongodb://localhost:27017 \
       MINIO_ENDPOINT=localhost:9000 \
       MINIO_ACCESS_KEY=... \
       MINIO_SECRET_KEY=... \
       python3 seed_effects.py --import

  After import, rebuild messenger-command-server and messenger-query-server docker images.
  Effects are then served from the MongoDB 'effects' collection by GetAvailableEffectsHandler.

Notes:
  * availableEffect references documents by id; this seeder stores each referenced document
    denormalized inside the effect record (same shape the reactions seeder uses) so that serving
    the catalog stays a single query.
  * effect_animation_id is optional upstream: when absent, clients derive the animation from the
    premium sticker effect of effect_sticker_id, so a missing file here is not an error.
"""
import asyncio, io, json, os, struct, time
from pathlib import Path
from typing import Any, Dict, List

TG_API_ID   = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_PHONE    = os.environ.get("TG_PHONE", "")

MONGO_URL        = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MINIO_ENDPOINT   = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET     = "tg-files"
DC_ID            = 2

OUT_DIR       = Path("effects_files")
MANIFEST_FILE = Path("effects_manifest.json")

# availableEffect fields that point at a document, mapped to the MongoDB field they land in.
DOC_FIELDS = {
    "static_icon":      "StaticIcon",
    "effect_sticker":   "EffectSticker",
    "effect_animation": "EffectAnimation",
}


def serialize_thumbs(document) -> List[Dict[str, Any]]:
    """Convert Telethon document thumbs into MongoDB/manifest-safe dictionaries."""
    from telethon.tl.types import (
        PhotoCachedSize, PhotoPathSize, PhotoSize, PhotoSizeEmpty,
        PhotoSizeProgressive, PhotoStrippedSize,
    )

    serialized = []
    for thumb in getattr(document, "thumbs", None) or []:
        if isinstance(thumb, PhotoSize):
            serialized.append({"_t": "TPhotoSize", "Type": thumb.type,
                               "W": thumb.w, "H": thumb.h, "Size": thumb.size})
        elif isinstance(thumb, PhotoCachedSize):
            serialized.append({"_t": "TPhotoCachedSize", "Type": thumb.type,
                               "W": thumb.w, "H": thumb.h, "Bytes": list(thumb.bytes)})
        elif isinstance(thumb, PhotoSizeProgressive):
            serialized.append({"_t": "TPhotoSizeProgressive", "Type": thumb.type,
                               "W": thumb.w, "H": thumb.h, "Sizes": list(thumb.sizes)})
        elif isinstance(thumb, PhotoStrippedSize):
            serialized.append({"_t": "TPhotoStrippedSize", "Type": thumb.type,
                               "Bytes": list(thumb.bytes)})
        elif isinstance(thumb, PhotoPathSize):
            serialized.append({"_t": "TPhotoPathSize", "Type": thumb.type,
                               "Bytes": list(thumb.bytes)})
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
        minio.put_object(MINIO_BUCKET, f"{doc_id}_{thumb_type}",
                         io.BytesIO(data), length=len(data))


# ── Download ──────────────────────────────────────────────────────────────────

async def download_doc(client, emoticon, field, doc, sem):
    from telethon.tl.types import Document, DocumentAttributeFilename
    if not isinstance(doc, Document):
        return field, None, [], {}
    thumbs = serialize_thumbs(doc)
    async with sem:
        thumb_files = await download_thumbs(client, doc, field)
        is_tgs = any(isinstance(a, DocumentAttributeFilename) and a.file_name.endswith(".tgs")
                     for a in doc.attributes) or "tgs" in (doc.mime_type or "")
        if is_tgs:
            ext = "tgs"
        elif "webm" in (doc.mime_type or ""):
            ext = "webm"
        elif "webp" in (doc.mime_type or ""):
            ext = "webp"
        else:
            ext = "bin"
        out = OUT_DIR / f"{doc.id}_{field}.{ext}"
        if out.exists():
            return field, str(out), thumbs, thumb_files
        print(f"  [{emoticon}] {field}: {doc.size}b", flush=True)
        try:
            data = await client.download_media(doc, file=bytes)
        except Exception as e:
            print(f"  [{emoticon}] {field}: ERROR {e}")
            return field, None, thumbs, thumb_files
        if data:
            out.write_bytes(data)
        return field, str(out) if data else None, thumbs, thumb_files


async def cmd_download():
    from telethon import TelegramClient
    from telethon.tl.functions.messages import GetAvailableEffectsRequest
    assert TG_API_ID and TG_API_HASH and TG_PHONE, \
        "Set TG_API_ID, TG_API_HASH, TG_PHONE env vars"
    OUT_DIR.mkdir(exist_ok=True)
    client = TelegramClient("effect_seeder", TG_API_ID, TG_API_HASH)
    await client.start(phone=TG_PHONE)
    result = await client(GetAvailableEffectsRequest(hash=0))
    print(f"Got {len(result.effects)} effects, {len(result.documents)} documents")

    # availableEffect stores document ids; resolve them against the documents vector.
    docs_by_id = {d.id: d for d in result.documents}
    sem = asyncio.Semaphore(10)

    async def process(effect):
        wanted = {
            "static_icon":      getattr(effect, "static_icon_id", None),
            "effect_sticker":   effect.effect_sticker_id,
            "effect_animation": getattr(effect, "effect_animation_id", None),
        }
        results = await asyncio.gather(*[
            download_doc(client, effect.emoticon, field, docs_by_id.get(doc_id), sem)
            for field, doc_id in wanted.items()
        ])
        files       = {field: path for field, path, _, _ in results}
        thumbs      = {f"{field}_thumbs": t for field, _, t, _ in results}
        thumb_files = {f"{field}_thumb_files": f for field, _, _, f in results}
        return {
            "effect_id": effect.id,
            "emoticon": effect.emoticon,
            "premium_required": bool(getattr(effect, "premium_required", False)),
            **files, **thumbs, **thumb_files,
        }

    manifest = await asyncio.gather(*[process(e) for e in result.effects])
    MANIFEST_FILE.write_text(json.dumps(list(manifest), indent=2, ensure_ascii=False))
    print(f"Saved {len(manifest)} effects to {MANIFEST_FILE}")
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
    doc_col     = mongo["tg"]["eventflow-documentreadmodel"]
    effects_col = mongo["tg"]["effects"]

    existing = {d["Name"]: to_int64(d["DocumentId"])
                for d in doc_col.find({}, {"Name": 1, "DocumentId": 1})
                if d.get("Name")}

    attrs_bytes = list(struct.pack("<II", 0x15c4b51c, 0))
    imported = 0
    effect_docs = []

    for order, effect in enumerate(manifest):
        emoticon = effect["emoticon"]
        effect_doc = {
            "EffectId": int(effect["effect_id"]),
            "Emoticon": emoticon,
            "PremiumRequired": bool(effect.get("premium_required", False)),
            "Order": order,
        }

        for field, mongo_field in DOC_FIELDS.items():
            file_path = effect.get(field)
            effect_doc[mongo_field] = None
            if not file_path:
                continue
            p = Path(file_path)
            if not p.exists():
                print(f"  [{emoticon}] {field}: file missing {p}")
                continue

            orig_tg_id  = p.stem.split("_")[0]
            name        = p.name
            thumbs      = effect.get(f"{field}_thumbs") or None
            thumb_files = effect.get(f"{field}_thumb_files") or {}

            if name in existing:
                doc_id = existing[name]
                if thumbs:
                    doc_col.update_one({"DocumentId": doc_id}, {"$set": {"Thumbs": thumbs}})
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                except Exception:
                    data = p.read_bytes()
                    minio.put_object(MINIO_BUCKET, str(doc_id),
                                     io.BytesIO(data), length=len(data))
                    print(f"  [{emoticon}] {field}: uploaded to minio doc_id={doc_id}")
                upload_thumbs(minio, doc_id, thumb_files)
            else:
                data = p.read_bytes()
                mime = ("application/x-tgsticker" if p.suffix == ".tgs"
                        else "image/webp" if p.suffix == ".webp"
                        else "video/webm" if p.suffix == ".webm"
                        else "application/octet-stream")
                access_hash = int.from_bytes(os.urandom(8), "little", signed=True)
                doc_id      = int(orig_tg_id) & 0x7FFFFFFFFFFFFFFF

                minio.put_object(MINIO_BUCKET, str(doc_id),
                                 io.BytesIO(data), length=len(data), content_type=mime)
                upload_thumbs(minio, doc_id, thumb_files)
                doc_col.insert_one({
                    "_id": f"documentreadmodel-{doc_id}",
                    "Id": f"documentreadmodel-{doc_id}",
                    "DocumentId": doc_id,
                    "LocalFile": f"effects_files/{name}",
                    "AccessHash": access_hash,
                    # No FileReference: derived from the document id on the way out.
                    # See https://corefork.telegram.org/api/file-references
                    "Date": int(time.time()),
                    "DcId": DC_ID, "MimeType": mime, "Size": len(data),
                    "Name": name, "Thumbs": thumbs, "VideoThumbs": None,
                    "Attributes": attrs_bytes, "Attributes2": None,
                    "CreatorId": None, "Fingerprint": None,
                    "Md5CheckSum": None, "ThumbId": None, "VideoThumbId": None,
                    "Version": 1,
                })
                print(f"  [{emoticon}] {field}: imported doc_id={doc_id}")
                imported += 1
                existing[name] = doc_id

            doc_meta = doc_col.find_one({"DocumentId": existing[name]})
            if doc_meta:
                effect_doc[mongo_field] = {
                    "Id": to_int64(doc_meta["DocumentId"]),
                    "AccessHash": to_int64(doc_meta["AccessHash"]),
                    "Date": doc_meta["Date"],
                    "MimeType": doc_meta["MimeType"],
                    "Size": doc_meta["Size"],
                    "DcId": doc_meta["DcId"],
                    "Thumbs": doc_meta.get("Thumbs"),
                }

        # An effect without its preview sticker cannot be rendered, so it is not worth storing.
        if effect_doc["EffectSticker"] is None:
            print(f"  [{emoticon}] skipped: no effect_sticker document")
            continue

        effect_docs.append(effect_doc)

    effects_col.delete_many({})
    if effect_docs:
        effects_col.insert_many(effect_docs)
        effects_col.create_index("EffectId", unique=True)
        effects_col.create_index("Order")
        print(f"\nInserted {len(effect_docs)} effects into MongoDB")

    print(f"Done. Imported {imported} new documents.")
    print("Effects are served from MongoDB. Rebuild messenger servers to apply changes.")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys
    if "--import" in sys.argv:
        cmd_import()
    elif "--download" in sys.argv:
        asyncio.run(cmd_download())
    else:
        print(__doc__)
