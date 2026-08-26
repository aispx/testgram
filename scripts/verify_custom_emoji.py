#!/usr/bin/env python3
"""End-to-end check of the /api/custom-emoji surface against a running deployment.

Covers the three things the page makes the server responsible for:

  * `messages.searchCustomEmoji` — read twice, the second time quoting the hash the server returned.
    A `emojiListNotModified` is the point: the hash a client sends is one it computed itself with the
    documented algorithm (tdlib `StickersManager::reload_found_stickers`), so a server using any
    other algorithm can never answer notModified and every client re-fetches the list forever.

  * the emoji keyword lists — `getEmojiKeywordsLanguages` must only name languages that actually
    have keywords, `getEmojiKeywords` must return them, and `getEmojiKeywordsDifference` quoting the
    returned version must come back empty at the same version.

  * `messageEntityCustomEmoji` on send — a mismatched entity is ignored, not refused, and no more
    than `message_animated_emoji_max` of them survive. Both are checked by sending to Saved Messages
    and reading the stored message back.

Setup is the same as verify_stickers.py — this server advertises its own RSA key, which Telethon does
not ship:

    docker cp <auth-server>:/app/private.pkcs8.key /tmp/priv.key
    openssl rsa -in /tmp/priv.key -pubout | openssl rsa -pubin -RSAPublicKey_out -out server_pub.pem
    shred -u /tmp/priv.key

Usage:
    TG_SERVER_IP=127.0.0.1 TG_SERVER_PORT=20443 TG_API_ID=... TG_API_HASH=... \\
    TG_SERVER_PUBKEY=server_pub.pem TG_PHONE=... \\
    MONGO_URL=mongodb://172.23.0.8:27017 python3 verify_custom_emoji.py

Without TG_SESSION_STRING the script logs in with TG_PHONE and reads the code out of
`eventflow-appcodereadmodel`, then prints the session string for reuse.
"""
import asyncio
import os
import sys

from telethon import TelegramClient
from telethon.crypto import rsa as trsa
from telethon.sessions import StringSession
from telethon.network import ConnectionTcpAbridged
from telethon.tl import functions, types

SERVER_IP = os.environ.get("TG_SERVER_IP", "127.0.0.1")
SERVER_PORT = int(os.environ.get("TG_SERVER_PORT", "20443"))
API_ID = int(os.environ.get("TG_API_ID", "0"))
API_HASH = os.environ.get("TG_API_HASH", "")
PUBKEY = os.environ.get("TG_SERVER_PUBKEY", "server_pub.pem")
SESSION_STRING = os.environ.get("TG_SESSION_STRING", "")
PHONE = os.environ.get("TG_PHONE", "")
MONGO_URL = os.environ.get("MONGO_URL", "mongodb://localhost:27017")
MONGO_DB = os.environ.get("MONGO_DB", "tg")

EMOJI_SET = os.environ.get("TG_EMOJI_SET", "Topics")
UNKNOWN_DOCUMENT_ID = 1

ok: list[str] = []
bad: list[str] = []


def note(good: bool, text: str) -> None:
    (ok if good else bad).append(text)
    print(("  ok    " if good else "  FAIL  ") + text)


def utf16_len(text: str) -> int:
    """
    Entity offsets and lengths are counted in UTF-16 code units, not code points - see
    https://corefork.telegram.org/api/entities#entity-length . Python's len() gives code points, so a
    single emoji outside the BMP would be described as length 1 and the server rightly answers
    ENTITY_BOUNDS_INVALID for splitting a surrogate pair.
    """
    return len(text.encode("utf-16-le")) // 2


async def connect():
    if not API_ID or not API_HASH:
        print("ERROR: set TG_API_ID and TG_API_HASH", file=sys.stderr)
        return None

    trsa.add_key(open(PUBKEY).read(), old=False)
    TelegramClient._on_login = lambda self, user: asyncio.sleep(0)

    session = StringSession(SESSION_STRING) if SESSION_STRING else StringSession()
    if not SESSION_STRING:
        session.set_dc(1, SERVER_IP, SERVER_PORT)

    client = TelegramClient(session, API_ID, API_HASH, connection=ConnectionTcpAbridged, catch_up=False)
    await asyncio.wait_for(client.connect(), timeout=40)

    if SESSION_STRING:
        return client

    if not PHONE:
        print("ERROR: set TG_SESSION_STRING or TG_PHONE", file=sys.stderr)
        return None

    sent = await client(functions.auth.SendCodeRequest(
        phone_number=PHONE, api_id=API_ID, api_hash=API_HASH, settings=types.CodeSettings()))

    from pymongo import MongoClient

    row = MongoClient(MONGO_URL)[MONGO_DB]["eventflow-appcodereadmodel"].find_one(
        {"PhoneCodeHash": sent.phone_code_hash})
    if not row:
        print("ERROR: no login code stored for this request", file=sys.stderr)
        return None

    await client(functions.auth.SignInRequest(phone_number=PHONE, phone_code_hash=sent.phone_code_hash,
                                              phone_code=row["Code"]))
    print("TG_SESSION_STRING=" + StringSession.save(client.session))

    return client


async def check_keywords(client):
    print("emoji keywords")
    languages = await client(functions.messages.GetEmojiKeywordsLanguagesRequest(
        lang_codes=["en", "ru", "zz"]))
    codes = [language.lang_code for language in languages]
    note("en" in codes, f"getEmojiKeywordsLanguages returns en: {codes}")
    note("zz" not in codes,
         "a language with no keywords is not advertised (a client caches the empty answer)")

    for code in codes:
        difference = await client(functions.messages.GetEmojiKeywordsRequest(lang_code=code))
        count = len(difference.keywords)
        note(count > 0, f"getEmojiKeywords({code}): {count} keywords, version={difference.version}")
        note(difference.version > 0,
             f"getEmojiKeywords({code}) version is a real revision, not 0")

        again = await client(functions.messages.GetEmojiKeywordsDifferenceRequest(
            lang_code=code, from_version=difference.version))
        note(len(again.keywords) == 0 and again.version == difference.version,
             f"getEmojiKeywordsDifference({code}, from={difference.version}): "
             f"{len(again.keywords)} keywords at version {again.version}")


async def emoji_set_documents(client):
    """The documents of one custom-emoji set, to have real ids to work with."""
    full = await client(functions.messages.GetStickerSetRequest(
        stickerset=types.InputStickerSetShortName(short_name=EMOJI_SET), hash=0))

    return full.documents


async def check_search(client, documents):
    print("searchCustomEmoji")
    alt = ""
    for document in documents:
        for attribute in document.attributes:
            if isinstance(attribute, types.DocumentAttributeCustomEmoji) and attribute.alt:
                alt = attribute.alt
                break
        if alt:
            break

    if not alt:
        note(False, f"no custom-emoji document with an alt in {EMOJI_SET}")
        return

    first = await client(functions.messages.SearchCustomEmojiRequest(emoticon=alt, hash=0))
    if isinstance(first, types.EmojiListNotModified):
        note(False, f"searchCustomEmoji({alt}) answered notModified to hash = 0")
        return

    note(len(first.document_id) > 0, f"searchCustomEmoji({alt}): {len(first.document_id)} ids")
    note(first.hash != 0, f"searchCustomEmoji({alt}) hash is non-zero: {first.hash}")

    again = await client(functions.messages.SearchCustomEmojiRequest(emoticon=alt, hash=first.hash))
    note(isinstance(again, types.EmojiListNotModified),
         f"searchCustomEmoji({alt}) quoting hash={first.hash}: {type(again).__name__}")


async def check_documents(client, documents):
    print("getCustomEmojiDocuments")
    ids = [document.id for document in documents][:5]
    if not ids:
        note(False, "no documents to resolve")
        return

    # A duplicate and an unresolvable id: clients match the answer against their request positionally.
    requested = ids + [ids[0], UNKNOWN_DOCUMENT_ID]
    resolved = await client(functions.messages.GetCustomEmojiDocumentsRequest(document_id=requested))

    note(len(resolved) == len(requested),
         f"{len(requested)} ids in, {len(resolved)} documents out")
    note(all(getattr(item, "id", None) == want for item, want in zip(resolved, requested)),
         "every answer sits at the position of its id, duplicates included")
    note(isinstance(resolved[-1], types.DocumentEmpty),
         f"an unresolvable id comes back as documentEmpty: {type(resolved[-1]).__name__}")

    attributes = [attribute for item in resolved if isinstance(item, types.Document)
                  for attribute in item.attributes
                  if isinstance(attribute, types.DocumentAttributeCustomEmoji)]
    note(len(attributes) == len(ids) + 1,
         f"{len(attributes)} of the resolved documents carry documentAttributeCustomEmoji")
    note(all(attribute.free for attribute in attributes),
         "every custom emoji is free (a non-free one locks its whole pack for non-Premium)")
    note(all(isinstance(attribute.stickerset, types.InputStickerSetID) and attribute.stickerset.id != 0
             for attribute in attributes),
         "every custom emoji names its stickerset with a non-zero id")

    too_many = await client(functions.messages.GetCustomEmojiDocumentsRequest(
        document_id=[UNKNOWN_DOCUMENT_ID] * 300))
    note(len(too_many) == 200, f"300 ids are capped at 200: got {len(too_many)}")


async def sent_entities(client, text, entities):
    """Sends to Saved Messages and returns the entities the server stored."""
    await client(functions.messages.SendMessageRequest(
        peer=types.InputPeerSelf(), message=text, random_id=int.from_bytes(os.urandom(7), "big"),
        entities=entities))
    history = await client(functions.messages.GetHistoryRequest(
        peer=types.InputPeerSelf(), offset_id=0, offset_date=None, add_offset=0, limit=1,
        max_id=0, min_id=0, hash=0))
    message = history.messages[0]

    return message.message, list(message.entities or [])


async def check_entities(client, documents):
    print("messageEntityCustomEmoji on send")
    document = next((d for d in documents
                     if any(isinstance(a, types.DocumentAttributeCustomEmoji) and a.alt
                            for a in d.attributes)), None)
    if document is None:
        note(False, f"no custom-emoji document with an alt in {EMOJI_SET}")
        return

    alt = next(a.alt for a in document.attributes
               if isinstance(a, types.DocumentAttributeCustomEmoji) and a.alt)

    text, entities = await sent_entities(client, alt, [
        types.MessageEntityCustomEmoji(offset=0, length=utf16_len(alt), document_id=document.id)])
    kept = [e for e in entities if isinstance(e, types.MessageEntityCustomEmoji)]
    note(len(kept) == 1 and kept[0].document_id == document.id,
         f"an entity wrapping its own alt survives: {len(kept)} kept")

    for label, entity in [
        ("an unknown document id", types.MessageEntityCustomEmoji(
            offset=0, length=utf16_len(alt), document_id=UNKNOWN_DOCUMENT_ID)),
        ("a zero document id", types.MessageEntityCustomEmoji(
            offset=0, length=utf16_len(alt), document_id=0)),
    ]:
        try:
            _, entities = await sent_entities(client, alt, [entity])
            kept = [e for e in entities if isinstance(e, types.MessageEntityCustomEmoji)]
            note(not kept, f"{label} is ignored and the text still goes out")
        except Exception as error:  # noqa: BLE001 - the point is that nothing is raised
            note(False, f"{label} was refused: {type(error).__name__} {error}")

    # message_animated_emoji_max, read from the server's own appConfig.
    config = await client(functions.help.GetAppConfigRequest(hash=0))
    limit = next((int(value.value.value) for value in config.config.value
                  if value.key == "message_animated_emoji_max"), 0)
    note(limit > 0, f"appConfig advertises message_animated_emoji_max={limit}")
    if limit <= 0 or limit >= 100:
        # The validator refuses more than 100 entities outright, so a limit that high cannot be
        # exercised from here without tripping ENTITIES_TOO_LONG first.
        note(True, f"limit {limit} not exercised: over the {100} entity ceiling")
        return

    count = limit + 2
    try:
        _, entities = await sent_entities(client, alt * count, [
            types.MessageEntityCustomEmoji(offset=index * utf16_len(alt), length=utf16_len(alt),
                                           document_id=document.id)
            for index in range(count)])
        kept = [e for e in entities if isinstance(e, types.MessageEntityCustomEmoji)]
        note(len(kept) == limit, f"{count} custom emojis are trimmed to {limit}: kept {len(kept)}")
    except Exception as error:  # noqa: BLE001
        note(False, f"{count} custom emojis were refused: {type(error).__name__} {error}")


async def main():
    client = await connect()
    if client is None:
        sys.exit(1)

    try:
        await check_keywords(client)
        documents = await emoji_set_documents(client)
        print(f"{EMOJI_SET}: {len(documents)} documents")
        await check_search(client, documents)
        await check_documents(client, documents)
        await check_entities(client, documents)
    finally:
        await client.disconnect()

    print(f"\n{len(ok)} ok, {len(bad)} failed")
    for text in bad:
        print("  FAIL  " + text)
    sys.exit(1 if bad else 0)


if __name__ == "__main__":
    asyncio.run(main())
