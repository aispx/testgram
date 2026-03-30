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
       GRPC_ENDPOINT=localhost:8080 \
       python3 seed_reactions.py --import

  After import, rebuild messenger-command-server and messenger-query-server
  docker images (the handler is auto-generated from reactions_manifest.json).
"""
import asyncio, json, os, io, struct, time
from pathlib import Path

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


# ── Download ──────────────────────────────────────────────────────────────────

async def download_doc(client, emoji, field, doc, sem):
    from telethon.tl.types import Document, DocumentAttributeFilename
    if not isinstance(doc, Document):
        return field, None
    async with sem:
        is_tgs = any(isinstance(a, DocumentAttributeFilename) and a.file_name.endswith(".tgs")
                     for a in doc.attributes) or "tgs" in (doc.mime_type or "")
        ext = "tgs" if is_tgs else ("webp" if "webp" in (doc.mime_type or "") else "bin")
        out = OUT_DIR / f"{doc.id}_{field}.{ext}"
        if out.exists():
            return field, str(out)
        print(f"  [{emoji}] {field}: {doc.size}b", flush=True)
        try:
            data = await client.download_media(doc, file=bytes)
        except Exception as e:
            print(f"  [{emoji}] {field}: ERROR {e}")
            return field, None
        if data:
            out.write_bytes(data)
        return field, str(out) if data else None


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
        return {"emoji": r.reaction, "title": r.title,
                "inactive": r.inactive, "premium": r.premium, **dict(results)}

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
    col = mongo["tg"]["eventflow-documentreadmodel"]

    # Build name -> doc_id mapping from existing MongoDB records
    existing = {d["Name"]: to_int64(d["DocumentId"])
                for d in col.find({}, {"Name": 1, "DocumentId": 1, "AccessHash": 1,
                                       "FileReference": 1, "MimeType": 1, "Size": 1})}

    attrs_bytes = list(struct.pack("<II", 0x15c4b51c, 0))
    imported = 0

    for reaction in manifest:
        emoji = reaction["emoji"]
        for field in FIELDS:
            file_path = reaction.get(field)
            if not file_path:
                continue
            p = Path(file_path)
            if not p.exists():
                print(f"  [{emoji}] {field}: file missing {p}")
                continue

            # Extract original Telegram doc ID from filename
            orig_tg_id = p.stem.split("_")[0]
            name = p.name

            # Check if already in MongoDB
            if name in existing:
                doc_id = existing[name]
                # Check if file is in Minio
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                    continue  # already uploaded
                except Exception:
                    pass
                # Upload missing file
                data = p.read_bytes()
                minio.put_object(MINIO_BUCKET, str(doc_id),
                                 io.BytesIO(data), length=len(data))
                print(f"  [{emoji}] {field}: uploaded to minio doc_id={doc_id}")
                continue

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
            col.insert_one({
                "_id": f"documentreadmodel-{doc_id}",
                "Id": f"documentreadmodel-{doc_id}",
                "DocumentId": doc_id,
                "LocalFile": f"reactions_files/{name}",
                "AccessHash": access_hash,
                "FileReference": file_ref,
                "Date": int(time.time()),
                "DcId": DC_ID, "MimeType": mime, "Size": len(data),
                "Name": name, "Thumbs": None, "VideoThumbs": None,
                "Attributes": attrs_bytes, "Attributes2": None,
                "CreatorId": None, "Fingerprint": None,
                "Md5CheckSum": None, "ThumbId": None, "VideoThumbId": None,
                "Version": 1,
            })
            print(f"  [{emoji}] {field}: imported doc_id={doc_id}")
            imported += 1

    print(f"\nDone. Imported {imported} new documents.")
    print("Now run: python3 seed_reactions.py --generate-handler")
    print("Then rebuild messenger-command-server and messenger-query-server images.")


# ── Generate handler ──────────────────────────────────────────────────────────

HANDLER_PATH = Path(os.environ.get(
    "HANDLER_PATH",
    "../source/src/MyTelegram.Messenger/Handlers/LatestLayer/Messages/GetAvailableReactionsHandler.cs"
))

CS_FIELDS = ["StaticIcon", "AppearAnimation", "SelectAnimation",
             "ActivateAnimation", "EffectAnimation", "AroundAnimation", "CenterIcon"]


def cmd_generate_handler():
    import pymongo

    manifest = json.loads(MANIFEST_FILE.read_text())
    col = pymongo.MongoClient(MONGO_URL)["tg"]["eventflow-documentreadmodel"]

    def to_int64(v):
        if isinstance(v, dict):
            val = (v["high"] << 32) | (v["low"] & 0xFFFFFFFF)
            return val - (1 << 64) if val >= (1 << 63) else val
        return int(v)

    # Build doc_id -> metadata mapping (by filename stem)
    name_to_meta = {}
    import re
    pattern = "^[0-9]+_(static_icon|appear_animation|select_animation|activate_animation|effect_animation|around_animation|center_icon)"
    for d in col.find({"Name": {"$regex": pattern}}):
        did = to_int64(d["DocumentId"])
        ah  = to_int64(d["AccessHash"])
        stem = d["Name"].rsplit(".", 1)[0]  # e.g. "4988077362902991651_static_icon"
        name_to_meta[stem] = {"id": did, "ah": ah, "fr": d["FileReference"],
                              "mime": d["MimeType"], "size": d["Size"], "dc": d["DcId"]}

    def tdoc(file_path):
        if not file_path:
            return "new TDocumentEmpty()"
        stem = Path(file_path).stem  # e.g. "4988077362902991651_static_icon"
        d = name_to_meta.get(stem)
        if not d:
            return "new TDocumentEmpty()"
        fr = ", ".join(str(b) for b in d["fr"])
        return (f'new TDocument {{ Id = {d["id"]}L, AccessHash = {d["ah"]}L, '
                f'FileReference = new byte[]{{ {fr} }}, Date = 0, '
                f'MimeType = "{d["mime"]}", Size = {d["size"]}, '
                f'Thumbs = [], VideoThumbs = [], DcId = {d["dc"]}, '
                f'Attributes = new TVector<IDocumentAttribute>() }}')

    reactions_code = []
    for r in manifest:
        lines = [
            "        new TAvailableReaction",
            "        {",
            f'            Reaction = "{r["emoji"]}",',
            f'            Title = "{r["title"]}",',
            f'            Inactive = {"true" if r["inactive"] else "false"},',
            f'            Premium = {"true" if r["premium"] else "false"},',
        ]
        for f, cf in zip(FIELDS, CS_FIELDS):
            lines.append(f"            {cf} = {tdoc(r.get(f))},")
        lines.append("        },")
        reactions_code.append("\n".join(lines))

    content = (
        "namespace MyTelegram.Messenger.Handlers.LatestLayer.Messages;\n"
        "internal sealed class GetAvailableReactionsHandler : RpcResultObjectHandler"
        "<MyTelegram.Schema.Messages.RequestGetAvailableReactions, MyTelegram.Schema.Messages.IAvailableReactions>\n"
        "{\n"
        "    private static readonly IReadOnlyList<IAvailableReaction> DefaultReactions =\n"
        "    [\n"
        + "\n".join(reactions_code) + "\n"
        "    ];\n\n"
        "    private static readonly int _hash = DefaultReactions.Aggregate(0, "
        "(h, r) => h * 31 + ((TAvailableReaction)r).Reaction.GetHashCode()) & int.MaxValue;\n\n"
        "    protected override Task<MyTelegram.Schema.Messages.IAvailableReactions> HandleCoreAsync"
        "(IRequestInput input, MyTelegram.Schema.Messages.RequestGetAvailableReactions obj)\n"
        "    {\n"
        "        if (obj.Hash != 0 && obj.Hash == _hash)\n"
        "            return Task.FromResult<IAvailableReactions>(new TAvailableReactionsNotModified());\n"
        "        return Task.FromResult<IAvailableReactions>(new TAvailableReactions\n"
        "        {\n"
        "            Reactions = new TVector<IAvailableReaction>(DefaultReactions),\n"
        "            Hash = _hash\n"
        "        });\n"
        "    }\n"
        "}\n"
    )

    HANDLER_PATH.write_text(content)
    print(f"Handler written to {HANDLER_PATH} ({len(manifest)} reactions)")


# ── Entry point ───────────────────────────────────────────────────────────────

if __name__ == "__main__":
    import sys
    if "--import" in sys.argv:
        cmd_import()
    elif "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--generate-handler" in sys.argv:
        cmd_generate_handler()
    else:
        print(__doc__)
