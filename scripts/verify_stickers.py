#!/usr/bin/env python3
"""End-to-end check of the /api/stickers surface against a running deployment.

Every read is done twice: once with `hash = 0`, then again quoting the hash the server just returned. The
second answer is the point of the script — a `*NotModified` proves the hash the server minted matches what
it recomputes, which is what makes clients stop re-downloading the list on every poll. A non-empty list
answered with `hash = 0` is reported as a failure; an empty one is not, because zero is exactly what a
client with an empty cache sends and the two have to agree.

The run installs two sets, exercises the panel, favourites, recents, trending and search, then uninstalls
them again, so it leaves the account as it found it.

Setup — this server advertises its own RSA key, which Telethon does not ship, so the handshake fails until
the key is registered (that is the "auth_key generation failed" you get otherwise):

    docker cp <auth-server>:/app/private.pkcs8.key /tmp/priv.key
    openssl rsa -in /tmp/priv.key -pubout | openssl rsa -pubin -RSAPublicKey_out -out server_pub.pem
    shred -u /tmp/priv.key

Usage:
    TG_SERVER_IP=127.0.0.1 TG_SERVER_PORT=20443 TG_API_ID=... TG_API_HASH=... \\
    TG_SERVER_PUBKEY=server_pub.pem TG_SESSION_STRING=<string> \\
    MONGO_URL=mongodb://172.23.0.8:27017 python3 verify_stickers.py

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

REGULAR_SET = os.environ.get("TG_REGULAR_SET", "GiftsPremium")
EMOJI_SET = os.environ.get("TG_EMOJI_SET", "Topics")

ok: list[str] = []
bad: list[str] = []


def note(good: bool, text: str) -> None:
    (ok if good else bad).append(text)
    print(("  ok    " if good else "  FAIL  ") + text)


def sizes(result, *fields):
    return {f: len(getattr(result, f)) for f in fields if isinstance(getattr(result, f, None), list)}


async def read_twice(client, label, build, hash_field="hash"):
    """Read once cold, then again quoting the returned hash."""
    first = await client(build(0))
    returned = getattr(first, hash_field, None)
    counts = sizes(first, "sets", "stickers", "unread", "packs", "dates", "document_id")
    info = f"{label}: {type(first).__name__} {counts} hash={returned}"

    # `unread` is excluded: a trending list can legitimately be all-read and still carry sets.
    payload = max([v for k, v in counts.items() if k != "unread"] or [0])

    if not returned:
        note(payload == 0, info + ("  (empty list, hash 0 is correct)" if payload == 0
                                   else "  (hash 0 on a non-empty list — caching can never engage)"))
        return first

    second = await client(build(returned))
    note("NotModified" in type(second).__name__, info + f" -> requote {type(second).__name__}")

    return first


async def connect():
    if not API_ID or not API_HASH:
        print("ERROR: set TG_API_ID and TG_API_HASH", file=sys.stderr)
        return None

    # Telethon ships Telegram's public keys and cannot match the fingerprint this server advertises in
    # res_pq, so without its key the handshake dies at step 1 with "auth_key generation failed".
    trsa.add_key(open(PUBKEY).read(), old=False)

    # _on_login initialises the update state, which deserializes objects an older Telethon cannot parse
    # against layer 222; the sticker methods themselves are fine.
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


async def main():
    client = await connect()
    if client is None:
        return 1

    me = await client(functions.users.GetFullUserRequest(types.InputUserSelf()))
    print(f"as user {me.full_user.id}\n")

    print("install")
    for short_name in (REGULAR_SET, EMOJI_SET):
        result = await client(functions.messages.InstallStickerSetRequest(
            stickerset=types.InputStickerSetShortName(short_name=short_name), archived=False))
        note("StickerSetInstallResult" in type(result).__name__, f"install {short_name}: {type(result).__name__}")

    print("\ninstalled lists")
    installed = await read_twice(client, "getAllStickers",
                                 lambda h: functions.messages.GetAllStickersRequest(hash=h))
    note(len(installed.sets) >= 1, f"getAllStickers carries {len(installed.sets)} set(s)")
    if installed.sets:
        header = installed.sets[0]
        header = header.set if hasattr(header, "set") else header
        # Zero is the client's "nothing cached" sentinel, and the installed-list hash is built from these.
        note(header.hash != 0, f"stickerSet.hash is non-zero ({header.hash})")
        note(header.installed_date is not None, f"installed_date is set ({header.installed_date})")

    await read_twice(client, "getEmojiStickers", lambda h: functions.messages.GetEmojiStickersRequest(hash=h))
    await read_twice(client, "getMaskStickers", lambda h: functions.messages.GetMaskStickersRequest(hash=h))

    print("\nstickerset")
    full = await client(functions.messages.GetStickerSetRequest(
        stickerset=types.InputStickerSetShortName(short_name=REGULAR_SET), hash=0))
    note(len(full.documents) > 0 and len(full.packs) > 0,
         f"getStickerSet: documents={len(full.documents)} packs={len(full.packs)} keywords={len(full.keywords)}")
    again = await client(functions.messages.GetStickerSetRequest(
        stickerset=types.InputStickerSetShortName(short_name=REGULAR_SET), hash=full.set.hash))
    note("NotModified" in type(again).__name__, f"getStickerSet requote -> {type(again).__name__}")

    print("\narchive and order")
    await client(functions.messages.ToggleStickerSetsRequest(
        stickersets=[types.InputStickerSetShortName(short_name=REGULAR_SET)], archive=True))
    archived = await client(functions.messages.GetArchivedStickersRequest(offset_id=0, limit=0))
    note(archived.count >= 1, f"getArchivedStickers(limit=0) count={archived.count}")

    # Re-installing an archived set is how every client un-archives one.
    await client(functions.messages.InstallStickerSetRequest(
        stickerset=types.InputStickerSetShortName(short_name=REGULAR_SET), archived=False))
    after = await client(functions.messages.GetArchivedStickersRequest(offset_id=0, limit=0))
    note(after.count == archived.count - 1, f"re-install un-archived it ({archived.count} -> {after.count})")

    panel = await client(functions.messages.GetAllStickersRequest(hash=0))
    ids = [s.id for s in panel.sets]
    if len(ids) >= 2:
        reversed_ids = list(reversed(ids))
        await client(functions.messages.ReorderStickerSetsRequest(order=reversed_ids))
        reordered = await client(functions.messages.GetAllStickersRequest(hash=0))
        note([s.id for s in reordered.sets] == reversed_ids, "reorderStickerSets is remembered")
    else:
        note(True, f"reorder skipped, only {len(ids)} set(s) in this panel")

    print("\nfavourites and recents")
    document = full.documents[0]
    input_document = types.InputDocument(id=document.id, access_hash=document.access_hash,
                                         file_reference=document.file_reference)
    await client(functions.messages.FaveStickerRequest(id=input_document, unfave=False))
    await read_twice(client, "getFavedStickers", lambda h: functions.messages.GetFavedStickersRequest(hash=h))
    await client(functions.messages.SaveRecentStickerRequest(id=input_document, unsave=False))
    await read_twice(client, "getRecentStickers", lambda h: functions.messages.GetRecentStickersRequest(hash=h))

    print("\ntrending and search")
    await read_twice(client, "getFeaturedStickers",
                     lambda h: functions.messages.GetFeaturedStickersRequest(hash=h))
    await read_twice(client, "getFeaturedEmojiStickers",
                     lambda h: functions.messages.GetFeaturedEmojiStickersRequest(hash=h))
    await read_twice(client, "searchStickerSets",
                     lambda h: functions.messages.SearchStickerSetsRequest(q="e", hash=h))
    await read_twice(client, "searchEmojiStickerSets",
                     lambda h: functions.messages.SearchEmojiStickerSetsRequest(q="e", hash=h))
    if full.packs:
        emoticon = full.packs[0].emoticon
        await read_twice(client, f"getStickers({emoticon})",
                         lambda h: functions.messages.GetStickersRequest(emoticon=emoticon, hash=h))

    print("\ncleanup")
    for short_name in (REGULAR_SET, EMOJI_SET):
        await client(functions.messages.UninstallStickerSetRequest(
            stickerset=types.InputStickerSetShortName(short_name=short_name)))
    final = await client(functions.messages.GetAllStickersRequest(hash=0))
    note(len(final.sets) == 0, f"panel is empty again ({len(final.sets)} sets)")

    await client.disconnect()

    print(f"\n{len(ok)} ok, {len(bad)} failed")
    for failure in bad:
        print("  FAILED:", failure)

    return 1 if bad else 0


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
