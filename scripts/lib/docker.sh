#!/bin/bash
# Checks that the docker CLI with the compose plugin is on PATH and the daemon answers.
# Never installs anything — see the README's "Prerequisites".

lodge_ensure_docker() {
    if ! command -v docker >/dev/null 2>&1; then
        echo "error: docker not found on PATH (see README.md, Prerequisites)." >&2
        return 1
    fi
    if ! docker compose version >/dev/null 2>&1; then
        echo "error: the docker compose plugin is missing (see README.md, Prerequisites)." >&2
        return 1
    fi
    if ! docker info >/dev/null 2>&1; then
        echo "error: the docker daemon isn't reachable — is it running, and can this user use it?" >&2
        return 1
    fi
}
