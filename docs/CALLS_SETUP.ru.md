# Настройка голосовых и видео звонков в Testgram

## Обзор

Testgram поддерживает голосовые и видео звонки через WebRTC. Для работы звонков **обязательно** необходимо настроить собственный STUN/TURN сервер.

## Быстрая настройка

### 1. Установка Coturn TURN сервера (обязательно)

Для работы звонков необходим собственный TURN сервер:

```bash
# Ubuntu/Debian
sudo apt-get update
sudo apt-get install coturn

# Включить сервис
sudo systemctl enable coturn
```

### 2. Конфигурация Coturn

Отредактируйте `/etc/turnserver.conf`:

```conf
# Listening port
listening-port=3478
tls-listening-port=5349

# External IP (замените на IP вашего сервера)
external-ip=YOUR_SERVER_IP

# Realm
realm=testgram.local

# User credentials
user=testgram:testgram123

# Fingerprint
fingerprint

# Long-term credentials
lt-cred-mech

# Verbose logging (для отладки)
verbose

# Log file
log-file=/var/log/turnserver.log

# Relay IP
relay-ip=YOUR_SERVER_IP

# No TCP relay
no-tcp-relay

# No TLS
no-tls
no-dtls
```

### 3. Запуск Coturn

```bash
sudo systemctl start coturn
sudo systemctl status coturn
```

### 4. Открытие портов в файрволе

```bash
sudo ufw allow 3478/udp
sudo ufw allow 3478/tcp
sudo ufw allow 49152:65535/udp  # Диапазон портов для relay
```

### 5. Настройка WebRTC в Testgram

Отредактируйте файл `.env`:

```bash
# ОБЯЗАТЕЛЬНАЯ конфигурация для звонков
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Ipv6=
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123

# Дополнительный сервер для резервирования (опционально)
App__WebRtcConnections__1__Ip=BACKUP_SERVER_IP
App__WebRtcConnections__1__Port=3478
App__WebRtcConnections__1__Turn=True
App__WebRtcConnections__1__Stun=True
App__WebRtcConnections__1__UserName=testgram
App__WebRtcConnections__1__Password=testgram123
```

### 6. Установка индексов MongoDB

Индексы создаются **автоматически** при запуске серверов через init-контейнер `call-init`.

Если нужно создать индексы вручную:

```bash
cd /root/testgram/scripts
./setup_call_indexes.sh
```

Или через mongosh:

```bash
docker compose exec mongodb mongosh tg < scripts/setup_call_indexes.js
```

### 7. Запуск серверов

```bash
cd /root/testgram/docker/compose
docker compose up -d
```

При первом запуске автоматически:
- Создадутся индексы для коллекции `call_sessions`
- Настроится TTL для автоматической очистки старых записей
- Проверится готовность MongoDB

Проверьте логи init-контейнера:
```bash
docker compose logs call-init
```

## Тестирование звонков

### 1. Проверка конфигурации

Используйте Telegram клиент для проверки:

1. Войдите с двух разных аккаунтов
2. Инициируйте звонок между ними
3. Проверьте логи сервера:

```bash
docker compose logs -f messenger-command-server | grep -i call
```

### 2. Проверка STUN/TURN сервера

Используйте онлайн инструмент: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

Или через командную строку:

```bash
# Установка stuntman
sudo apt-get install stuntman-client

# Проверка STUN
stunclient YOUR_SERVER_IP 3478

# Проверка TURN
turnutils_uclient -v -u testgram -w testgram123 YOUR_SERVER_IP
```

## Архитектура звонков

### Поток данных

1. **RequestCall** - Инициатор создает звонок
   - Создается запись в MongoDB `call_sessions`
   - Отправляется `UpdatePhoneCall` получателю
   
2. **AcceptCall** - Получатель принимает звонок
   - Обновляется состояние на "accepted"
   - Отправляется обновление инициатору

3. **ConfirmCall** - Инициатор подтверждает звонок
   - Обмен ключами шифрования (Diffie-Hellman)
   - Возвращаются WebRTC connections (STUN/TURN серверы)
   - Состояние меняется на "confirmed"

4. **SendSignalingData** - Обмен WebRTC сигналами
   - ICE candidates
   - SDP offers/answers
   - Передается через `UpdatePhoneCallSignalingData`

5. **DiscardCall** - Завершение звонка
   - Сохраняется длительность и причина завершения
   - Состояние меняется на "discarded"

### Состояния звонка

- `requested` - Звонок инициирован
- `accepted` - Звонок принят
- `confirmed` - Ключи обменены, WebRTC соединение устанавливается
- `discarded` - Звонок завершен

## Улучшения в этом обновлении

### 1. Поддержка нескольких WebRTC серверов
- Можно настроить несколько STUN/TURN серверов
- Автоматический fallback на публичные STUN серверы Google

### 2. Улучшенная конфигурация
- Поддержка UDP и TCP транспортов для TURN
- Правильные параметры протокола (minLayer, maxLayer)
- Поддержка IPv6

### 3. Оптимизация базы данных
- Индексы для быстрого поиска звонков
- Автоматическое удаление старых записей (TTL 30 дней)
- Уникальный индекс по CallId + AccessHash

### 4. Улучшенная обработка ошибок
- Проверка состояний звонка
- Валидация участников
- Правильные RPC ошибки

## Troubleshooting

### Звонки не соединяются

1. Проверьте логи:
```bash
docker compose logs messenger-command-server | grep -i "call\|webrtc"
```

2. Проверьте MongoDB:
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5)
```

3. Проверьте конфигурацию WebRTC:
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

### TURN сервер не работает

1. Проверьте статус Coturn:
```bash
sudo systemctl status coturn
sudo tail -f /var/log/turnserver.log
```

2. Проверьте порты:
```bash
sudo netstat -tulpn | grep 3478
```

3. Проверьте файрвол:
```bash
sudo ufw status
```

### Плохое качество звука/видео

1. Увеличьте диапазон портов для relay в Coturn
2. Проверьте пропускную способность сети
3. Используйте несколько TURN серверов в разных локациях

## Дополнительные ресурсы

- [WebRTC документация](https://webrtc.org/)
- [Coturn документация](https://github.com/coturn/coturn)
- [Telegram MTProto звонки](https://core.telegram.org/api/calls)
