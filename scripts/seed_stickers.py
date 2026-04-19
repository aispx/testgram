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
import time
from collections import defaultdict
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

PREMIUM_PROMO_SECTION_CANDIDATES = [
    ("more_upload", ["EmojiAnimations"]),
    ("faster_download", ["EmojiAroundAnimations"]),
    ("voice_to_text", ["EmojiShortAnimations"]),
    ("no_ads", ["EmojiAppearAnimations"]),
    ("unique_reactions", ["EmojiCenterAnimations"]),
    ("premium_stickers", ["AnimatedEmojies", "AnimatedEmoji"]),
    ("advanced_chat_management", ["EmojiGenericAnimations"]),
    ("profile_badge", ["StatusPack", "EmojiDefaultStatuses"]),
    ("animated_userpics", ["StatusPack", "EmojiDefaultStatuses"]),
    ("app_icons", ["GiftsPremium", "PremiumGifts"]),
]

EXTRA_SHORT_NAME_TARGETS = [
    "AnimatedEmojies",
    "EmojiAnimations",
    "EmojiAroundAnimations",
    "EmojiShortAnimations",
    "EmojiAppearAnimations",
    "EmojiCenterAnimations",
    "EmojiGenericAnimations",
    "RestrictedEmoji",
    "StatusPack",
    "GiftsPremium",
]

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


def build_section_short_name_map() -> Dict[str, List[str]]:
    mapping: Dict[str, List[str]] = defaultdict(list)
    for section, short_names in PREMIUM_PROMO_SECTION_CANDIDATES:
        for short_name in short_names:
            mapping[short_name].append(section)
    return dict(mapping)


def get_promo_sections_for_set(short_name: str, slug: str) -> List[str]:
    section_map = build_section_short_name_map()
    values = section_map.get(short_name, []).copy()
    for section in section_map.get(slug, []):
        if section not in values:
            values.append(section)
    return values


async def fetch_sticker_set(client, input_set, fallback_name: str, slug: Optional[str] = None) -> Optional[Dict[str, Any]]:
    from telethon.tl import functions

    try:
        result = await client(functions.messages.GetStickerSetRequest(stickerset=input_set, hash=0))
    except Exception as e:
        print(f"    ERROR: {e}")
        return None

    s = result.set
    effective_short_name = getattr(s, "short_name", None) or slug or fallback_name
    effective_slug = slug or effective_short_name
    promo_sections = get_promo_sections_for_set(effective_short_name, effective_slug)
    print(f"    id={s.id} short_name={effective_short_name} count={s.count}")

    set_dir = OUT_DIR / fallback_name
    set_dir.mkdir(parents=True, exist_ok=True)

    docs = []
    downloaded_count = 0
    skipped_count = 0
    for doc in result.documents:
        ext = get_file_ext(doc.mime_type, str(doc.id))
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
            doc_id = d.id if hasattr(d, "id") else d
            pack_doc_ids.append(doc_id)
        packs.append({
            "emoticon": p.emoticon,
            "documents": pack_doc_ids,
        })

    return {
        "manifest": {
            "name": fallback_name,
            "slug": effective_slug,
            "set_id": s.id,
            "set_access_hash": s.access_hash,
            "short_name": effective_short_name,
            "title": s.title,
            "documents": docs,
            "packs": packs,
            "promo_sections": promo_sections,
        },
        "downloaded_count": downloaded_count,
        "skipped_count": skipped_count,
    }


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
    processed_keys = set()

    print("\n=== Fetching featured sticker sets ===")
    try:
        featured_result = await client(functions.messages.GetFeaturedStickersRequest(hash=0))
        print(f"Found {len(featured_result.sets)} featured sticker sets")
        all_sets = list(featured_result.sets)
    except Exception as e:
        print(f"Could not fetch featured stickers: {e}")
        all_sets = []

    print(f"\n=== Processing {len(all_sets)} featured sticker sets ===")
    for i, stickerset in enumerate(all_sets):
        short_name = getattr(stickerset, "short_name", None)
        if not short_name:
            print(f"  [{i + 1}/{len(all_sets)}] Skipping set without short_name")
            continue

        print(f"  [{i + 1}/{len(all_sets)}] Processing: {short_name}")
        payload = await fetch_sticker_set(
            client,
            types.InputStickerSetShortName(short_name=short_name),
            short_name,
            short_name,
        )
        if payload is None:
            continue

        key = payload["manifest"]["short_name"]
        if key in processed_keys:
            continue
        processed_keys.add(key)
        manifest.append(payload["manifest"])
        downloaded_count += payload["downloaded_count"]
        skipped_count += payload["skipped_count"]

    print("\n=== Processing special sets ===")
    special_inputs = [
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

    for slug, name, input_set in special_inputs:
        print(f"  Processing: {name}")
        payload = await fetch_sticker_set(client, input_set, name, slug)
        if payload is None:
            continue

        key = payload["manifest"]["short_name"]
        if key in processed_keys:
            continue
        processed_keys.add(key)
        manifest.append(payload["manifest"])
        downloaded_count += payload["downloaded_count"]
        skipped_count += payload["skipped_count"]

    print("\n=== Processing extra short-name targets ===")
    for short_name in EXTRA_SHORT_NAME_TARGETS:
        if short_name in processed_keys:
            continue

        print(f"  Processing: {short_name}")
        payload = await fetch_sticker_set(
            client,
            types.InputStickerSetShortName(short_name=short_name),
            short_name,
            short_name,
        )
        if payload is None:
            continue

        key = payload["manifest"]["short_name"]
        if key in processed_keys:
            continue
        processed_keys.add(key)
        manifest.append(payload["manifest"])
        downloaded_count += payload["downloaded_count"]
        skipped_count += payload["skipped_count"]

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


def build_input_stickerset_id(set_id: int, set_access_hash: int) -> Dict[str, Any]:
    return {
        "_t": "TInputStickerSetID",
        "Id": set_id,
        "AccessHash": set_access_hash,
    }


def build_sticker_attribute(set_id: int, set_access_hash: int, alt: str, mask: bool = False) -> Dict[str, Any]:
    return {
        "_t": "TDocumentAttributeSticker",
        "Alt": alt,
        "Stickerset": build_input_stickerset_id(set_id, set_access_hash),
        "Mask": mask,
    }


def build_custom_emoji_attribute(set_id: int, set_access_hash: int, alt: str, free: bool, text_color: bool) -> Dict[str, Any]:
    return {
        "_t": "TDocumentAttributeCustomEmoji",
        "Free": free,
        "TextColor": text_color,
        "Alt": alt,
        "Stickerset": build_input_stickerset_id(set_id, set_access_hash),
    }


def merge_attributes(existing_attributes: Any, new_primary_attribute: Dict[str, Any]) -> List[Dict[str, Any]]:
    attributes = [new_primary_attribute]
    if isinstance(existing_attributes, list):
        for attribute in existing_attributes:
            if not isinstance(attribute, dict):
                continue
            if attribute.get("_t") in {"TDocumentAttributeSticker", "TDocumentAttributeCustomEmoji"}:
                continue
            attributes.append(attribute)
    return attributes


def build_set_keywords(entry: Dict[str, Any]) -> List[Dict[str, Any]]:
    keywords = []
    seen = set()
    doc_emoticon_map: Dict[int, str] = {}
    for pack in entry.get("packs", []):
        emoticon = pack.get("emoticon") or ""
        for raw_doc_id in pack.get("documents", []):
            doc_emoticon_map[to_int64(raw_doc_id) & 0x7FFFFFFFFFFFFFFF] = emoticon

    for raw_doc_id, emoticon in doc_emoticon_map.items():
        tokens = [emoticon]
        if entry.get("is_custom_emoji"):
            tokens.append((entry.get("title") or "").strip())
            tokens.append((entry.get("short_name") or entry.get("slug") or "").replace("_", " "))
        normalized = []
        for token in tokens:
            token = (token or "").strip().lower()
            if token and token not in normalized:
                normalized.append(token)
        if not normalized:
            continue
        dedupe_key = (raw_doc_id, tuple(normalized))
        if dedupe_key in seen:
            continue
        seen.add(dedupe_key)
        keywords.append({
            "DocumentId": raw_doc_id,
            "Keyword": normalized,
        })
    return keywords


def build_emoji_keyword_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    keyword_map: Dict[str, set[str]] = defaultdict(set)
    for entry in entries:
        if not entry.get("is_custom_emoji"):
            continue
        for pack in entry.get("packs", []):
            emoticon = (pack.get("emoticon") or "").strip()
            if not emoticon:
                continue
            keyword_map[emoticon].add(emoticon)
            title = (entry.get("title") or "").strip().lower()
            short_name = (entry.get("short_name") or entry.get("slug") or "").replace("_", " ").strip().lower()
            if title:
                keyword_map[title].add(emoticon)
            if short_name:
                keyword_map[short_name].add(emoticon)

    docs = []
    version = 1
    for keyword in sorted(keyword_map):
        docs.append({
            "_id": f"emoji-keyword-en-{keyword}",
            "LangCode": "en",
            "Keyword": keyword,
            "Emoticons": sorted(keyword_map[keyword]),
            "Version": version,
        })
        version += 1
    return docs


def build_emoji_group_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    custom_entries = [entry for entry in entries if entry.get("is_custom_emoji")]
    all_emoticons = []
    status_emoticons = []
    for entry in custom_entries:
        for pack in entry.get("packs", []):
            emoticon = (pack.get("emoticon") or "").strip()
            if not emoticon:
                continue
            all_emoticons.append(emoticon)
            if entry.get("channel_emoji_status") or entry.get("slug") == "emoji_default_statuses":
                status_emoticons.append(emoticon)

    def unique(values: List[str]) -> List[str]:
        result = []
        seen = set()
        for value in values:
            if value not in seen:
                seen.add(value)
                result.append(value)
        return result

    all_emoticons = unique(all_emoticons)
    status_emoticons = unique(status_emoticons)
    docs = []
    if all_emoticons:
        docs.extend([
            {
                "_id": "emoji-group-default-trending",
                "For": "default",
                "Kind": "default",
                "Title": "Trending",
                "IconEmojiId": 0,
                "Emoticons": all_emoticons[:64],
                "Order": 1,
                "Version": 1,
            },
            {
                "_id": "emoji-group-stickers-trending",
                "For": "stickers",
                "Kind": "default",
                "Title": "Trending",
                "IconEmojiId": 0,
                "Emoticons": all_emoticons[:64],
                "Order": 1,
                "Version": 1,
            },
            {
                "_id": "emoji-group-profile-photo-default",
                "For": "profile_photo",
                "Kind": "default",
                "Title": "Profile",
                "IconEmojiId": 0,
                "Emoticons": all_emoticons[:64],
                "Order": 1,
                "Version": 1,
            },
        ])
    if status_emoticons:
        docs.append({
            "_id": "emoji-group-status-default",
            "For": "status",
            "Kind": "default",
            "Title": "Status",
            "IconEmojiId": 0,
            "Emoticons": status_emoticons[:64],
            "Order": 1,
            "Version": 1,
        })
    premium_emoticons = unique([
        pack.get("emoticon") or ""
        for entry in custom_entries if not entry.get("free", False)
        for pack in entry.get("packs", [])
        if (pack.get("emoticon") or "").strip()
    ])
    if premium_emoticons:
        docs.append({
            "_id": "emoji-group-default-premium",
            "For": "default",
            "Kind": "premium",
            "Title": "Premium",
            "IconEmojiId": 0,
            "Order": 2,
            "Version": 1,
        })
    return docs


def build_featured_emoji_set_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    docs = []
    order = 1
    for entry in entries:
        if not entry.get("is_custom_emoji"):
            continue
        docs.append({
            "_id": f"featured-emoji-set-{entry['set_id']}",
            "StickerSetId": to_int64(entry["set_id"]),
            "Unread": False,
            "Order": order,
            "Version": 1,
        })
        order += 1
    return docs


def build_premium_promo_docs(entries: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    docs = []
    order = 1
    for section, short_names in PREMIUM_PROMO_SECTION_CANDIDATES:
        matched_entry = None
        for entry in entries:
            entry_short_name = entry.get("short_name") or ""
            entry_slug = entry.get("slug") or ""
            if entry_short_name in short_names or entry_slug in short_names:
                matched_entry = entry
                break
        if matched_entry is None:
            continue
        document_ids = [to_int64(d.get("doc_id")) & 0x7FFFFFFFFFFFFFFF for d in matched_entry.get("documents", [])]
        if not document_ids:
            continue
        docs.append({
            "_id": f"premium-promo-media-{section}",
            "Section": section,
            "StickerSetId": to_int64(matched_entry["set_id"]),
            "ShortName": matched_entry.get("short_name") or matched_entry.get("slug") or "",
            "DocumentId": document_ids[0],
            "Order": order,
            "Version": 1,
        })
        order += 1
    return docs


def infer_set_flags(entry: Dict[str, Any]) -> Dict[str, Any]:
    slug = entry.get("slug") or ""
    is_custom_emoji = bool(entry.get("is_custom_emoji")) or slug.startswith("emoji_") or slug.startswith("animated_emoji")
    text_color = bool(entry.get("text_color")) or slug in {
        "animated_emoji",
        "emoji_default_statuses",
        "emoji_default_topic_icons",
        "emoji_channel_statuses",
    }
    channel_emoji_status = bool(entry.get("channel_emoji_status")) or slug == "emoji_channel_statuses"
    free = bool(entry.get("free")) or slug in {
        "animated_emoji",
        "emoji_default_statuses",
        "emoji_default_topic_icons",
    }
    return {
        "is_custom_emoji": is_custom_emoji,
        "text_color": text_color,
        "channel_emoji_status": channel_emoji_status,
        "free": free,
    }


def extract_doc_alt(entry: Dict[str, Any], doc_id: int) -> str:
    for pack in entry.get("packs", []):
        for raw_doc_id in pack.get("documents", []):
            if (to_int64(raw_doc_id) & 0x7FFFFFFFFFFFFFFF) == doc_id:
                return (pack.get("emoticon") or "").strip()
    return ""


def update_metadata_collections(db, manifest: List[Dict[str, Any]]) -> None:
    emoji_keywords_col = db["emoji_keywords"]
    emoji_groups_col = db["emoji_groups"]
    featured_emoji_col = db["featured_emoji_sticker_sets"]
    premium_promo_col = db["premium_promo_media"]

    emoji_keyword_docs = build_emoji_keyword_docs(manifest)
    emoji_group_docs = build_emoji_group_docs(manifest)
    featured_docs = build_featured_emoji_set_docs(manifest)
    premium_promo_docs = build_premium_promo_docs(manifest)

    emoji_keywords_col.delete_many({})
    if emoji_keyword_docs:
        emoji_keywords_col.insert_many(emoji_keyword_docs)

    emoji_groups_col.delete_many({})
    if emoji_group_docs:
        emoji_groups_col.insert_many(emoji_group_docs)

    featured_emoji_col.delete_many({})
    if featured_docs:
        featured_emoji_col.insert_many(featured_docs)

    premium_promo_col.delete_many({})
    if premium_promo_docs:
        premium_promo_col.insert_many(premium_promo_docs)

    print(
        f"Updated emoji metadata: keywords={len(emoji_keyword_docs)}, groups={len(emoji_group_docs)}, "
        f"featured_sets={len(featured_docs)}, premium_promo={len(premium_promo_docs)}"
    )

    if premium_promo_docs:
        for promo_doc in premium_promo_docs:
            print(f"  premium section {promo_doc['Section']} -> {promo_doc['ShortName']} -> {promo_doc['DocumentId']}")
        missing_sections = [section for section, _ in PREMIUM_PROMO_SECTION_CANDIDATES if section not in {x['Section'] for x in premium_promo_docs}]
        if missing_sections:
            print(f"  WARNING: missing premium promo sections: {', '.join(missing_sections)}")
    else:
        print("  WARNING: no premium promo media mappings were generated")


def normalize_manifest(manifest: List[Dict[str, Any]]) -> List[Dict[str, Any]]:
    normalized = []
    for entry in manifest:
        flags = infer_set_flags(entry)
        set_keywords = build_set_keywords({**entry, **flags})
        normalized.append({**entry, **flags, "keywords": set_keywords})
    return normalized


def cmd_import():
    import pymongo
    from minio import Minio

    assert MINIO_ACCESS_KEY and MINIO_SECRET_KEY, \
        "Set MINIO_ACCESS_KEY and MINIO_SECRET_KEY env vars"

    if not MANIFEST_FILE.exists():
        print(f"ERROR: Manifest file {MANIFEST_FILE} not found. Run --download first.")
        return

    manifest = normalize_manifest(json.loads(MANIFEST_FILE.read_text()))
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
        to_int64(d["DocumentId"]): d
        for d in doc_col.find({}, {"DocumentId": 1, "Attributes2": 1, "AccessHash": 1, "FileReference": 1, "Date": 1, "DcId": 1, "MimeType": 1, "Size": 1, "Name": 1, "Version": 1})
    }
    print(f"Found {len(existing_docs)} existing documents in MongoDB")

    for entry in manifest:
        name = entry["name"]
        print(f"\n=== {name} ===")

        set_id = to_int64(entry["set_id"])
        set_access_hash = to_int64(entry["set_access_hash"])
        doc_ids = []

        for doc in entry["documents"]:
            doc_id = to_int64(doc["doc_id"]) & 0x7FFFFFFFFFFFFFFF
            doc_ids.append(doc_id)
            p = Path(doc["file"])
            mime = doc.get("mime", "application/octet-stream")
            alt = extract_doc_alt(entry, doc_id)
            if entry.get("is_custom_emoji"):
                primary_attribute = build_custom_emoji_attribute(set_id, set_access_hash, alt, entry.get("free", False), entry.get("text_color", False))
            else:
                primary_attribute = build_sticker_attribute(set_id, set_access_hash, alt)

            existing_doc = existing_docs.get(doc_id)
            if existing_doc is not None:
                try:
                    minio.stat_object(MINIO_BUCKET, str(doc_id))
                except Exception:
                    if p.exists():
                        data = p.read_bytes()
                        minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)
                        print(f"  Re-uploaded doc {doc_id}")
                    else:
                        print(f"  WARNING: doc {doc_id} missing in MinIO and file not found")

                merged_attributes = merge_attributes(existing_doc.get("Attributes2"), primary_attribute)
                update_fields = {
                    "Attributes2": merged_attributes,
                    "Version": max(int(existing_doc.get("Version", 1) or 1), 1),
                }
                if entry.get("is_custom_emoji"):
                    update_fields["Attributes"] = None
                doc_col.update_one({"DocumentId": doc_id}, {"$set": update_fields})
                print(f"  Updated doc {doc_id} attributes")
                continue

            if not p.exists():
                print(f"  MISSING file {p}")
                continue

            data = p.read_bytes()
            file_ref = list(os.urandom(16))
            access_hash = to_int64(doc.get("access_hash", 0)) or int.from_bytes(os.urandom(8), "little", signed=True)
            ext = doc.get("ext", "bin")

            minio.put_object(MINIO_BUCKET, str(doc_id), io.BytesIO(data), length=len(data), content_type=mime)

            attributes2 = [primary_attribute]
            document = {
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
                "Attributes": None,
                "Attributes2": attributes2,
                "CreatorId": None,
                "Fingerprint": None,
                "Md5CheckSum": None,
                "ThumbId": None,
                "VideoThumbId": None,
                "Version": 1,
            }
            doc_col.insert_one(document)
            existing_docs[doc_id] = document
            print(f"  Imported doc {doc_id} ({ext}, {len(data)} bytes)")

        packs = []
        for p in entry.get("packs", []):
            pack_doc_ids = [to_int64(d) & 0x7FFFFFFFFFFFFFFF for d in p.get("documents", [])]
            packs.append({
                "Emoticon": p["emoticon"],
                "Documents": pack_doc_ids,
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
                "Keywords": entry.get("keywords", []),
                "Emojis": entry.get("is_custom_emoji", False),
                "TextColor": entry.get("text_color", False),
                "ChannelEmojiStatus": entry.get("channel_emoji_status", False),
                "Version": 1,
            }},
            upsert=True,
        )
        print(f"  Upserted sticker set {set_id} ({len(doc_ids)} docs)")

    update_metadata_collections(db, manifest)
    print("\nDone!")


if __name__ == "__main__":
    import sys
    if "--download" in sys.argv:
        asyncio.run(cmd_download())
    elif "--import" in sys.argv:
        cmd_import()
    else:
        print(__doc__)
