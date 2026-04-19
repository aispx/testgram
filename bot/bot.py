import asyncio
import json
import logging
import os
import re
import aiosqlite
import aio_pika
import aiohttp
from aiohttp import web
from aiogram import Bot, Dispatcher, F
from aiogram.filters import CommandStart
from aiogram.types import Message, CallbackQuery, InlineKeyboardMarkup, InlineKeyboardButton

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger(__name__)

# Load .env
with open("/root/testgram/bot/.env") as f:
    for line in f:
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            k, v = line.split("=", 1)
            os.environ[k] = v

BOT_TOKEN = os.environ.get("BOT_TOKEN", "")
DB_PATH = os.environ.get("DB_PATH", "/root/testgram/bot/codes.db")
RABBITMQ_URL = os.environ.get("RABBITMQ_URL", "amqp://test:testgram2024@localhost/")
MAX_NUMBERS = 2

# Bot placeholder - will be created in main after event loop starts
bot = None
dp = Dispatcher()
dp = Dispatcher()
waiting_for_phone = set()

async def init_db():
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("""
            CREATE TABLE IF NOT EXISTS user_numbers (
                tg_id INTEGER, phone TEXT, PRIMARY KEY (tg_id, phone)
            )
        """)
        await db.commit()

async def get_user_numbers(tg_id):
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute("SELECT phone FROM user_numbers WHERE tg_id=?", (tg_id,)) as cur:
            return [r[0] for r in await cur.fetchall()]

async def get_owner_of(phone):
    clean = re.sub(r'\D', '', phone)
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute("SELECT tg_id FROM user_numbers WHERE replace(replace(phone,'+',''),'-','')=?", (clean,)) as cur:
            r = await cur.fetchone()
            return r[0] if r else None

async def add_number(tg_id, phone):
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

async def remove_number(tg_id, phone):
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("DELETE FROM user_numbers WHERE tg_id=? AND phone=?", (tg_id, phone))
        await db.commit()

def numbers_keyboard(numbers):
    buttons = [[InlineKeyboardButton(text=f"❌ {n}", callback_data=f"del:{n}")] for n in numbers]
    buttons.append([InlineKeyboardButton(text="➕ Добавить номер", callback_data="add")])
    return InlineKeyboardMarkup(inline_keyboard=buttons)

def status_text(numbers):
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
        await call.answer(f"Лимит {MAX_NUMBERS} номера", show_alert=True)
        return
    waiting_for_phone.add(call.from_user.id)
    await call.answer("Введите номер: +79XXXXXXXXX")
    await call.message.delete()

@dp.callback_query(F.data.startswith("del:"))
async def cb_del(call: CallbackQuery):
    phone = call.data[4:]
    await remove_number(call.from_user.id, phone)
    numbers = await get_user_numbers(call.from_user.id)
    await call.message.edit_text(status_text(numbers), reply_markup=numbers_keyboard(numbers))
    await call.answer()

@dp.message()
async def handle_phone(message: Message):
    if message.from_user.id not in waiting_for_phone:
        return
    phone = message.text.strip()
    if not re.match(r"^\+?7\d{10}$", phone):
        await message.answer("Неверный формат")
        return
    result = await add_number(message.from_user.id, phone)
    if result == "ok":
        await message.answer("✅ Добавлен!")
    elif result == "limit":
        await message.answer(f"❌ Лимит {MAX_NUMBERS}")
    elif result == "taken":
        await message.answer("❌ Занят")
    elif result == "exists":
        await message.answer("✅ Уже есть")
    waiting_for_phone.discard(message.from_user.id)
    numbers = await get_user_numbers(message.from_user.id)
    await message.answer(status_text(numbers), reply_markup=numbers_keyboard(numbers))

async def send_code_to_owner(owner, digits, code):
    text = f"📱 Код для {digits}: <code>{code}</code>"
    try:
        await bot.send_message(owner, text, parse_mode="HTML")
    except Exception as e:
        logger.error(f"send_code_to_owner error: {e}")

async def rabbitmq_consumer():
    while True:
        try:
            conn = await aio_pika.connect(RABBITMQ_URL)
            channel = await conn.channel()
            # Use passive mode to get existing exchange
            exchange = await channel.declare_exchange("mytelegram_exchange", aio_pika.ExchangeType.DIRECT, durable=False)
            queue = await channel.declare_queue("bot_codes", durable=False)
            await queue.bind(exchange, routing_key="AppCodeCreatedIntegrationEvent")
            
            async with queue.iterator() as q:
                async for message in q:
                    with message.process():
                        data = json.loads(message.body.decode())
                        phone = data.get("phone", "")
                        code = data.get("code", "")
                        if phone and code:
                            digits = phone  # Show full number
                            owner = await get_owner_of(phone)
                            if owner:
                                await send_code_to_owner(owner, digits, code)
        except Exception as e:
            logger.error(f"RabbitMQ error: {e}, reconnecting in 5s...")
            await asyncio.sleep(5)

async def handle_send(request):
    try:
        data = await request.json()
        phone = data.get("phone", "")
        code = data.get("code", "")
        if phone and code:
            owner = await get_owner_of(phone)
            if owner:
                digits = phone  # Show full number
                await send_code_to_owner(owner, digits, code)
                return web.json_response({"ok": True})
        return web.json_response({"ok": False, "error": "no owner"}, status=404)
    except Exception as e:
        logger.error(f"handle_send error: {e}")
        return web.json_response({"ok": False, "error": str(e)}, status=500)

async def main():
    global bot
    
    # Proxy configuration from .env (PROXY_URL)
    # Leave empty to disable proxy
    from aiogram.client.session.aiohttp import AiohttpSession
    
    session = None
    if os.environ.get("PROXY_URL"):
        session = AiohttpSession(proxy=os.environ.get("PROXY_URL"))
        logger.info(f"Using proxy: {os.environ.get('PROXY_URL')}")
    else:
        logger.info("Proxy disabled - using direct connection")
    
    bot = Bot(token=BOT_TOKEN, session=session)
    
    await init_db()
    asyncio.create_task(rabbitmq_consumer())
    
    app = web.Application()
    app.router.add_post("/send", handle_send)
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, "0.0.0.0", 5005)
    await site.start()
    logger.info("Bot started on port 5005")
    
    await dp.start_polling(bot)

if __name__ == "__main__":
    asyncio.run(main())