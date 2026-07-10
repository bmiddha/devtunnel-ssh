#!/usr/bin/env bash
# Render the project SVG assets to PNG.
#
# Produces:
#   assets/logo.png            256x256  (README / general use)
#   assets/logo-512.png        512x512  (hi-dpi)
#   assets/social-preview.png  1280x640 (GitHub repo "Social preview")
#
# Uses the first available rasterizer: rsvg-convert, inkscape, ImageMagick,
# or Python cairosvg. Install one, e.g.:
#   apt install librsvg2-bin   # rsvg-convert
#   pip install cairosvg
set -euo pipefail

cd "$(dirname "$0")/.."
assets="assets"

render() { # <src.svg> <out.png> <width> <height>
  local src="$1" out="$2" w="$3" h="$4"
  if command -v rsvg-convert >/dev/null 2>&1; then
    rsvg-convert -w "$w" -h "$h" "$src" -o "$out"
  elif command -v inkscape >/dev/null 2>&1; then
    inkscape "$src" -w "$w" -h "$h" -o "$out" >/dev/null 2>&1
  elif command -v magick >/dev/null 2>&1; then
    magick -background none -density 384 "$src" -resize "${w}x${h}" "$out"
  elif command -v convert >/dev/null 2>&1; then
    convert -background none -density 384 "$src" -resize "${w}x${h}" "$out"
  elif command -v python3 >/dev/null 2>&1 && python3 -c "import cairosvg" >/dev/null 2>&1; then
    python3 - "$src" "$out" "$w" "$h" <<'PY'
import sys, cairosvg
_, src, out, w, h = sys.argv
cairosvg.svg2png(url=src, write_to=out, output_width=int(w), output_height=int(h))
PY
  else
    echo "error: no SVG rasterizer found (need rsvg-convert, inkscape, ImageMagick, or python cairosvg)" >&2
    exit 1
  fi
  echo "rendered $out (${w}x${h})"
}

render "$assets/logo.svg"           "$assets/logo.png"           256 256
render "$assets/logo.svg"           "$assets/logo-512.png"       512 512
render "$assets/social-preview.svg" "$assets/social-preview.png" 1280 640
