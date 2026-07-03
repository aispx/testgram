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

# Load local .env file.
# The bot is usually started from docker/systemd, but reading .env here keeps
# the same config working when the bot is launched manually from /root/testgram.
env_path = os.path.join(os.path.dirname(__file__), ".env")
if os.path.exists(env_path):
    with open(env_path) as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith("#") and "=" in line:
                k, v = line.split("=", 1)
                os.environ[k.strip()] = v.strip()
else:
    logger.warning(".env file not found at %s, using environment variables only", env_path)

# Main bot settings.
DB_PATH = os.environ.get("DB_PATH", "/root/testgram/bot/codes.db")
MAX_NUMBERS = 2

def collect_bot_tokens():
    """Return the list of bot tokens to run.

    Multiple bots are supported: define BOT_TOKEN and any number of
    BOT_TOKEN1, BOT_TOKEN2, ... in .env. Running a single bot is fine too -
    just leave only one token set. Empty values and duplicates are ignored,
    and order is preserved (BOT_TOKEN first).
    """
    keys = ["BOT_TOKEN"] + [f"BOT_TOKEN{i}" for i in range(1, 10)]
    tokens = []
    seen = set()
    for key in keys:
        value = os.environ.get(key, "").strip()
        if value and value not in seen:
            seen.add(value)
            tokens.append(value)
    return tokens

BOT_TOKENS = collect_bot_tokens()

def get_rabbitmq_url():
    """Return RabbitMQ URL.

    RABBITMQ_URL=AUTO is supported for the host-run systemd bot: the function
    reads the RabbitMQ container IP with docker inspect and builds an AMQP URL.
    A normal amqp:// URL can still be set explicitly in .env.
    """
    url = os.environ.get("RABBITMQ_URL", "")
    if url and url.upper() != "AUTO":
        return url

    import subprocess

    container_names = [
        os.environ.get("RABBITMQ_CONTAINER", ""),
        "compose_rabbitmq_1",
        "compose-rabbitmq-1",
    ]
    user = os.environ.get("RABBITMQ_USER", "test")
    password = os.environ.get("RABBITMQ_PASSWORD", "testgram2024")

    for container in [name for name in container_names if name]:
        try:
            ip = subprocess.check_output(
                [
                    "docker",
                    "inspect",
                    "-f",
                    "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}",
                    container,
                ],
                text=True,
            ).strip()
            if ip:
                logger.info("RabbitMQ auto-detected at %s (%s)", ip, container)
                return f"amqp://{user}:{password}@{ip}/"
        except Exception as e:
            logger.warning("Could not inspect RabbitMQ container %s: %s", container, e)

    logger.warning("RabbitMQ auto-detect failed, falling back to localhost")
    return f"amqp://{user}:{password}@localhost/"

ENABLE_RABBITMQ_CONSUMER = os.environ.get("ENABLE_RABBITMQ_CONSUMER", "false").lower() in {"1", "true", "yes", "on"}
RABBITMQ_URL = get_rabbitmq_url() if ENABLE_RABBITMQ_CONSUMER else ""

# Bot registry - the real Bot objects are created in main() after the
# asyncio event loop exists, so proxy/session settings can be applied safely.
# Maps bot_id -> Bot so codes can be delivered through the same bot the user
# linked their number with. One Dispatcher drives all bots.
bots = {}
dp = Dispatcher()

def get_bot_for(bot_id):
    """Return the Bot for a given id, falling back to the first configured bot.

    The fallback covers legacy bindings created before multi-bot support, which
    have no stored bot_id.
    """
    if bot_id is not None and bot_id in bots:
        return bots[bot_id]
    return next(iter(bots.values()), None)

# Users that pressed "add number" and are expected to send a phone number next.
waiting_for_phone = set()

async def init_db():
    """Create/upgrade the SQLite table mapping Telegram users to phone numbers.

    The bot_id column records which bot a number was linked through, so codes
    are delivered via the correct bot when several are running.
    """
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("""
            CREATE TABLE IF NOT EXISTS user_numbers (
                tg_id INTEGER, phone TEXT, bot_id INTEGER, PRIMARY KEY (tg_id, phone)
            )
        """)
        # Migrate older databases that predate multi-bot support.
        async with db.execute("PRAGMA table_info(user_numbers)") as cur:
            columns = [row[1] for row in await cur.fetchall()]
        if "bot_id" not in columns:
            await db.execute("ALTER TABLE user_numbers ADD COLUMN bot_id INTEGER")
        await db.commit()

async def get_user_numbers(tg_id):
    """Return all TestGram phone numbers attached to a Telegram account."""
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute("SELECT phone FROM user_numbers WHERE tg_id=?", (tg_id,)) as cur:
            return [r[0] for r in await cur.fetchall()]

async def get_owner_of(phone):
    """Find the owner (tg_id, bot_id) of a phone number from an incoming code event."""
    clean = re.sub(r'\D', '', phone)
    async with aiosqlite.connect(DB_PATH) as db:
        async with db.execute("SELECT tg_id, bot_id FROM user_numbers WHERE replace(replace(phone,'+',''),'-','')=?", (clean,)) as cur:
            r = await cur.fetchone()
            return (r[0], r[1]) if r else (None, None)

async def add_number(tg_id, phone, bot_id):
    """Attach a phone number to a Telegram user, enforcing limits and uniqueness."""
    numbers = await get_user_numbers(tg_id)
    if len(numbers) >= MAX_NUMBERS:
        return "limit"
    owner, _ = await get_owner_of(phone)
    if owner and owner != tg_id:
        return "taken"
    if phone in numbers:
        return "exists"
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("INSERT OR IGNORE INTO user_numbers (tg_id, phone, bot_id) VALUES (?,?,?)", (tg_id, phone, bot_id))
        await db.commit()
    return "ok"

async def remove_number(tg_id, phone):
    """Detach a phone number from a Telegram user."""
    async with aiosqlite.connect(DB_PATH) as db:
        await db.execute("DELETE FROM user_numbers WHERE tg_id=? AND phone=?", (tg_id, phone))
        await db.commit()

def numbers_keyboard(numbers):
    """Build the inline keyboard for adding/removing linked numbers."""
    buttons = [[InlineKeyboardButton(text=f"❌ {n}", callback_data=f"del:{n}")] for n in numbers]
    buttons.append([InlineKeyboardButton(text="➕ Добавить номер", callback_data="add")])
    return InlineKeyboardMarkup(inline_keyboard=buttons)

def status_text(numbers):
    """Render the account status text shown in /start and after changes."""
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
    """Start phone-number binding flow."""
    numbers = await get_user_numbers(call.from_user.id)
    if len(numbers) >= MAX_NUMBERS:
        await call.answer(f"Лимит {MAX_NUMBERS} номера", show_alert=True)
        return
    waiting_for_phone.add(call.from_user.id)
    await call.answer("Введите номер: +79XXXXXXXXX")
    await call.message.delete()

@dp.callback_query(F.data.startswith("del:"))
async def cb_del(call: CallbackQuery):
    """Remove the selected phone number from the user's account."""
    phone = call.data[4:]
    await remove_number(call.from_user.id, phone)
    numbers = await get_user_numbers(call.from_user.id)
    await call.message.edit_text(status_text(numbers), reply_markup=numbers_keyboard(numbers))
    await call.answer()

@dp.message()
async def handle_phone(message: Message, bot: Bot):
    """Validate and save the phone number sent after pressing the add button."""
    if message.from_user.id not in waiting_for_phone:
        return
    phone = message.text.strip()
    if not re.match(r"^\+?7\d{10}$", phone):
        await message.answer("Неверный формат")
        return
    result = await add_number(message.from_user.id, phone, bot.id)
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

async def send_code_to_owner(owner, digits, code, bot_id=None):
    """Send a login code to the Telegram user that owns the phone number.

    The message is delivered through the bot the user linked their number with
    (bot_id), falling back to the first configured bot for legacy bindings.
    """
    target_bot = get_bot_for(bot_id)
    if target_bot is None:
        logger.error("No bot available to send code for %s", digits)
        return
    text = f"📱 Код для {digits}: <code>{code}</code>"
    try:
        await target_bot.send_message(owner, text, parse_mode="HTML")
        logger.info("Sent login code for %s to Telegram user %s via bot %s", digits, owner, target_bot.id)
    except Exception as e:
        logger.error(f"send_code_to_owner error: {e}")

async def rabbitmq_consumer():
    """Listen for MyTelegram code-created events and forward codes to owners."""
    while True:
        try:
            conn = await aio_pika.connect(RABBITMQ_URL)
            channel = await conn.channel()
            # Use the same exchange/routing key as the backend publishes.
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
                            owner, bot_id = await get_owner_of(phone)
                            if owner:
                                await send_code_to_owner(owner, digits, code, bot_id)
        except Exception as e:
            logger.error(f"RabbitMQ error: {e}, reconnecting in 5s...")
            await asyncio.sleep(5)

async def handle_send(request):
    """HTTP fallback endpoint: POST /send {"phone": "...", "code": "..."}."""
    try:
        data = await request.json()
        phone = data.get("phone", "")
        code = data.get("code", "")
        if phone and code:
            owner, bot_id = await get_owner_of(phone)
            if owner:
                digits = phone  # Show full number
                await send_code_to_owner(owner, digits, code, bot_id)
                return web.json_response({"ok": True})
        return web.json_response({"ok": False, "error": "no owner"}, status=404)
    except Exception as e:
        logger.error(f"handle_send error: {e}")
        return web.json_response({"ok": False, "error": str(e)}, status=500)

async def main():
    # Proxy configuration from .env (PROXY_URL)
    # Leave PROXY_URL empty to use a direct connection.
    from aiogram.client.session.aiohttp import AiohttpSession

    proxy_url = os.environ.get("PROXY_URL")
    if proxy_url:
        logger.info(f"Using proxy: {proxy_url}")
    else:
        logger.info("Proxy disabled - using direct connection")

    if not BOT_TOKENS:
        raise RuntimeError("No bot token configured. Set BOT_TOKEN (and optionally BOT_TOKEN1..) in .env")

    # Create one Bot per configured token. Each bot gets its own session so a
    # shared proxy connector is not reused across bots. A token that fails to
    # authorize is skipped with a warning instead of taking down the others.
    for token in BOT_TOKENS:
        session = AiohttpSession(proxy=proxy_url) if proxy_url else None
        b = Bot(token=token, session=session)
        try:
            me = await b.get_me()
        except Exception as e:
            logger.error("Skipping bot id=%s, could not authorize: %s", b.id, e)
            await b.session.close()
            continue
        bots[b.id] = b
        logger.info("Configured bot @%s (id=%s)", me.username, b.id)

    if not bots:
        raise RuntimeError("No bot could be authorized; check the tokens in .env")

    logger.info("Running %d bot(s)", len(bots))

    await init_db()
    if ENABLE_RABBITMQ_CONSUMER:
        asyncio.create_task(rabbitmq_consumer())
    else:
        logger.info("RabbitMQ consumer disabled; codes are received via HTTP /send")

    app = web.Application()
    app.router.add_post("/send", handle_send)
    runner = web.AppRunner(app)
    await runner.setup()
    site = web.TCPSite(runner, "0.0.0.0", 5005)
    await site.start()
    logger.info("Bot started on port 5005")

    await dp.start_polling(*bots.values())

if __name__ == "__main__":
    asyncio.run(main())
