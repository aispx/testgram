#!/usr/bin/env bash
# Shared compose() helper for Testgram admin scripts.
# Handles two real-world quirks of a dockerized deployment:
#   1. `docker-compose` v1 may not be installed, so fall back to `docker compose`.
#   2. The stack may be started under a project name that differs from the
#      compose directory name (e.g. `-p mytelegram`), so auto-detect the name
#      from the running containers instead of guessing.
#
# Source this file and then call `compose exec ...` exactly like you would call
# `docker-compose exec ...`.

# Resolve the docker/compose directory relative to this helper file.
_compose_dir() {
    local script_dir
    script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
    (cd -- "$script_dir/../docker/compose" && pwd)
}

# Detect the compose project name of the running stack that was started from
# this repo's docker/compose directory. Empty when nothing is running.
_compose_project() {
    local compose_dir
    compose_dir="$(_compose_dir)"
    docker ps --format '{{.Label "com.docker.compose.project.working_dir"}}\t{{.Label "com.docker.compose.project"}}' \
        2>/dev/null | awk -F'\t' -v d="$compose_dir" '$1 == d { print $2; exit }'
}

# Run a docker-compose command against the correct project and directory,
# using either the docker-compose v1 binary or the docker compose plugin.
compose() {
    local cmd compose_dir project
    if command -v docker-compose >/dev/null 2>&1; then
        cmd="docker-compose"
    else
        cmd="docker compose"
    fi

    compose_dir="$(_compose_dir)"
    project="$(_compose_project)"

    (
        cd -- "$compose_dir" || exit 1
        if [ -n "$project" ]; then
            $cmd -p "$project" "$@"
        else
            $cmd "$@"
        fi
    )
}
