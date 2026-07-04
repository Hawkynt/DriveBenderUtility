#!/usr/bin/env bash
#
# install.sh — build DriveBender, install its runtime dependency, and put the
# 'dbmount' daemon/CLI and the 'drivebender' desktop shell on your $PATH.
#
# Linux and macOS. Framework-dependent build: needs the .NET 10 SDK to build
# and the .NET 10 runtime to run (the SDK includes it). Re-run any time to
# upgrade in place. Windows users: use install.ps1.
#
# Usage:
#   ./install.sh                 # install under ~/.local (per-user, no root for the copy)
#   PREFIX=/usr/local ./install.sh   # system-wide (the symlink step then needs write access)
#   ./install.sh --no-deps       # skip the WebKitGTK package install (GUI then needs it later)
#
set -euo pipefail

# ---- configuration ---------------------------------------------------------
PREFIX="${PREFIX:-$HOME/.local}"
LIBDIR="$PREFIX/lib/drivebender"
BINDIR="$PREFIX/bin"
CONFIGURATION="Release"
INSTALL_DEPS=1

for arg in "$@"; do
  case "$arg" in
    --no-deps) INSTALL_DEPS=0 ;;
    -h|--help) grep '^#' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "unknown option: $arg" >&2; exit 2 ;;
  esac
done

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$REPO_ROOT"

say()  { printf '\033[1;34m==>\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m!!\033[0m %s\n'  "$*" >&2; }
die()  { printf '\033[1;31mERROR:\033[0m %s\n' "$*" >&2; exit 1; }

case "$(uname -s)" in
  Linux)  OS=linux ;;
  Darwin) OS=macos ;;
  *) die "unsupported OS '$(uname -s)'. On Windows run install.ps1 instead." ;;
esac

# ---- 0. toolchain ----------------------------------------------------------
command -v dotnet >/dev/null 2>&1 || die "the .NET SDK ('dotnet') is not on your PATH. Install .NET 10: https://dotnet.microsoft.com/download"
say "Using $(dotnet --version) ($(command -v dotnet))"

# ---- 1. runtime dependency: the WebView engine the desktop shell hosts -----
# dbmount + a browser need nothing extra; only the 'drivebender' GUI window
# needs WebKitGTK on Linux. macOS has WebKit built in.
install_webkit_linux() {
  local sudo=""
  [ "$(id -u)" -ne 0 ] && command -v sudo >/dev/null 2>&1 && sudo="sudo"
  if   command -v pacman  >/dev/null 2>&1; then $sudo pacman -S --needed --noconfirm webkit2gtk-4.1
  elif command -v apt-get >/dev/null 2>&1; then $sudo apt-get update && $sudo apt-get install -y libwebkit2gtk-4.1-0
  elif command -v dnf     >/dev/null 2>&1; then $sudo dnf install -y webkit2gtk4.1
  elif command -v zypper  >/dev/null 2>&1; then $sudo zypper install -y libwebkit2gtk-4_1-0
  else return 1
  fi
}

if [ "$INSTALL_DEPS" -eq 1 ] && [ "$OS" = linux ]; then
  say "Installing the WebView runtime (WebKitGTK) for the desktop shell"
  if ! install_webkit_linux; then
    warn "Could not auto-install WebKitGTK (unknown package manager or no privileges)."
    warn "The 'dbmount' CLI still works; install libwebkit2gtk-4.1 later for the GUI."
  fi
else
  [ "$OS" = macos ] && say "macOS ships WebKit — no WebView dependency to install"
fi

# ---- 2. build & publish into the library dir -------------------------------
MOUNT_TFM="net10.0"   # Linux/macOS flavour of dbmount (FUSE); Windows build is net10.0-windows
APP_TFM="net10.0"

say "Restoring packages"
dotnet restore DriveBender.sln --nologo

say "Publishing dbmount and the desktop shell into $LIBDIR"
rm -rf "$LIBDIR"
mkdir -p "$LIBDIR"
# both land in the SAME directory on purpose: the desktop shell locates dbmount
# as a sibling next to itself (see DriveBender.App/Program.cs::_LocateDbmount).
dotnet publish DriveBender.Mount/DriveBender.Mount.csproj -c "$CONFIGURATION" -f "$MOUNT_TFM" -o "$LIBDIR" --nologo
dotnet publish DriveBender.App/DriveBender.App.csproj     -c "$CONFIGURATION" -f "$APP_TFM"   -o "$LIBDIR" --nologo

[ -x "$LIBDIR/dbmount" ]        || die "publish did not produce $LIBDIR/dbmount"
[ -x "$LIBDIR/DriveBender.App" ] || die "publish did not produce $LIBDIR/DriveBender.App"

# ---- 3. link the two entry points onto $PATH -------------------------------
say "Linking 'dbmount' and 'drivebender' into $BINDIR"
mkdir -p "$BINDIR"
ln -sf "$LIBDIR/dbmount"         "$BINDIR/dbmount"
ln -sf "$LIBDIR/DriveBender.App" "$BINDIR/drivebender"

# ---- 4. done ---------------------------------------------------------------
say "Installed:"
printf '    %s\n' "$BINDIR/dbmount      -> the pool daemon/CLI  (run 'dbmount serve')" \
                  "$BINDIR/drivebender  -> the desktop manager window"

case ":$PATH:" in
  *":$BINDIR:"*) : ;;
  *) warn "$BINDIR is not on your PATH. Add it, e.g.:"
     warn "    echo 'export PATH=\"$BINDIR:\$PATH\"' >> ~/.profile && . ~/.profile" ;;
esac
