import asyncio
import json
import logging
import os
import re
import aiosqlite
import aio_pika
import aiohttp
from aiohttp import web
from aiogram import Bot, Dispatcher, F, Router
from aiogram.filters import CommandStart
from aiogram.types import Message, CallbackQuery, InlineKeyboardMarkup, InlineKeyboardButton

# Load .env file
_env_path = os.path.join(os.path.dirname(__file__), ".env")
if os.path.exists(_env_path):
    with open(_env_path) as _f:
        for _line in _f:
            _line = _line.strip()
            if _line and not _line.startswith("#") and "=" in _line:
                _k, _v = _line.split("=", 1)
                os.environ[_k.strip()] = _v.strip()

BOT_TOKEN = os.environ["BOT_TOKEN"]
BOT_TOKEN2 = os.environ.get("BOT_TOKEN2", "")
DB_PATH = os.environ.get("DB_PATH", "/root/testgram/bot/codes.db")
MAX_NUMBERS = 2

def _get_rabbitmq_url() -> str:
    """Get RabbitMQ URL, auto-detecting container IP if placeholder is set."""
    url = os.environ.get("RABBITMQ_URL", "")
    if not url or "AUTO" in url:
        import subprocess, re as _re
        container = os.environ.get("RABBITMQ_CONTAINER", "compose_rabbitmq_1")
        user = os.environ.get("RABBITMQ_USER", "test")
        password = os.environ.get("RABBITMQ_PASSWORD", "")
        try:
            out = subprocess.check_output(
                ["docker", "inspect", "-f", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", container],
                text=True
            ).strip()
            ip = _re.search(r'\d+\.\d+\.\d+\.\d+', out)
            if ip:
                return f"amqp://{user}:{password}@{ip.group()}/"
        except Exception as e:
            logging.warning(f"Could not auto-detect RabbitMQ IP: {e}")
    return url or "amqp://test:password@localhost/"

RABBITMQ_URL = _get_rabbitmq_url()
EXCHANGE = "mytelegram_exchange"
ROUTING_KEY = "AppCodeCreatedIntegrationEvent"

bot = Bot(token=BOT_TOKEN)
bot2 = Bot(token=BOT_TOKEN2) if BOT_TOKEN2 else None
dp = Dispatcher()
dp2 = Dispatcher() if BOT_TOKEN2 else None
waiting_for_phone: set[int] = set()


async def init_db():
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("""
            CREATE TABLE IF NOT EXISTS user_numbers (
                tg_id INTEGER, phone TEXT, PRIMARY KEY (tg_id, phone)
            )
        """)
        await db.commit()


async def get_user_numbers(tg_id: int) -> list:
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute("SELECT phone FROM user_numbers WHERE tg_id=?", (tg_id,)) as cur:
            return [row[0] for row in await cur.fetchall()]


async def get_owner_of(phone: str):
    clean = re.sub(r'\D', '', phone)
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute(
            "SELECT tg_id FROM user_numbers WHERE replace(replace(phone,'+',''),'-','')=?", (clean,)
        ) as cur:
            row = await cur.fetchone()
            return row[0] if row else None


async def add_number(tg_id: int, phone: str) -> str:
    numbers = await get_user_numbers(tg_id)
    if len(numbers) >= MAX_NUMBERS:
        return "limit"
    owner = await get_owner_of(phone)
    if owner and owner != tg_id:
        return "taken"
    if phone in numbers:
        return "exists"
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("INSERT OR IGNORE INTO user_numbers VALUES (?,?)", (tg_id, phone))
        await db.commit()
    return "ok"


async def remove_number(tg_id: int, phone: str):
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("DELETE FROM user_numbers WHERE tg_id=? AND phone=?", (tg_id, phone))
        await db.commit()


def numbers_keyboard(numbers: list) -> InlineKeyboardMarkup:
    buttons = [[InlineKeyboardButton(text=f"❌ {n}", callback_data=f"del:{n}")] for n in numbers]
    buttons.append([InlineKeyboardButton(text="➕ Добавить номер", callback_data="add")])
    return InlineKeyboardMarkup(inline_keyboard=buttons)


def status_text(numbers: list) -> str:
    if not numbers:
        return f"📱 Нет привязанных номеров.\nЛимит: 0/{MAX_NUMBERS}"
    nums_str = "\n".join(f"  • {n}" for n in numbers)
    return f"📱 Ваши номера TestGram:\n{nums_str}\n\nЛимит: {len(numbers)}/{MAX_NUMBERS}"


@dp.message(CommandStart())
async def cmd_start(message: Message):
    waiting_for_phone.discard(message.from_user.id)
    numbers = await get_user_numbers(message.from_user.id)
    await message.answer(status_text(numbers), reply_markup=numbers_keyboard(numbers))


@dp.callback_query(F.data == "add")
async def cb_add(call: CallbackQuery):
    numbers = await get_user_numbers(call.from_user.id)
    if len(numbers) >= MAX_NUMBERS:
        await call.answer(f"Лимит {MAX_NUMBERS} номера достигнут", show_alert=True)
        return
    waiting_for_phone.add(call.from_user.id)
    await call.message.answer("📱 Введите номер: +79991234567")
    await call.answer()


@dp.callback_query(F.data.startswith("del:"))
async def cb_del(call: CallbackQuery):
    phone = call.data[4:]
    numbers = await get_user_numbers(call.from_user.id)
    if phone not in numbers:
        await call.answer("Номер не найден", show_alert=True)
        return
    await remove_number(call.from_user.id, phone)
    numbers = await get_user_numbers(call.from_user.id)
    await call.message.edit_text(
        f"✅ Номер {phone} отвязан.\n\n{status_text(numbers)}",
        reply_markup=numbers_keyboard(numbers)
    )
    await call.answer()


@dp.message()
async def handle_phone(message: Message):
    if message.from_user.id not in waiting_for_phone:
        return
    phone = message.text.strip()
    clean = "+" + re.sub(r'\D', '', phone)
    if len(clean) < 5:
        await message.answer("❌ Неверный формат. Попробуйте ещё раз:")
        return
    waiting_for_phone.discard(message.from_user.id)
    result = await add_number(message.from_user.id, clean)
    numbers = await get_user_numbers(message.from_user.id)
    if result == "ok":
        text = f"✅ Номер {clean} добавлен.\n\n{status_text(numbers)}"
    elif result == "taken":
        text = f"❌ Номер {clean} уже занят другим пользователем.\n\n{status_text(numbers)}"
    elif result == "limit":
        text = f"❌ Достигнут лимит {MAX_NUMBERS} номера.\n\n{status_text(numbers)}"
    else:
        text = f"ℹ️ Номер {clean} уже добавлен.\n\n{status_text(numbers)}"
    await message.answer(text, reply_markup=numbers_keyboard(numbers))


if dp2:
    dp2.message.register(cmd_start, CommandStart())
    dp2.callback_query.register(cb_add, F.data == "add")
    dp2.callback_query.register(cb_del, F.data.startswith("del:"))
    dp2.message.register(handle_phone)


def read_tl_string(body: bytes, offset: int):
    """Read TL-encoded string (MTProto format used by MyTelegram serializer)."""
    first = body[offset]
    offset += 1
    if first == 254:
        length = body[offset] | (body[offset+1] << 8) | (body[offset+2] << 16)
        offset += 3
        padding = length % 4
    else:
        length = first
        padding = (length + 1) % 4
    text = body[offset:offset+length].decode('utf-8')
    offset += length
    if padding > 0:
        offset += 4 - padding
    return text, offset


def parse_code_event(body: bytes):
    """
    AppCodeCreatedIntegrationEvent binary layout:
      userId(int64 LE) + WriteString(phoneNumber) + WriteString(code) + expire(int32 LE)
    WriteString = 0x00 if null, else 0x01 + TL-string
    """
    try:
        offset = 0
        # userId: 8 bytes LE
        offset += 8
        # phoneNumber: nullable flag byte + TL-string
        if body[offset] == 0:
            return None, None
        offset += 1
        phone, offset = read_tl_string(body, offset)
        # code: nullable flag byte + TL-string
        if body[offset] == 0:
            return None, None
        offset += 1
        code, offset = read_tl_string(body, offset)
        return phone, code
    except Exception as e:
        logging.error(f"Failed to parse event: {e}, body={body.hex()}")
        return None, None


async def send_code_to_owner(owner: int, digits: str, code: str):
    text = f"📱 Код для {digits}: <code>{code}</code>"
    for b in ([bot] + ([bot2] if bot2 else [])):
        try:
            await b.send_message(owner, text, parse_mode="HTML")
        except Exception as e:
            logging.error(f"send_code_to_owner bot={b.id if hasattr(b,'id') else '?'} error: {e}")


async def rabbitmq_consumer():
    while True:
        try:
            conn = await aio_pika.connect(RABBITMQ_URL)
            channel = await conn.channel()
            exchange = await channel.get_exchange(EXCHANGE)
            queue = await channel.declare_queue("tgbot_codes", durable=True)
            await queue.bind(exchange, routing_key=ROUTING_KEY)

            async with queue.iterator() as q:
                async for message in q:
                    async with message.process():
                        phone, code = parse_code_event(bytes(message.body))
                        if not phone or not code:
                            continue
                        clean = "+" + re.sub(r'\D', '', phone)
                        owner = await get_owner_of(clean)
                        if owner:
                            digits = re.sub(r'\D', '', phone)
                            await send_code_to_owner(owner, digits, code)
                        else:
                            logging.warning(f"No owner for {phone}")
        except Exception as e:
            logging.error(f"RabbitMQ error: {e}, reconnecting in 5s...", exc_info=True)
            await asyncio.sleep(5)


async def handle_send(request: web.Request) -> web.Response:
    try:
        data = await request.json()
        phone = data.get("phone", "")
        code = data.get("code", "")
        clean = "+" + re.sub(r'\D', '', phone)
        owner = await get_owner_of(clean)
        if owner:
            digits = re.sub(r'\D', '', phone)
            await send_code_to_owner(owner, digits, code)
            return web.json_response({"ok": True})
        else:
            logging.warning(f"No owner for {phone}")
            return web.json_response({"ok": False, "error": "no owner"}, status=404)
    except Exception as e:
        logging.error(f"handle_send error: {e}")
        return web.json_response({"ok": False, "error": str(e)}, status=500)


async def main():
    await init_db()
    asyncio.create_task(rabbitmq_consumer())

    app = web.Application()
    app.router.add_post("/send", handle_send)
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, "0.0.0.0", 5005)
    await site.start()
    logging.info("HTTP /send server listening on 0.0.0.0:5005")

    if bot2:
        await asyncio.gather(dp.start_polling(bot), dp2.start_polling(bot2))
    else:
        await dp.start_polling(bot)


if __name__ == "__main__":
    asyncio.run(main())
