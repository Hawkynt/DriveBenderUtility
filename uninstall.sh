#!/usr/bin/env bash
#
# uninstall.sh — remove what install.sh installed: the 'dbmount' / 'drivebender'
# launchers on $PATH and the published lib directory. Mirrors install.sh's layout.
#
# It does NOT touch your pools, their data, or your config in
# ~/.config/DriveBenderUtility (or /usr/share on a system install) — removing the
# tool never removes a storage array. Delete that directory by hand if you want a
# completely clean slate. It also leaves the WebKitGTK system package in place.
#
# Usage:
#   ./uninstall.sh                   # undo a per-user install under ~/.local
#   PREFIX=/usr/local ./uninstall.sh # undo a system-wide install
#   ./uninstall.sh --purge-config    # ALSO delete the config/registry dir (not pool data)
#
set -euo pipefail

PREFIX="${PREFIX:-$HOME/.local}"
LIBDIR="$PREFIX/lib/drivebender"
BINDIR="$PREFIX/bin"
PURGE_CONFIG=0

for arg in "$@"; do
  case "$arg" in
    --purge-config) PURGE_CONFIG=1 ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n'  "$*" >&2; }

# only remove the bin entries if they point into our lib dir — never clobber an unrelated
# 'dbmount' the user put on their PATH themselves.
remove_link() {
  local link="$1"
  if [ -L "$link" ]; then
    local dest; dest="$(readlink -f "$link" 2>/dev/null || true)"
    case "$dest" in
      "$LIBDIR"/*) say "Removing $link"; rm -f "$link" ;;
      *) warn "Leaving $link (does not point into $LIBDIR)" ;;
    esac
  elif [ -e "$link" ]; then
    warn "Leaving $link (not a symlink — not created by install.sh)"
  fi
}

remove_link "$BINDIR/dbmount"
remove_link "$BINDIR/drivebender"

if [ -d "$LIBDIR" ]; then
  say "Removing $LIBDIR"
  rm -rf "$LIBDIR"
else
  warn "$LIBDIR not found — nothing to remove there"
fi

if [ "$PURGE_CONFIG" -eq 1 ]; then
  CONFIG="${XDG_CONFIG_HOME:-$HOME/.config}/DriveBenderUtility"
  if [ -d "$CONFIG" ]; then
    say "Purging config/registry $CONFIG (pool DATA on your drives is untouched)"
    rm -rf "$CONFIG"
  fi
fi

say "Uninstalled. Any pool still mounted keeps serving until you unmount it (umount <target>)."
