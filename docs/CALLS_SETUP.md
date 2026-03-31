# Voice & Video Calls Setup in Testgram

## Overview

Testgram supports voice and video calls via WebRTC. For calls to work, you **must** configure your own STUN/TURN server.

## Quick Setup

### 1. Install Coturn TURN Server (Required)

Calls require your own TURN server:

```bash
# Ubuntu/Debian
sudo apt-get update
sudo apt-get install coturn

# Enable service
sudo systemctl enable coturn
```

### 2. Configure Coturn

Edit `/etc/turnserver.conf`:

```conf
# Listening port
listening-port=3478
tls-listening-port=5349

# External IP (replace with your server IP)
external-ip=YOUR_SERVER_IP

# Realm
realm=testgram.local

# User credentials
user=testgram:testgram123

# Fingerprint
fingerprint

# Long-term credentials
lt-cred-mech

# Verbose logging (for debugging)
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

### 3. Start Coturn

```bash
sudo systemctl start coturn
sudo systemctl status coturn
```

### 4. Open Firewall Ports

```bash
sudo ufw allow 3478/udp
sudo ufw allow 3478/tcp
sudo ufw allow 49152:65535/udp  # Port range for relay
```

### 5. Configure WebRTC in Testgram

Edit `.env` file:

```bash
# REQUIRED configuration for calls
App__WebRtcConnections__0__Ip=YOUR_SERVER_IP
App__WebRtcConnections__0__Ipv6=
App__WebRtcConnections__0__Port=3478
App__WebRtcConnections__0__Turn=True
App__WebRtcConnections__0__Stun=True
App__WebRtcConnections__0__UserName=testgram
App__WebRtcConnections__0__Password=testgram123

# Additional server for redundancy (optional)
App__WebRtcConnections__1__Ip=BACKUP_SERVER_IP
App__WebRtcConnections__1__Port=3478
App__WebRtcConnections__1__Turn=True
App__WebRtcConnections__1__Stun=True
App__WebRtcConnections__1__UserName=testgram
App__WebRtcConnections__1__Password=testgram123
```

### 6. Setup MongoDB Indexes

Indexes are created **automatically** on server startup via the `call-init` container.

To create indexes manually:

```bash
cd /root/testgram/scripts
./setup_call_indexes.sh
```

Or via mongosh:

```bash
docker compose exec mongodb mongosh tg < scripts/setup_call_indexes.js
```

### 7. Start Servers

```bash
cd /root/testgram/docker/compose
docker compose up -d
```

On first startup, automatically:
- Indexes for `call_sessions` collection are created
- TTL is configured for automatic cleanup of old records
- MongoDB readiness is verified

Check init container logs:
```bash
docker compose logs call-init
```

## Testing Calls

### 1. Configuration Check

Use Telegram client to test:

1. Login with two different accounts
2. Initiate a call between them
3. Check server logs:

```bash
docker compose logs -f messenger-command-server | grep -i call
```

### 2. Test STUN/TURN Server

Use online tool: https://webrtc.github.io/samples/src/content/peerconnection/trickle-ice/

Or via command line:

```bash
# Install stuntman
sudo apt-get install stuntman-client

# Test STUN
stunclient YOUR_SERVER_IP 3478

# Test TURN
turnutils_uclient -v -u testgram -w testgram123 YOUR_SERVER_IP
```

## Call Architecture

### Data Flow

1. **RequestCall** - Initiator creates call
   - Record created in MongoDB `call_sessions`
   - `UpdatePhoneCall` sent to receiver
   
2. **AcceptCall** - Receiver accepts call
   - State updated to "accepted"
   - Update sent to initiator

3. **ConfirmCall** - Initiator confirms call
   - Encryption keys exchanged (Diffie-Hellman)
   - WebRTC connections returned (STUN/TURN servers)
   - State changed to "confirmed"

4. **SendSignalingData** - Exchange WebRTC signals
   - ICE candidates
   - SDP offers/answers
   - Transmitted via `UpdatePhoneCallSignalingData`

5. **DiscardCall** - End call
   - Duration and reason saved
   - State changed to "discarded"

### Call States

- `requested` - Call initiated
- `accepted` - Call accepted
- `confirmed` - Keys exchanged, WebRTC connection establishing
- `discarded` - Call ended

## Improvements in This Update

### 1. Multiple WebRTC Server Support
- Configure multiple STUN/TURN servers
- Automatic fallback to Google public STUN servers

### 2. Improved Configuration
- Support for UDP and TCP transports for TURN
- Correct protocol parameters (minLayer, maxLayer)
- IPv6 support

### 3. Database Optimization
- Indexes for fast call lookup
- Automatic deletion of old records (TTL 30 days)
- Unique index on CallId + AccessHash

### 4. Improved Error Handling
- Call state validation
- Participant validation
- Proper RPC errors

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

3. Check WebRTC configuration:
```bash
docker compose exec messenger-command-server env | grep WebRtc
```

### TURN Server Not Working

1. Check Coturn status:
```bash
sudo systemctl status coturn
sudo tail -f /var/log/turnserver.log
```

2. Check ports:
```bash
sudo netstat -tulpn | grep 3478
```

3. Check firewall:
```bash
sudo ufw status
```

### Poor Audio/Video Quality

1. Increase relay port range in Coturn
2. Check network bandwidth
3. Use multiple TURN servers in different locations

## Additional Resources

- [WebRTC Documentation](https://webrtc.org/)
- [Coturn Documentation](https://github.com/coturn/coturn)
- [Telegram MTProto Calls](https://core.telegram.org/api/calls)
