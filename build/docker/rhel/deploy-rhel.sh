#!/usr/bin/env bash
# deploy-rhel.sh — Deploy testgram stack on RHEL 9.8 or RHEL 10.2
#
# Usage:
#   ./deploy-rhel.sh <rhel_version> <image_registry> <namespace> <version>
#
# Example:
#   ./deploy-rhel.sh rhel9 ghcr.io/loyldg/testgram 0.38.224.0903
#
# This script is called by GitHub Actions via SSH, but can also be run manually.
# It expects docker compose v2 to be available and the testgram stack directory
# at /opt/testgram (or wherever DEPLOY_DIR points).
set -euo pipefail

RHEL_VERSION="${1:?Usage: deploy-rhel.sh <rhel9|rhel10> <registry> <namespace> <version>}"
IMAGE_REGISTRY="${2:?Missing image registry}"
NAMESPACE="${3:?Missing namespace}"
VERSION="${4:?Missing version}"

# ── Configuration ────────────────────────────────────────────────────────
DEPLOY_DIR="${DEPLOY_DIR:-/opt/testgram}"
COMPOSE_PROJECT="${COMPOSE_PROJECT:-mytelegram}"
SERVICE_ORDER=(
  messenger-command-server
  messenger-query-server
  auth-server
  gateway-server
  sms-sender
  data-seeder
)
INFRA_SERVICES=(redis rabbitmq mongodb minio)

# ── Pre-flight checks ───────────────────────────────────────────────────
echo "=== Pre-flight checks ==="

if ! command -v docker &>/dev/null; then
  echo "ERROR: docker is not installed"
  exit 1
fi

if ! docker compose version &>/dev/null; then
  echo "ERROR: docker compose v2 is required (not docker-compose v1)"
  exit 1
fi

DOCKER_VERSION=$(docker version --format '{{.Server.Version}}' 2>/dev/null || echo "unknown")
COMPOSE_VERSION=$(docker compose version --short 2>/dev/null || echo "unknown")
RHEL_RELEASE=$(cat /etc/redhat-release 2>/dev/null || echo "unknown")
echo "  Host: ${RHEL_RELEASE}"
echo "  Docker: ${DOCKER_VERSION}"
echo "  Compose: ${COMPOSE_VERSION}"

# ── Deploy directory ─────────────────────────────────────────────────────
if [ ! -d "${DEPLOY_DIR}" ]; then
  echo "ERROR: Deploy directory ${DEPLOY_DIR} does not exist"
  echo "  Create it with: mkdir -p ${DEPLOY_DIR}"
  exit 1
fi

if [ ! -f "${DEPLOY_DIR}/docker-compose.yml" ]; then
  echo "ERROR: docker-compose.yml not found in ${DEPLOY_DIR}"
  exit 1
fi

if [ ! -f "${DEPLOY_DIR}/.env" ]; then
  echo "ERROR: .env not found in ${DEPLOY_DIR}"
  echo "  Copy from .env.example and configure it first"
  exit 1
fi

cd "${DEPLOY_DIR}"

# ── Update .env with new version ─────────────────────────────────────────
echo "=== Updating .env version ==="
if grep -q "^MyTelegramVersion=" .env; then
  sed -i "s/^MyTelegramVersion=.*/MyTelegramVersion=latest/" .env
else
  echo "MyTelegramVersion=latest" >> .env
fi

# ── Pull and retag images ────────────────────────────────────────────────
echo "=== Pulling images ==="
SUFFIX="${RHEL_VERSION}"
for svc in "${SERVICE_ORDER[@]}"; do
  IMAGE="${IMAGE_REGISTRY}/${NAMESPACE}/mytelegram-${svc}-${SUFFIX}:${VERSION}"
  echo "  Pulling ${IMAGE}..."
  if ! docker pull "${IMAGE}"; then
    echo "ERROR: Failed to pull ${IMAGE}"
    exit 1
  fi
  docker tag "${IMAGE}" "mytelegram-${svc}:latest"
done

# ── Rolling restart ──────────────────────────────────────────────────────
echo "=== Rolling restart ==="

echo "  Starting infrastructure..."
docker compose -p "${COMPOSE_PROJECT}" up -d --no-deps "${INFRA_SERVICES[@]}"
echo "  Waiting for infrastructure to stabilize..."
sleep 15

for svc in "${SERVICE_ORDER[@]}"; do
  echo "  Restarting ${svc}..."
  docker compose -p "${COMPOSE_PROJECT}" up -d --no-deps "${svc}"
  sleep 5
done

# ── Health check ─────────────────────────────────────────────────────────
echo "=== Health check ==="
sleep 10

HEALTHY=true
for svc in "${SERVICE_ORDER[@]}"; do
  STATUS=$(docker inspect --format='{{.State.Status}}' "${COMPOSE_PROJECT}-${svc}-1" 2>/dev/null || echo "not found")
  if [ "${STATUS}" = "running" ]; then
    echo "  ✓ ${svc}: running"
  else
    echo "  ✗ ${svc}: ${STATUS}"
    HEALTHY=false
  fi
done

# ── Cleanup ──────────────────────────────────────────────────────────────
echo "=== Cleanup old images ==="
docker image prune -f --filter "label!=keep" 2>/dev/null || true

# ── Summary ──────────────────────────────────────────────────────────────
echo ""
echo "=== Deployment Summary ==="
echo "  Version:  ${VERSION}"
echo "  Target:   ${RHEL_VERSION} ($(cat /etc/redhat-release 2>/dev/null || echo 'unknown'))"
echo "  Status:   $(if ${HEALTHY}; then echo 'HEALTHY'; else echo 'DEGRADED'; fi)"
echo ""
docker compose -p "${COMPOSE_PROJECT}" ps

if ! ${HEALTHY}; then
  echo ""
  echo "Some services are not running. Check logs with:"
  echo "  docker compose -p ${COMPOSE_PROJECT} logs -f <service>"
  exit 1
fi
