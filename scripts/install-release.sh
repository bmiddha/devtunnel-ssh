#!/usr/bin/env bash
# Download and install a prebuilt dtssh binary from GitHub Releases (Linux/macOS).
# No build tools required — the release binaries are self-contained NativeAOT.
#
# Quick install:
#   curl -fsSL https://raw.githubusercontent.com/bmiddha/devtunnel-ssh/main/scripts/install-release.sh | bash
#
# Overrides (env vars):
#   VERSION   release tag to install, e.g. v0.1.0   (default: latest)
#   RID       runtime id, e.g. linux-arm64          (default: auto-detected)
#   PREFIX    install dir                           (default: ~/.local/bin)
#   REPO      owner/name                            (default: bmiddha/devtunnel-ssh)
#   BASE_URL  override the asset base URL (mirror)  (default: the GitHub release)
set -euo pipefail

repo="${REPO:-bmiddha/devtunnel-ssh}"

command -v curl >/dev/null 2>&1 || { echo "error: curl is required." >&2; exit 1; }

# Detect the runtime identifier for this machine.
os="$(uname -s)"
arch="$(uname -m)"
case "$os" in
  Linux)  rid_os="linux" ;;
  Darwin) rid_os="osx" ;;
  *) echo "error: unsupported OS '$os' (use install-release.ps1 on Windows)." >&2; exit 1 ;;
esac
case "$arch" in
  x86_64|amd64)  rid_arch="x64" ;;
  arm64|aarch64) rid_arch="arm64" ;;
  *) echo "error: unsupported architecture '$arch'." >&2; exit 1 ;;
esac
rid="${RID:-$rid_os-$rid_arch}"

# Releases ship Apple Silicon only for macOS.
if [ "$rid" = "osx-x64" ]; then
  echo "error: no osx-x64 release build (Apple Silicon uses osx-arm64; Intel Macs must build from source — see the README)." >&2
  exit 1
fi

asset="dtssh-$rid"
ver="${VERSION:-latest}"
if [ -n "${BASE_URL:-}" ]; then
  base="$BASE_URL"
elif [ "$ver" = "latest" ]; then
  base="https://github.com/$repo/releases/latest/download"
else
  base="https://github.com/$repo/releases/download/$ver"
fi

prefix="${PREFIX:-$HOME/.local/bin}"
mkdir -p "$prefix"

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

echo "Downloading $asset ($ver) from $repo..."
if ! curl -fSL --proto '=https' --tlsv1.2 "$base/$asset" -o "$tmp/$asset"; then
  echo "error: could not download $base/$asset (does that release/asset exist?)." >&2
  exit 1
fi

# Verify the checksum when the release publishes SHA256SUMS.txt.
if curl -fsSL "$base/SHA256SUMS.txt" -o "$tmp/SHA256SUMS.txt" 2>/dev/null; then
  want="$(awk -v f="$asset" '$2==f || $2=="*"f {print $1}' "$tmp/SHA256SUMS.txt" | head -n1)"
  if [ -n "$want" ]; then
    if command -v sha256sum >/dev/null 2>&1; then
      got="$(sha256sum "$tmp/$asset" | awk '{print $1}')"
    else
      got="$(shasum -a 256 "$tmp/$asset" | awk '{print $1}')"
    fi
    if [ "$want" != "$got" ]; then
      echo "error: checksum mismatch for $asset (want $want, got $got)." >&2
      exit 1
    fi
    echo "Checksum OK."
  fi
fi

install -m755 "$tmp/$asset" "$prefix/dtssh"
echo "Installed: $prefix/dtssh"
case ":$PATH:" in
  *":$prefix:"*) ;;
  *) echo "note: add $prefix to your PATH (e.g. export PATH=\"$prefix:\$PATH\")." ;;
esac

echo
"$prefix/dtssh" version || true
echo
"$prefix/dtssh" doctor || true
echo
echo "Next:"
echo "  1. Sign in (once):   dtssh login"
echo "  2. Host:             dtssh host"
echo "     Client:           dtssh discover  &&  ssh <alias>"
echo
echo "Run 'dtssh login' before 'dtssh host'/'dtssh discover' — both need a signed-in devtunnel session."
