# Настройка голосовых, видео и групповых звонков в Testgram

## Обзор

Testgram поддерживает полный набор методов `phone.*` через WebRTC:

- **Голосовые и видеозвонки 1:1** (WebRTC, обмен ключами по DH через MTProto)
- **Групповые звонки / голосовые и видеочаты** (много участников, на основе SSRC)
- **Конференц-звонки** (сквозное шифрование, E2E)
- **Трансляции** (приём RTMP + воспроизведение HLS для групповых звонков и историй)

Для работы звонков нужен STUN/TURN сервер. Стек `docker compose` уже **включает** его:

| Сервис | Образ | Назначение |
|--------|-------|------------|
| `coturn` | `coturn/coturn:latest` | STUN/TURN сервер для WebRTC (1:1 и групповые звонки) |
| `rtmp-server` | `bluenviron/mediamtx:latest` | Приём RTMP + воспроизведение HLS для трансляций |
| `data-seeder` | `mytelegram-data-seeder` | Создаёт индексы MongoDB для звонков при первом запуске |

Устанавливать Coturn на хост **больше не нужно**. Достаточно настроить `.env` и
запустить стек. Внешний/ручной Coturn по-прежнему поддерживается как альтернатива
(см. ниже).

## Быстрая настройка (встроенный Coturn — рекомендуется)

### 1. Настройка WebRTC в `.env`

Встроенный сервис `coturn` использует long-term учётные данные `testgram:testgram2024`
(см. флаг `--user` сервиса `coturn` в `docker-compose.yml`). Учётные данные, которые
сервер отдаёт клиентам в `.env`, **должны совпадать** с пользователем coturn, иначе
аутентификация TURN relay не пройдёт.

```bash
# ОБЯЗАТЕЛЬНАЯ конфигурация для звонков
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Ipv6=
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram2024

# Дополнительный сервер для резервирования (опционально)
App__WebRtcConnections__1__Ip=BACKUP_SERVER_IP
App__WebRtcConnections__1__Port=3478
App__WebRtcConnections__1__Turn=True
App__WebRtcConnections__1__Stun=True
App__WebRtcConnections__1__UserName=testgram
App__WebRtcConnections__1__Password=testgram2024
```

> Сервис `coturn` читает `App__WebRtcConnections__0__Ip` как свой `--external-ip`,
> поэтому одного этого значения достаточно, чтобы TURN поднялся с правильным
> публичным адресом.

### 2. Настройка RTMP / HLS (групповые звонки и трансляции)

```bash
App__RtmpStreamUrl=rtmp://YOUR_SERVER_IP:1935/live
App__RtmpHlsUrl=http://rtmp-server:8888/live
RTMP_PORT=1935
RTMP_HLS_PORT=8888
```

- `App__RtmpStreamUrl` — публичный URL приёма RTMP, который выдаётся программам для
  стриминга (OBS и т.п.). Используйте публичный IP или домен сервера.
- `App__RtmpHlsUrl` — внутренний HLS-URL, откуда мессенджер читает сегменты; оставьте
  его указывающим на имя сервиса `rtmp-server`.

### 3. Открытие портов в файрволе

```bash
sudo ufw allow 3478/tcp
sudo ufw allow 3478/udp
sudo ufw allow 3479/tcp
sudo ufw allow 3479/udp
sudo ufw allow 49152:49172/udp   # Диапазон relay-портов TURN (совпадает с min/max-port coturn)
sudo ufw allow 1935/tcp          # Приём RTMP
```

Встроенный сервис `coturn` публикует `3478`, `3479` и диапазон relay
`49152-49172/udp`; `rtmp-server` публикует `1935` (RTMP) и `8888` (HLS).

### 4. Запуск стека

```bash
cd docker/compose
docker compose up -d
```

При первом запуске автоматически:
- Контейнер `data-seeder` создаёт индексы для коллекций `call_sessions` / `group_calls`
  и настраивает TTL-очистку старых записей.
- `coturn` и `rtmp-server` запускаются вместе с сервисами мессенджера.

Проверьте вспомогательные сервисы:

```bash
docker compose logs data-seeder    # создание индексов
docker compose logs coturn         # TURN сервер
docker compose logs rtmp-server    # RTMP/HLS сервер
```

Создать индексы вручную:

```bash
cd scripts
./setup_call_indexes.sh
# или:
docker compose exec mongodb mongosh tg < scripts/setup_call_indexes.js
```

## Альтернатива: внешний / хостовый Coturn

Если вы предпочитаете свой Coturn (например, на отдельном relay-хосте), отключите или
удалите встроенный сервис `coturn` и установите Coturn самостоятельно:

```bash
# Ubuntu/Debian
sudo apt-get update
sudo apt-get install coturn
sudo systemctl enable coturn
```

Отредактируйте `/etc/turnserver.conf`:

```conf
listening-port=3478
tls-listening-port=5349
external-ip=YOUR_SERVER_IP
realm=testgram
user=testgram:testgram2024
fingerprint
lt-cred-mech
min-port=49152
max-port=49172
log-file=/var/log/turnserver.log
no-tls
no-dtls
```

```bash
sudo systemctl start coturn
sudo systemctl status coturn
```

Затем укажите `App__WebRtcConnections__0__Ip` / `UserName` / `Password` в `.env` на ваш
внешний сервер, следя за тем, чтобы учётные данные совпадали со строкой `user` выше.

## Тестирование звонков

### 1. Проверка конфигурации

1. Войдите с двух разных аккаунтов.
2. Запустите звонок 1:1, затем групповой звонок (голосовой чат) между ними.
3. Проверьте логи сервера:

```bash
docker compose logs -f messenger-command-server | grep -i call
```

### 2. Проверка STUN/TURN сервера

Онлайн-инструмент: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

Или через командную строку:

```bash
sudo apt-get install stuntman-client

# Проверка STUN
stunclient YOUR_SERVER_IP 3478

# Проверка TURN
turnutils_uclient -v -u testgram -w testgram2024 YOUR_SERVER_IP
```

### 3. Проверка RTMP-трансляции

Направьте программу для стриминга (например, OBS) на `rtmp://YOUR_SERVER_IP:1935/live`
с ключом трансляции, который возвращает `phone.getGroupCallStreamRtmpUrl`, затем
подключитесь к групповому звонку как зритель, чтобы проверить воспроизведение HLS.

## Архитектура звонков

### Поток данных звонка 1:1

1. **RequestCall** — инициатор создаёт звонок.
   - Создаётся запись в MongoDB `call_sessions` (состояние `requested`).
   - Получателю отправляется `updatePhoneCall{ phoneCallRequested }` (несёт `g_a_hash`
     и протокол).

2. **AcceptCall** — получатель принимает звонок.
   - Состояние меняется на `accepted`; инициатору отправляется
     `updatePhoneCall{ phoneCallAccepted }`.
   - Остальные сессии принимающего устройства получают `phoneCallDiscarded`
     (единственное принятие).

3. **ConfirmCall** — инициатор подтверждает звонок.
   - Завершается обмен ключами Diffie-Hellman (проверка диапазона и хеша `g_a`/`g_b`).
   - Возвращаются WebRTC `connections` (STUN/TURN рефлекторы + опционально P2P).
   - Состояние меняется на `confirmed`.

4. **SendSignalingData** — обмен WebRTC-сигналами (ICE candidates, SDP) через
   `updatePhoneCallSignalingData`.

5. **DiscardCall** — завершение звонка.
   - Сохраняются длительность и причина; состояние меняется на `discarded`
     (повторный discard идемпотентен).

### Состояния звонка 1:1

- `requested` — звонок инициирован
- `received` — клиент получателя получил запрос
- `accepted` — звонок принят
- `confirmed` — ключи обменены, WebRTC-соединение устанавливается
- `discarded` — звонок завершён (терминальное)

### Групповые звонки и трансляции

- **CreateGroupCall / JoinGroupCall / LeaveGroupCall / DiscardGroupCall** управляют
  жизненным циклом группового звонка; участники отслеживаются по уникальным SSRC
  (`Source`) и монотонно растущему `Version`.
- **EditGroupCallParticipant / ToggleGroupCallSettings / ToggleGroupCallRecord /
  EditGroupCallTitle** управляют состоянием участников и звонка в целом.
- **Запланированные звонки** поддерживают `schedule_date` и `StartScheduledGroupCall`.
- **Конференц-звонки (E2E)** добавляют цепочки блоков (chain blocks), приглашения и
  зашифрованные широковещательные сообщения.
- **Трансляции**: `GetGroupCallStreamRtmpUrl` возвращает RTMP `url`/`key` (с ротацией
  ключа при `revoke`); `GetGroupCallStreamChannels` перечисляет каналы; HLS-сегменты
  отдаются через `upload.getFile` с локацией `inputGroupCallStream`.

## Устранение неполадок

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

3. Проверьте конфигурацию WebRTC, переданную мессенджеру:
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

### TURN сервер не работает

1. Проверьте встроенный контейнер coturn:
```bash
docker compose logs -f coturn
docker compose ps coturn
```

2. Убедитесь, что отдаваемые клиентам учётные данные совпадают с `--user` coturn
   (`testgram:testgram2024`). Несовпадение — самая частая причина одностороннего звука
   и неработающего relay.

3. Проверьте порты на хосте:
```bash
sudo ss -tulpn | grep -E '3478|3479'
sudo ufw status
```

### Трансляция не воспроизводится

1. Проверьте RTMP-сервер:
```bash
docker compose logs -f rtmp-server
```

2. Убедитесь, что `App__RtmpStreamUrl` использует доступный публичный адрес, а
   `App__RtmpHlsUrl` указывает на имя сервиса `rtmp-server`.

3. Убедитесь, что порт `1935` открыт, а ключ трансляции совпадает с возвращённым
   методом `phone.getGroupCallStreamRtmpUrl`.

### Плохое качество звука/видео

1. Расширьте диапазон relay-портов во флагах coturn `--min-port`/`--max-port`
   (и откройте соответствующий UDP-диапазон в файрволе).
2. Проверьте пропускную способность сети.
3. Добавьте несколько TURN-серверов в разных локациях через
   `App__WebRtcConnections__N__*`.

## Дополнительные ресурсы

- [Документация WebRTC](https://webrtc.org/)
- [Документация Coturn](https://github.com/coturn/coturn)
- [MediaMTX (RTMP/HLS сервер)](https://github.com/bluenviron/mediamtx)
- [Звонки Telegram MTProto](https://core.telegram.org/api/calls)
