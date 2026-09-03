# GitHub Actions Secrets & Environment Variables

## Required Secrets

Add these in **Settings → Secrets and variables → Actions** of your repository.

### Container Registry

| Secret | Description | Example |
|--------|-------------|---------|
| `GITHUB_TOKEN` | Auto-provided by GitHub Actions. Used for GHCR push. | (auto) |

### RHEL 9.8 Host

| Secret | Description | Example |
|--------|-------------|---------|
| `RHEL9_HOST` | SSH hostname or IP | `192.168.1.10` |
| `RHEL9_USER` | SSH username (must have sudo without password) | `deploy` |
| `RHEL9_SSH_KEY` | Private SSH key (ed25519 or RSA) | `-----BEGIN OPENSSH...` |
| `RHEL9_PORT` | SSH port (optional, default 22) | `22` |

### RHEL 10.2 Host

| Secret | Description | Example |
|--------|-------------|---------|
| `RHEL10_HOST` | SSH hostname or IP | `192.168.1.11` |
| `RHEL10_USER` | SSH username | `deploy` |
| `RHEL10_SSH_KEY` | Private SSH key | `-----BEGIN OPENSSH...` |
| `RHEL10_PORT` | SSH port (optional, default 22) | `22` |

## Environment Variables

Set these in **Settings → Environments** → `rhel9-prod` / `rhel10-prod`.

| Variable | Default | Description |
|----------|---------|-------------|
| `DEPLOY_DIR` | `/opt/testgram` | Where docker-compose.yml lives on the host |
| `DEPLOY_URL` | (none) | Public URL shown in GitHub deployment status |

## SSH Key Setup

Generate a dedicated deploy key pair:

```bash
# On your workstation
ssh-keygen -t ed25519 -f deploy_key -N "" -C "github-actions-deploy"

# On each RHEL host
sudo useradd -m -s /bin/bash deploy
sudo mkdir -p /home/deploy/.ssh
sudo cp deploy_key.pub /home/deploy/.ssh/authorized_keys
sudo chown -R deploy:deploy /home/deploy/.ssh
sudo chmod 700 /home/deploy/.ssh
sudo chmod 600 /home/deploy/.ssh/authorized_keys

# Allow deploy user to run docker without sudo
sudo usermod -aG docker deploy
```

The private key goes into `RHEL9_SSH_KEY` / `RHEL10_SSH_KEY` secrets.

## Deploy Directory Setup

On each RHEL host, prepare the deployment directory:

```bash
sudo mkdir -p /opt/testgram
cd /opt/testgram

# Copy from this repository
scp docker/compose/docker-compose.yml deploy@HOST:/opt/testgram/
scp docker/compose/.env deploy@HOST:/opt/testgram/

# Edit .env on the host
vim .env
```

## Running setup-rhel.sh (first time only)

```bash
# Copy to host
scp build/docker/rhel/setup-rhel.sh deploy@HOST:/tmp/

# On host (as root)
sudo /tmp/setup-rhel.sh
```

## Workflow Triggers

| Event | Behavior |
|-------|----------|
| `push` to `main` | Build + deploy to both RHEL hosts |
| `pull_request` to `main` | Build + test only (no deploy) |
| Manual dispatch | Choose RHEL target and deploy |

## Architecture Notes

```
GitHub Actions (ubuntu-latest)
  │
  ├─ build-default    →  ghcr.io/{owner}/testgram/mytelegram-*:latest
  ├─ build-rhel9      →  ghcr.io/{owner}/testgram/mytelegram-*-rhel9:latest
  └─ build-rhel10     →  ghcr.io/{owner}/testgram/mytelegram-*-rhel10:latest
  │
  ├─ deploy-rhel9     →  SSH → RHEL 9.8 host → pull + retag + restart
  └─ deploy-rhel10    →  SSH → RHEL 10.2 host → pull + retag + restart
```

Images are always built on GitHub's runners (Debian/Ubuntu) since .NET SDK
builds are cross-platform. The RHEL-specific Dockerfiles use Red Hat UBI
base images so the final container runs natively on RHEL.
