#!/usr/bin/env python3
"""
Seeds the wallpaper catalogue (`account.getWallPapers`) from the official Telegram servers.

The catalogue is not derivable from anything: it is a curated list the service ships, so it is copied
verbatim — ids, slugs, access hashes, the `default`/`pattern`/`dark` flags, the fill settings and, for
image and pattern wallpapers, the document body itself.

**That last part is the point of this script.** `download_wallpapers_themes.py` records only
`document.id`, which leaves every image and pattern wallpaper pointing at a document that does not
exist here — and a wallpaper whose document is missing is dropped from `account.getWallPapers`, in
silence. The result looks like a healthy server whose wallpaper picker only ever offers gradients.

Usage:
  1. Download from Telegram (needs an account; reuse an existing Telethon session to skip login):
       TG_API_ID=... TG_API_HASH=... TG_SESSION=/root/sticker_seeder \\
       python3 seed_wallpapers.py --download

  2. Import into the server:
       MONGO_URL=mongodb://172.23.0.8:27017 \\
       MINIO_ENDPOINT=172.23.0.10:9000 \\
       MINIO_ACCESS_KEY=... MINIO_SECRET_KEY=... \\
       python3 seed_wallpapers.py --import

  --dry-run may be added to --import to print what would change and write nothing.

Both steps are idempotent, and --import **upserts**: it never clears the collection, so a wallpaper a
user uploaded through `account.uploadWallPaper` survives a re-seed. (`import_to_mongodb.py` starts with
`db.wallpapers.deleteMany({})`, which is why it cannot be used to top the catalogue up.)

See https://corefork.telegram.org/api/wallpapers
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
TG_SESSION = os.environ.get("TG_SESSION", "wallpaper_seeder")

MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")
MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT", "localhost:9000")
MINIO_ACCESS_KEY = os.environ.get("MINIO_ACCESS_KEY", "")
MINIO_SECRET_KEY = os.environ.get("MINIO_SECRET_KEY", "")
MINIO_BUCKET = os.environ.get("MINIO_BUCKET", "tg-files")

# Bodies the server stores itself are unencrypted and live on the media DC, like the sticker files the
# other seeders write.
DC_ID = 1

CHUNK_SIZE = 512 * 1024

OUT_DIR = Path("wallpapers")
MANIFEST_FILE = Path("wallpapers_manifest.json")

COLLECTION = "wallpapers"
DOCUMENT_COLLECTION = "eventflow-documentreadmodel"

SETTINGS_FIELDS = [
    ("blur", "Blur"),
    ("motion", "Motion"),
    ("background_color", "BackgroundColor"),
    ("second_background_color", "SecondBackgroundColor"),
    ("third_background_color", "ThirdBackgroundColor"),
    ("fourth_background_color", "FourthBackgroundColor"),
    ("intensity", "Intensity"),
    ("rotation", "Rotation"),
    # The emoticon marks a channel/supergroup wallpaper and was dropped entirely by the old importer,
    # so no imported wallpaper could be installed in a channel.
    ("emoticon", "Emoticon"),
]


def read_settings(settings) -> Dict[str, Any]:
    """The settings a wallpaper carries, under the field names the read model uses."""
    result: Dict[str, Any] = {}
    if settings is None:
        return result

    for wire, stored in SETTINGS_FIELDS:
        value = getattr(settings, wire, None)
        if value is None or value is False:
            continue
        result[stored] = value

    return result


async def download_file(client, location, expect_size: int = 0) -> bytes:
    """
    Wallpaper bodies live on whichever DC Telegram put them on — a raw `upload.getFile` answers
    `FILE_MIGRATE_4` for most of them. `client.download_file` borrows a sender for the right DC, which a
    hand-rolled loop over `GetFileRequest` does not.
    """
    return await client.download_file(location, file=bytes, file_size=expect_size or None,
                                      part_size_kb=CHUNK_SIZE // 1024)


async def download_document(client, document, out_dir: Path) -> Dict[str, Any]:
    """
    The body plus every separately-downloadable thumbnail. Android draws the grid tile from the
    thumbnail closest to 320px and only falls back to the full file when there is none, so leaving the
    thumbnails behind would make the picker download every wallpaper at full size.
    """
    from telethon.tl import types

    def location(thumb_size: str = ""):
        return types.InputDocumentFileLocation(id=document.id, access_hash=document.access_hash,
                                               file_reference=document.file_reference or b"",
                                               thumb_size=thumb_size)

    body = await download_file(client, location(), document.size or 0)
    path = out_dir / str(document.id)
    path.write_bytes(body)

    thumbs: List[Dict[str, Any]] = []
    for thumb in getattr(document, "thumbs", None) or []:
        # Only a real photoSize has a body of its own; the stripped and cached variants carry their
        # bytes inline and this server has nowhere to put them.
        if not isinstance(thumb, types.PhotoSize):
            continue

        thumb_body = await download_file(client, location(thumb.type), thumb.size or 0)
        thumb_path = out_dir / f"{document.id}_{thumb.type}"
        thumb_path.write_bytes(thumb_body)
        thumbs.append({
            "type": thumb.type,
            "w": thumb.w,
            "h": thumb.h,
            "size": len(thumb_body),
            "file": str(thumb_path),
        })

    return {
        "document_id": document.id,
        "mime_type": document.mime_type,
        "size": len(body),
        "file": str(path),
        "thumbs": thumbs,
    }


async def cmd_download() -> None:
    from telethon import TelegramClient
    from telethon.tl import functions, types

    if not TG_API_ID or not TG_API_HASH:
        raise SystemExit("Set TG_API_ID and TG_API_HASH")

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    client = TelegramClient(TG_SESSION, TG_API_ID, TG_API_HASH)
    await client.start()

    result = await client(functions.account.GetWallPapersRequest(hash=0))
    print(f"account.getWallPapers: {len(result.wallpapers)} wallpapers, hash={result.hash}")

    manifest: List[Dict[str, Any]] = []
    for order, wp in enumerate(result.wallpapers):
        entry: Dict[str, Any] = {
            "id": wp.id,
            "order": order,
            "default": bool(getattr(wp, "default", False)),
            "dark": bool(getattr(wp, "dark", False)),
            "pattern": bool(getattr(wp, "pattern", False)),
            "settings": read_settings(getattr(wp, "settings", None)),
        }

        if isinstance(wp, types.WallPaper):
            entry["access_hash"] = wp.access_hash
            entry["slug"] = wp.slug
            entry["document"] = await download_document(client, wp.document, OUT_DIR)
            print(f"  {wp.slug} ({'pattern' if wp.pattern else 'image'}) "
                  f"{entry['document']['size']} bytes, {len(entry['document']['thumbs'])} thumb(s)")
        else:
            entry["access_hash"] = 0
            entry["slug"] = ""
            entry["document"] = None
            print(f"  fill {wp.id} {entry['settings']}")

        manifest.append(entry)

    await client.disconnect()

    MANIFEST_FILE.write_text(json.dumps(manifest, ensure_ascii=False, indent=1))
    print(f"Wrote {MANIFEST_FILE} ({len(manifest)} wallpapers)")


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


def upload_body(minio, object_name: str, path: Path, mime_type: str, dry_run: bool) -> bool:
    """Uploads a body only when the object store does not already have it."""
    try:
        minio.stat_object(MINIO_BUCKET, object_name)
        return False
    except Exception:  # noqa: BLE001 - the SDK raises for "no such object"
        pass

    if dry_run:
        print(f"    would upload {path} as {object_name}")
        return True

    data = path.read_bytes()
    minio.put_object(MINIO_BUCKET, object_name, io.BytesIO(data), length=len(data),
                     content_type=mime_type)
    return True


def document_row(document: Dict[str, Any]) -> Dict[str, Any]:
    doc_id = int(document["document_id"])

    return {
        "_id": f"documentreadmodel-{doc_id}",
        "Id": f"documentreadmodel-{doc_id}",
        "DocumentId": doc_id,
        # Decorative: what a client quotes back is minted per session by AccessHashHelper2.
        "AccessHash": doc_id & 0x7FFFFFFFFFFFFFFF,
        # No FileReference: the server mints it from the document id on the way out.
        # See https://corefork.telegram.org/api/file-references
        "Date": int(time.time()),
        "DcId": DC_ID,
        "MimeType": document["mime_type"],
        "Size": int(document["size"]),
        "Name": str(doc_id),
        "Thumbs": [{"Type": t["type"], "W": t["w"], "H": t["h"], "Size": t["size"]}
                   for t in document["thumbs"]] or None,
        "VideoThumbs": None,
        # No sticker attribute and no stickerset: a wallpaper belongs to no set. Clients read the size
        # off the image itself, so documentAttributeImageSize is not invented here.
        "Attributes": None,
        "Attributes2": None,
        "CreatorId": None,
        "Fingerprint": None,
        "Md5CheckSum": None,
        "ThumbId": None,
        "VideoThumbId": None,
        "Version": 1,
    }


def catalogue_row(entry: Dict[str, Any]) -> Dict[str, Any]:
    wallpaper_id = int(entry["id"])
    document = entry.get("document")

    return {
        "_id": f"wallpaper-{wallpaper_id}",
        "WallpaperId": wallpaper_id,
        "AccessHash": int(entry.get("access_hash") or 0),
        "Slug": entry.get("slug") or "",
        "DocumentId": int(document["document_id"]) if document else 0,
        "MimeType": document["mime_type"] if document else None,
        "IsDefault": bool(entry.get("default")),
        "IsPattern": bool(entry.get("pattern")),
        "IsDark": bool(entry.get("dark")),
        "ForChat": False,
        # Everything Telegram lists belongs to the starting list of every account here. That is not the
        # same as the `default` wire flag: 76 of the 83 wallpapers carry `default`, all 83 are listed.
        "Listed": True,
        "CreatedBy": 0,
        # The order Telegram served them in, which is the order this server serves them in and the
        # order the list hash is folded over.
        "Order": int(entry.get("order") or 0),
        "Settings": entry.get("settings") or None,
    }


def import_wallpapers(db, minio, manifest: List[Dict[str, Any]], dry_run: bool) -> None:
    doc_col = db[DOCUMENT_COLLECTION]
    wallpaper_col = db[COLLECTION]

    bodies = documents = rows = 0
    for entry in manifest:
        document = entry.get("document")

        if document:
            path = Path(document["file"])
            if not path.exists():
                raise SystemExit(f"{path} not found; run --download first")

            doc_id = int(document["document_id"])
            if upload_body(minio, str(doc_id), path, document["mime_type"], dry_run):
                bodies += 1

            for thumb in document["thumbs"]:
                thumb_path = Path(thumb["file"])
                if not thumb_path.exists():
                    raise SystemExit(f"{thumb_path} not found; run --download first")

                if upload_body(minio, f"{doc_id}_{thumb['type']}", thumb_path, document["mime_type"],
                               dry_run):
                    bodies += 1

            if dry_run:
                print(f"    would write document {doc_id} ({document['size']} bytes)")
            else:
                doc_col.replace_one({"DocumentId": doc_id}, document_row(document), upsert=True)
                documents += 1

        row = catalogue_row(entry)
        if dry_run:
            print(f"    would write wallpaper {row['WallpaperId']} ({row['Slug'] or 'fill'})")
            continue

        wallpaper_col.replace_one({"WallpaperId": row["WallpaperId"]}, row, upsert=True)
        rows += 1

    print(f"  bodies uploaded: {bodies}, documents written: {documents}, wallpapers written: {rows}")


def cmd_import(dry_run: bool) -> None:
    if not MANIFEST_FILE.exists():
        raise SystemExit(f"{MANIFEST_FILE} not found; run --download first")

    manifest: List[Dict[str, Any]] = json.loads(MANIFEST_FILE.read_text())
    db, minio = connect_storage()

    print(f"=== wallpapers ({len(manifest)}) ===")
    import_wallpapers(db, minio, manifest, dry_run)


if __name__ == "__main__":
    dry = "--dry-run" in sys.argv
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import(dry)
    else:
        print(__doc__)
