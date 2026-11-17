#!/usr/bin/env bash
# Reproduce this Debian x86_64 cloud build without changing system packages.
# Downloads development packages into /workspace; not needed on provisioned hosts.
set -euo pipefail
TASK_DEPS=/workspace/hd2d-deps
TASK_VENV=/workspace/hd2d-venv
python3 -m venv "$TASK_VENV"
"$TASK_VENV/bin/pip" install --no-cache-dir cmake==3.31.10 ninja==1.13.0
mkdir -p "$TASK_DEPS"/{debs,lists/partial,cache/archives/partial,apt-empty}
printf '%s\n' 'deb https://deb.debian.org/debian trixie main' > "$TASK_DEPS/sources.list"
cat > "$TASK_DEPS/apt.conf" <<EOF
Dir::Etc::parts "$TASK_DEPS/apt-empty";
Dir::Etc::main "-";
Dir::Etc::sourcelist "$TASK_DEPS/sources.list";
Dir::Etc::sourceparts "$TASK_DEPS/apt-empty";
Dir::State::lists "$TASK_DEPS/lists";
Dir::Cache "$TASK_DEPS/cache";
APT::Sandbox::User "$(id -un)";
EOF
APT_CONFIG="$TASK_DEPS/apt.conf" /usr/bin/apt-get update
(
    cd "$TASK_DEPS/debs"
    APT_CONFIG="$TASK_DEPS/apt.conf" /usr/bin/apt-get download \
        libsdl2-dev=2.32.4+dfsg-1 \
        libgl-dev=1.7.0-1+b2 libglx-dev=1.7.0-1+b2 \
        libopengl-dev=1.7.0-1+b2 libglvnd-dev=1.7.0-1+b2 \
        libegl-dev=1.7.0-1+b2 libgles-dev=1.7.0-1+b2 \
        libgles1=1.7.0-1+b2 libopengl0=1.7.0-1+b2 \
        libx11-dev=2:1.8.12-1 x11proto-dev=2024.1-1
)
python3 - "$TASK_DEPS" <<'PY'
import os
from pathlib import Path
import subprocess
import sys
base = Path(sys.argv[1])
prefix = base / 'prefix'
for package in (base / 'debs').glob('*.deb'):
    subprocess.run(['dpkg-deb', '-x', str(package), str(prefix)], check=True)
# Development symlinks refer to already-installed matching runtime libraries.
# Supply only absent targets in our local prefix; never change /usr.
for link in list(prefix.rglob('*')):
    if link.is_symlink() and not link.exists():
        target = link.parent / os.readlink(link)
        if target.is_relative_to(prefix):
            system = Path('/') / target.relative_to(prefix)
            if system.exists():
                target.symlink_to(system)
PY
printf '%s\n' 'Local dependencies ready; use the CMake command in SPRINT_0_REPORT.md.'
