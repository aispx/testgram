#!/usr/bin/env bash
# setup-rhel.sh — Install Docker + prerequisites on RHEL 9.8 / 10.2
#
# Usage:
#   sudo ./setup-rhel.sh
#
# Run this once on each target host before the first deployment.
# Requires root privileges.
set -euo pipefail

echo "=== RHEL prerequisites setup ==="

if [ "$(id -u)" -ne 0 ]; then
  echo "ERROR: This script must be run as root (use sudo)"
  exit 1
fi

RHEL_VERSION=$(rpm -E %rhel)
echo "Detected RHEL version: ${RHEL_VERSION}"

# ── Install Docker CE ────────────────────────────────────────────────────
echo "=== Installing Docker CE ==="
if command -v docker &>/dev/null; then
  echo "Docker already installed: $(docker version --format '{{.Server.Version}}')"
else
  dnf remove -y docker docker-client docker-client-latest docker-common \
    docker-latest docker-latest-logrotate docker-logrotate \
    docker-engine podman buildah 2>/dev/null || true

  dnf config-manager --add-repo https://download.docker.com/linux/rhel/docker-ce.repo

  if [ "${RHEL_VERSION}" -ge 9 ]; then
    dnf install -y docker-ce docker-ce-cli containerd.io \
      docker-buildx-plugin docker-compose-plugin
  else
    dnf install -y docker-ce docker-ce-cli containerd.io \
      docker-buildx-plugin docker-compose-plugin
  fi

  systemctl enable --now docker
  echo "Docker installed: $(docker version --format '{{.Server.Version}}')"
fi

# ── Install docker compose v2 ───────────────────────────────────────────
echo "=== Verifying docker compose v2 ==="
if docker compose version &>/dev/null; then
  echo "docker compose: $(docker compose version --short)"
else
  echo "ERROR: docker compose v2 not found after docker-ce installation"
  exit 1
fi

# ── Configure firewall ──────────────────────────────────────────────────
echo "=== Configuring firewall ==="
if command -v firewall-cmd &>/dev/null; then
  firewall-cmd --permanent --add-port=20443/tcp  # MTProto DC1
  firewall-cmd --permanent --add-port=20543/tcp  # MTProto DC1
  firewall-cmd --permanent --add-port=20643/tcp  # MTProto DC3
  firewall-cmd --permanent --add-port=20644/tcp  # MTProto DC2/DC4
  firewall-cmd --permanent --add-port=30443/tcp  # WebSocket HTTPS
  firewall-cmd --permanent --add-port=30444/tcp  # WebSocket HTTP
  firewall-cmd --permanent --add-port=3478/tcp   # STUN/TURN TCP
  firewall-cmd --permanent --add-port=3478/udp   # STUN/TURN UDP
  firewall-cmd --permanent --add-port=3479/tcp   # STUN/TURN TCP
  firewall-cmd --permanent --add-port=3479/udp   # STUN/TURN UDP
  firewall-cmd --permanent --add-port=49152-49172/udp  # TURN relay
  firewall-cmd --permanent --add-port=1935/tcp   # RTMP
  firewall-cmd --permanent --add-port=8888/tcp   # HLS
  firewall-cmd --reload
  echo "Firewall rules applied"
else
  echo "firewalld not found, skipping (verify iptables manually)"
fi

# ── Create deploy directory ─────────────────────────────────────────────
echo "=== Creating deploy directory ==="
DEPLOY_DIR="/opt/testgram"
mkdir -p "${DEPLOY_DIR}"

if [ ! -f "${DEPLOY_DIR}/docker-compose.yml" ]; then
  echo "NOTE: ${DEPLOY_DIR}/docker-compose.yml not found."
  echo "  Copy the docker-compose.yml from the repository manually:"
  echo "  scp docker/compose/docker-compose.yml root@$(hostname):${DEPLOY_DIR}/"
  echo "  scp docker/compose/.env root@$(hostname):${DEPLOY_DIR}/.env"
fi

# ── Install Python 3 for seed scripts ───────────────────────────────────
echo "=== Installing Python 3 ==="
dnf install -y python3 python3-pip 2>/dev/null || true

# ── Summary ──────────────────────────────────────────────────────────────
echo ""
echo "=== Setup complete ==="
echo "  Docker:  $(docker version --format '{{.Server.Version}}')"
echo "  Compose: $(docker compose version --short)"
echo "  Deploy:  ${DEPLOY_DIR}"
echo ""
echo "Next steps:"
echo "  1. Copy docker-compose.yml and .env to ${DEPLOY_DIR}"
echo "  2. Configure .env with your secrets"
echo "  3. Add the deploy SSH key to ~/.ssh/authorized_keys"
echo "  4. Set GitHub Actions secrets (see .github/SECRETS.md)"
