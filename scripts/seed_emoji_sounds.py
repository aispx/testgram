#!/usr/bin/env python3
"""
Seeds the animated-emoji soundbites (`emojies_sounds`) from the official Telegram servers.

Certain animated emojis play a sound when clicked, and the list of them is part of the client
configuration rather than of any sticker set: `help.getAppConfig` carries a map emoji ->
{id, access_hash, file_reference_base64}, and the client downloads that document with
`upload.getFile`. See https://corefork.telegram.org/api/animated-emojis#emojis-with-sounds

Nothing derives this list, so it is copied from Telegram verbatim: nine emojis, measured against the
live service (halloween 🎃 ⚰ 🧟 🧟‍♂ 🧟‍♀, plus 🍑 🎊 🎄 🦾). The bodies are ~5-7 KB Ogg files.

The key cannot be served from the static configuration, because the `access_hash` in it is minted
per session here (see AccessHashHelper2) - GetAppConfigHandler builds the entry per caller from the
`emoji_sounds` collection this script writes. Telegram's own access hashes are what --download uses
and they are *not* stored: only the emoji and the document id matter afterwards.

Usage:
  1. Download from Telegram (needs an account; reuse an existing Telethon session to skip login):
       TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
       python3 seed_emoji_sounds.py --download

  2. Import into the server:
       MONGO_URL=mongodb://172.23.0.8:27017 \\
       MINIO_ENDPOINT=172.23.0.10:9000 \\
       MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \\
       python3 seed_emoji_sounds.py --import

  --dry-run may be added to --import to print what would change and write nothing.

Both steps are idempotent: re-running replaces the `emoji_sounds` rows, keeps the file reference a
client may already hold, and re-uploads only bodies missing from the object store.
"""
import asyncio
import hashlib
import io
import json
import os
import sys
import time
from pathlib import Path
from typing import Any, Dict, List, Optional

TG_API_ID = int(os.environ.get("TG_API_ID", "0"))
TG_API_HASH = os.environ.get("TG_API_HASH", "")
TG_SESSION = os.environ.get("TG_SESSION", "emoji_sounds_seeder")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = os.environ.get("MINIO_BUCKET", "tg-files")

# Bodies the server stores itself are unencrypted and live on the media DC, like the sticker files
# the other seeders write.
DC_ID = 1

# audio/ogg is what tdlib assumes for these (`MimeType::to_extension("audio/ogg", "oga")` in
# StickersManager::on_update_emoji_sounds), and Telegram serves Ogg Opus bodies.
MIME_TYPE = "audio/ogg"

CHUNK_SIZE = 512 * 1024

OUT_DIR = Path("emoji_sounds")
MANIFEST_FILE = Path("emoji_sounds_manifest.json")

COLLECTION = "emoji_sounds"
DOCUMENT_COLLECTION = "eventflow-documentreadmodel"


def parse_app_config_sounds(config) -> List[Dict[str, Any]]:
    """
    Pulls `emojies_sounds` out of a `help.appConfig`. Every value is a string on the wire - that is
    what tdlib and Android require - so ids come back as text and are parsed here.
    """
    from telethon.tl.types import JsonObject, JsonString

    if not isinstance(config, JsonObject):
        raise SystemExit("appConfig is not a jsonObject")

    sounds: List[Dict[str, Any]] = []
    for entry in config.value:
        if entry.key != "emojies_sounds":
            continue
        if not isinstance(entry.value, JsonObject):
            raise SystemExit("emojies_sounds is not a jsonObject")

        for item in entry.value.value:
            if not isinstance(item.value, JsonObject):
                continue

            fields = {f.key: f.value.value for f in item.value.value if isinstance(f.value, JsonString)}
            if "id" not in fields or "access_hash" not in fields:
                print(f"  skipped {item.key!r}: incomplete entry {fields}")
                continue

            sounds.append({
                "emoji": item.key,
                "document_id": int(fields["id"]),
                "access_hash": int(fields["access_hash"]),
                "file_reference_base64": fields.get("file_reference_base64", ""),
            })

    return sounds


async def download_body(client, sound: Dict[str, Any]) -> bytes:
    """
    Downloads one soundbite. The document object is never served for these, so the location is built
    from the three values the configuration carries; Telegram currently ships an empty file
    reference, which its own file servers accept.
    """
    import base64

    from telethon.tl import functions, types

    reference = sound["file_reference_base64"]
    file_reference = base64.urlsafe_b64decode(reference + "=" * (-len(reference) % 4)) if reference else b""

    location = types.InputDocumentFileLocation(id=sound["document_id"],
                                               access_hash=sound["access_hash"],
                                               file_reference=file_reference,
                                               thumb_size="")

    body = b""
    while True:
        result = await client(functions.upload.GetFileRequest(location=location, offset=len(body),
                                                             limit=CHUNK_SIZE))
        chunk = result.bytes
        body += chunk
        if len(chunk) < CHUNK_SIZE:
            return body


async def cmd_download() -> None:
    from telethon import TelegramClient
    from telethon.tl import functions

    if not TG_API_ID or not TG_API_HASH:
        raise SystemExit("Set TG_API_ID and TG_API_HASH")

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.start()

    config = await client(functions.help.GetAppConfigRequest(hash=0))
    sounds = parse_app_config_sounds(config.config)
    print(f"emojies_sounds: {len(sounds)} entries")

    manifest: List[Dict[str, Any]] = []
    for sound in sounds:
        body = await download_body(client, sound)
        if not body.startswith(b"OggS"):
            print(f"  WARNING {sound['emoji']}: body is not Ogg ({body[:4]!r})")

        path = OUT_DIR / f"{sound['document_id']}.ogg"
        path.write_bytes(body)

        manifest.append({
            "emoji": sound["emoji"],
            "document_id": sound["document_id"],
            "size": len(body),
            "sha256": hashlib.sha256(body).hexdigest(),
            "file": str(path),
        })
        print(f"  {sound['emoji']} {sound['document_id']} {len(body)} bytes -> {path}")

    await client.disconnect()

    MANIFEST_FILE.write_text(json.dumps(manifest, ensure_ascii=False, indent=1))
    print(f"Wrote {MANIFEST_FILE} ({len(manifest)} sounds)")


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


def upload_body(minio, doc_id: int, path: Path, dry_run: bool) -> bool:
    """Uploads a body only when the object store does not already have it."""
    try:
        minio.stat_object(MINIO_BUCKET, str(doc_id))
        return False
    except Exception:  # noqa: BLE001 - the SDK raises for "no such object"
        pass

    if dry_run:
        print(f"    would upload {path} as {doc_id}")
        return True

    data = path.read_bytes()
    minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data),
                     content_type=MIME_TYPE)
    return True


def ogg_duration_seconds(body: bytes) -> int:
    """
    Duration from the granule position of the last Ogg page, at the 48 kHz Opus clock. No client
    reads it for a soundbite - tdlib registers the id as a bare remote file location and never asks
    for the document - but a plainly wrong `documentAttributeAudio` in the read model would be a
    trap for anything that later lists these rows.
    """
    index = body.rfind(b"OggS")
    if index < 0 or index + 14 > len(body):
        return 1

    granule = int.from_bytes(body[index + 6:index + 14], "little")

    return max(1, round(granule / 48000))


def import_sounds(db, minio, manifest: List[Dict[str, Any]], dry_run: bool) -> None:
    doc_col = db[DOCUMENT_COLLECTION]
    sound_col = db[COLLECTION]

    bodies = written = rows = 0
    for order, sound in enumerate(manifest):
        doc_id = int(sound["document_id"])
        path = Path(sound["file"])
        if not path.exists():
            raise SystemExit(f"{path} not found; run --download first")

        if upload_body(minio, doc_id, path, dry_run):
            bodies += 1

        body = path.read_bytes()
        row = {
            "_id": f"documentreadmodel-{doc_id}",
            "Id": f"documentreadmodel-{doc_id}",
            "DocumentId": doc_id,
            # Decorative: what a client quotes back is minted per session by AccessHashHelper2.
            "AccessHash": doc_id & 0x7FFFFFFFFFFFFFFF,
            # No FileReference: it is derived from the document id when the server serves the document,
            # including inside the emojies_sounds entry of help.getAppConfig.
            # See https://corefork.telegram.org/api/file-references
            "Date": int(time.time()),
            "DcId": DC_ID,
            "MimeType": MIME_TYPE,
            "Size": len(body),
            "Name": f"{doc_id}.ogg",
            "Thumbs": None,
            "VideoThumbs": None,
            "Attributes": None,
            # No sticker attribute and no stickerset: a soundbite belongs to no set, it is only ever
            # downloaded by id.
            "Attributes2": [
                {"_t": "TDocumentAttributeAudio", "Voice": False,
                 "Duration": ogg_duration_seconds(body)},
                {"_t": "TDocumentAttributeFilename", "FileName": f"{doc_id}.ogg"},
            ],
            "CreatorId": None,
            "Fingerprint": None,
            "Md5CheckSum": None,
            "ThumbId": None,
            "VideoThumbId": None,
            "Version": 1,
        }

        sound_row = {
            "_id": sound["emoji"],
            "Emoticon": sound["emoji"],
            "DocumentId": doc_id,
            "Order": order,
        }

        if dry_run:
            print(f"    would write document {doc_id} ({len(body)} bytes) and sound {sound['emoji']}")
            continue

        doc_col.replace_one({"DocumentId": doc_id}, row, upsert=True)
        written += 1

        sound_col.replace_one({"_id": sound["emoji"]}, sound_row, upsert=True)
        rows += 1

    print(f"  bodies uploaded: {bodies}, documents written: {written}, sounds written: {rows}")

    if not dry_run:
        stale = sound_col.delete_many(
            {"_id": {"$nin": [sound["emoji"] for sound in manifest]}}).deleted_count
        if stale:
            print(f"  removed {stale} sound(s) Telegram no longer serves")


def cmd_import(dry_run: bool) -> None:
    if not MANIFEST_FILE.exists():
        raise SystemExit(f"{MANIFEST_FILE} not found; run --download first")

    manifest: List[Dict[str, Any]] = json.loads(MANIFEST_FILE.read_text())
    db, minio = connect_storage()

    print(f"=== emoji sounds ({len(manifest)}) ===")
    import_sounds(db, minio, manifest, dry_run)


if __name__ == "__main__":
    dry = "--dry-run" in sys.argv
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import(dry)
    else:
        print(__doc__)
