# Voice, Video & Group Calls Setup in Testgram

## Overview

Testgram supports the full `phone.*` calling surface via WebRTC:

- **1:1 voice & video calls** (WebRTC, MTProto DH key exchange)
- **Group calls / voice & video chats** (multi-participant, SSRC-based)
- **Conference calls** (end-to-end encrypted group calls)
- **Live streams** (RTMP ingest + HLS playback for group calls and live stories)

For calls to work you need a STUN/TURN server. The `docker compose` stack **bundles**
this for you:

| Service | Image | Purpose |
|---------|-------|---------|
| `coturn` | `coturn/coturn:latest` | STUN/TURN server for WebRTC (1:1 and group calls) |
| `rtmp-server` | `bluenviron/mediamtx:latest` | RTMP ingest + HLS playback for live streams |
| `call-init` | `mongo:8` | Creates MongoDB call indexes on first start |

You **no longer need to install Coturn on the host**. Just configure `.env` and start
the stack. Manual/external Coturn is still supported as an alternative (see below).

## Quick Setup (bundled Coturn — recommended)

### 1. Configure WebRTC in `.env`

The bundled `coturn` service uses long-term credentials `testgram:testgram2024`
(see the `coturn` service `--user` flag in `docker-compose.yml`). The credentials
advertised to clients in `.env` **must match** the coturn user, otherwise TURN relay
authentication fails.

```bash
# REQUIRED configuration for calls
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Ipv6=
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram2024

# Additional server for redundancy (optional)
App__WebRtcConnections__1__Ip=BACKUP_SERVER_IP
App__WebRtcConnections__1__Port=3478
App__WebRtcConnections__1__Turn=True
App__WebRtcConnections__1__Stun=True
App__WebRtcConnections__1__UserName=testgram
App__WebRtcConnections__1__Password=testgram2024
```

> The `coturn` service reads `App__WebRtcConnections__0__Ip` as its `--external-ip`,
> so setting the value above is enough to bring TURN up with the right public address.

### 2. Configure RTMP / HLS (group calls & live streams)

```bash
App__RtmpStreamUrl=rtmp://YOUR_SERVER_IP:1935/live
App__RtmpHlsUrl=http://rtmp-server:8888/live
RTMP_PORT=1935
RTMP_HLS_PORT=8888
```

- `App__RtmpStreamUrl` is the public RTMP ingest URL handed to streaming software
  (OBS, etc.). Use your server's public IP or domain.
- `App__RtmpHlsUrl` is the internal HLS URL the messenger reads segments from; keep it
  pointed at the `rtmp-server` service name.

### 3. Open Firewall Ports

```bash
sudo ufw allow 3478/tcp
sudo ufw allow 3478/udp
sudo ufw allow 3479/tcp
sudo ufw allow 3479/udp
sudo ufw allow 49152:49172/udp   # TURN relay port range (matches coturn min/max-port)
sudo ufw allow 1935/tcp          # RTMP ingest
```

The bundled `coturn` service publishes `3478`, `3479` and the relay range
`49152-49172/udp`; `rtmp-server` publishes `1935` (RTMP) and `8888` (HLS).

### 4. Start the Stack

```bash
cd docker/compose
docker compose up -d
```

On first startup, automatically:
- The `call-init` container creates indexes for the `call_sessions` / `group_calls`
  collections and configures TTL cleanup for old records.
- `coturn` and `rtmp-server` start alongside the messenger services.

Verify the helper services:

```bash
docker compose logs call-init      # index creation
docker compose logs coturn         # TURN server
docker compose logs rtmp-server    # RTMP/HLS server
```

To create call indexes manually:

```bash
cd scripts
./setup_call_indexes.sh
# or:
docker compose exec mongodb mongosh tg < scripts/setup_call_indexes.js
```

## Alternative: External / Host Coturn

If you prefer to run your own Coturn (e.g. on a dedicated relay host), disable or
remove the bundled `coturn` service and install Coturn yourself:

```bash
# Ubuntu/Debian
sudo apt-get update
sudo apt-get install coturn
sudo systemctl enable coturn
```

Edit `/etc/turnserver.conf`:

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

Then point `App__WebRtcConnections__0__Ip` / `UserName` / `Password` in `.env` at your
external server, making sure the credentials match the `user` line above.

## Testing Calls

### 1. Configuration Check

1. Log in with two different accounts.
2. Start a 1:1 call, then a group call (voice chat), between them.
3. Check server logs:

```bash
docker compose logs -f messenger-command-server | grep -i call
```

### 2. Test STUN/TURN Server

Use the online tool: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

Or via command line:

```bash
sudo apt-get install stuntman-client

# Test STUN
stunclient YOUR_SERVER_IP 3478

# Test TURN
turnutils_uclient -v -u testgram -w testgram2024 YOUR_SERVER_IP
```

### 3. Test RTMP Live Stream

Point streaming software (e.g. OBS) at `rtmp://YOUR_SERVER_IP:1935/live` with the
stream key returned by `phone.getGroupCallStreamRtmpUrl`, then join the group call as a
viewer to confirm HLS playback.

## Call Architecture

### 1:1 Call Data Flow

1. **RequestCall** — initiator creates the call.
   - Record created in MongoDB `call_sessions` (state `requested`).
   - `updatePhoneCall{ phoneCallRequested }` (carrying `g_a_hash` + protocol) sent to receiver.

2. **AcceptCall** — receiver accepts.
   - State updated to `accepted`; `updatePhoneCall{ phoneCallAccepted }` sent to caller.
   - The accepting device's other sessions get `phoneCallDiscarded` (single-acceptance).

3. **ConfirmCall** — initiator confirms.
   - Diffie-Hellman key exchange completed (`g_a`/`g_b` range + hash validated).
   - WebRTC `connections` (STUN/TURN reflectors + optional P2P) returned.
   - State changed to `confirmed`.

4. **SendSignalingData** — exchange WebRTC signals (ICE candidates, SDP) via
   `updatePhoneCallSignalingData`.

5. **DiscardCall** — end the call.
   - Duration and reason saved; state changed to `discarded` (idempotent re-discard).

### 1:1 Call States

- `requested` — call initiated
- `received` — callee's client received the request
- `accepted` — call accepted
- `confirmed` — keys exchanged, WebRTC connection establishing
- `discarded` — call ended (terminal)

### Group Calls & Live Streams

- **CreateGroupCall / JoinGroupCall / LeaveGroupCall / DiscardGroupCall** manage the
  group-call lifecycle; participants are tracked with unique SSRC (`Source`) values and
  a monotonically increasing `Version`.
- **EditGroupCallParticipant / ToggleGroupCallSettings / ToggleGroupCallRecord /
  EditGroupCallTitle** manage per-participant and call-wide state.
- **Scheduled calls** support `schedule_date` and `StartScheduledGroupCall`.
- **Conference (E2E) calls** add chain blocks, invites, and encrypted broadcasts.
- **Live streams**: `GetGroupCallStreamRtmpUrl` returns the RTMP `url`/`key` (with key
  rotation on `revoke`); `GetGroupCallStreamChannels` lists channels; HLS segments are
  served through `upload.getFile` with an `inputGroupCallStream` location.

## Troubleshooting

### Calls Not Connecting

1. Check logs:
```bash
docker compose logs messenger-command-server | grep -i "call\|webrtc"
```

2. Check MongoDB:
```bash
docker compose exec mongodb mongosh tg
db.call_sessions.find().sort({Date: -1}).limit(5)
```

3. Check the WebRTC configuration passed to the messenger:
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

### TURN Server Not Working

1. Check the bundled coturn container:
```bash
docker compose logs -f coturn
docker compose ps coturn
```

2. Confirm the advertised credentials match coturn's `--user` (`testgram:testgram2024`).
   A mismatch is the most common cause of one-way audio / failed relay.

3. Check ports on the host:
```bash
sudo ss -tulpn | grep -E '3478|3479'
sudo ufw status
```

### Live Stream Not Playing

1. Check the RTMP server:
```bash
docker compose logs -f rtmp-server
```

2. Confirm `App__RtmpStreamUrl` uses a reachable public address and `App__RtmpHlsUrl`
   points at the `rtmp-server` service name.

3. Confirm port `1935` is open and the stream key matches the one returned by
   `phone.getGroupCallStreamRtmpUrl`.

### Poor Audio/Video Quality

1. Widen the relay port range in the coturn `--min-port`/`--max-port` flags (and open
   the matching UDP range on the firewall).
2. Check network bandwidth.
3. Add multiple TURN servers in different locations via `App__WebRtcConnections__N__*`.

## Additional Resources

- [WebRTC Documentation](https://webrtc.org/)
- [Coturn Documentation](https://github.com/coturn/coturn)
- [MediaMTX (RTMP/HLS server)](https://github.com/bluenviron/mediamtx)
- [Telegram MTProto Calls](https://core.telegram.org/api/calls)
