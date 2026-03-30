"""
HTTP endpoint called by .NET SmsSender to deliver verification codes via Telegram bot.
POST /send  {"phone": "+79991234567", "code": "12345"}
"""
import asyncio
import logging
import os
import re
import aiosqlite
from aiohttp import web
from aiogram import Bot

BOT_TOKEN = os.environ["BOT_TOKEN"]
DB_PATH = os.environ.get("DB_PATH", "/root/testgram/bot/codes.db")

bot = Bot(token=BOT_TOKEN)


async def init_db():
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("""
            CREATE TABLE IF NOT EXISTS user_numbers (
                tg_id INTEGER,
                phone TEXT,
                PRIMARY KEY (tg_id, phone)
            )
        """)
        await db.commit()


async def get_owner_of(phone: str):
    clean = re.sub(r'\D', '', phone)
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute(
            "SELECT tg_id FROM user_numbers WHERE replace(replace(phone,'+',''),'-','')=?", (clean,)
        ) as cur:
            row = await cur.fetchone()
            return row[0] if row else None


async def handle_send(request: web.Request) -> web.Response:
    data = await request.json()
    phone = data.get("phone", "")
    code = data.get("code", "")
    if not phone or not code:
        return web.json_response({"ok": False, "error": "missing phone or code"}, status=400)

    owner = await get_owner_of(phone)
    if not owner:
        logging.warning(f"No owner for phone {phone}")
        return web.json_response({"ok": False, "error": "no owner"}, status=404)

    digits = re.sub(r'\D', '', phone)
    await bot.send_message(owner, f"📱 Код для {digits}: {code}")
    return web.json_response({"ok": True})


app = web.Application()
app.router.add_post("/send", handle_send)

if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO)
    loop = asyncio.get_event_loop()
    loop.run_until_complete(init_db())
    web.run_app(app, host="127.0.0.1", port=5005)
