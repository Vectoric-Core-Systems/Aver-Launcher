"""
Renders the launcher's brand assets from the SVG masters.

    python branding/build-brand.py

Outputs into branding/ and copies the two the app embeds into src/Aver.Launcher.App/Assets/.

WHY HEADLESS CHROME. Pillow cannot rasterise SVG, and the alternatives (cairosvg, rsvg) are native
dependencies this repo does not otherwise need. Chrome is already on this machine and is what the
engine's own scripts/brand.py uses, so the two brands are produced by the same pipeline.

WHY TWO MASTERS. An .ico may carry different artwork per size, and this one has to. The pad is the
only thing distinguishing the launcher mark from the engine's cube, and at 16 px the master's
13-unit bar renders under a pixel -- the two icons would become the same picture in the taskbar,
which is the one thing this mark exists to avoid. Sizes <= 24 use the small variant.
"""

import shutil
import subprocess
import sys
from pathlib import Path

from PIL import Image

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
ASSETS = REPO / "src" / "Aver.Launcher.App" / "Assets"

CHROME_CANDIDATES = [
    Path(r"C:\Program Files\Google\Chrome\Application\chrome.exe"),
    Path(r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"),
]

# Sizes at or below this use the small-size master.
SMALL_AT_OR_BELOW = 24

ICON_SIZES = [16, 20, 24, 32, 48, 64, 128, 256]

# Masters are rasterised at this size and downscaled from it. 512 gives every target an exact or
# near-exact integer ratio without asking Chrome for a viewport it renders badly.
MASTER_PX = 512


def accent_pixels(img: Image.Image) -> int:
    """Count Aver-orange pixels. Zero means the render failed, whatever the file size says."""
    return sum(
        1
        for r, g, b, a in img.convert("RGBA").getdata()
        if a > 10 and r > 150 and 60 < g < 170 and b < 100
    )


def find_chrome() -> Path:
    for c in CHROME_CANDIDATES:
        if c.exists():
            return c
    sys.exit("chrome.exe not found; edit CHROME_CANDIDATES")


WRAPPER = """<!doctype html><meta charset="utf-8">
<style>
  html, body {{ margin:0; padding:0; width:100%; height:100%; background:transparent; overflow:hidden; }}
  svg {{ display:block; width:100vw; height:100vh; }}
</style>
{svg}
"""


def render(chrome: Path, svg: Path, out: Path, size: int, tmp: Path) -> None:
    """Rasterise one SVG at one size, on a transparent background.

    Goes through an HTML wrapper rather than loading the .svg directly. Opening an SVG that declares
    width="256" height="256" renders it at that INTRINSIC size, and --window-size then merely crops
    the viewport -- so every size below 256 came out as the dark top-left corner of the tile, with
    not one orange pixel in it. The wrapper strips the intrinsic size and lets the viewBox scale to
    the viewport, which is what makes the size argument mean anything.
    """
    out.parent.mkdir(parents=True, exist_ok=True)
    markup = svg.read_text(encoding="utf-8")
    markup = markup.replace('width="256" height="256"', 'width="100%" height="100%"')
    page = tmp / f"{svg.stem}-{size}.html"
    page.write_text(WRAPPER.format(svg=markup), encoding="utf-8")

    subprocess.run(
        [
            str(chrome),
            "--headless",
            "--disable-gpu",
            "--hide-scrollbars",
            # Transparent, so the tiled masters keep their own rounded corners rather than being
            # squared off by a white page behind them.
            "--default-background-color=00000000",
            f"--screenshot={out}",
            f"--window-size={size},{size}",
            page.as_uri(),
        ],
        check=True,
        capture_output=True,
    )

    with Image.open(out) as im:
        if im.size != (size, size):
            im.convert("RGBA").resize((size, size), Image.LANCZOS).save(out)


def main() -> int:
    chrome = find_chrome()
    tmp = HERE / "_render"
    tmp.mkdir(exist_ok=True)

    icon_big = HERE / "launcher-icon.svg"
    icon_small = HERE / "launcher-icon-small.svg"
    mark_big = HERE / "launcher-mark.svg"

    # ---- the .ico ladder, per-size artwork ----
    #
    # Each MASTER is rasterised once at high resolution and then downscaled to each target size,
    # rather than asking Chrome for a 16x16 viewport. Chrome does not render reliably at that size --
    # it produced frames containing nothing but the tile's dark corner -- and Pillow's LANCZOS
    # downscale is better than anything a 16px viewport would give anyway.
    #
    # This still yields per-size ARTWORK: sizes at or below SMALL_AT_OR_BELOW come from the small
    # master, whose pad is thick enough to survive the reduction, and the rest from the full one.
    masters = {}
    for label, src in (("big", icon_big), ("small", icon_small)):
        png = tmp / f"master-{label}.png"
        render(chrome, src, png, MASTER_PX, tmp)
        img = Image.open(png).convert("RGBA")
        if not accent_pixels(img):
            sys.exit(f"render produced no accent pixels from {src.name}")
        masters[label] = img
        print(f"  master {label:<5} <- {src.name}  ({img.size[0]}px)")

    frames = []
    for size in ICON_SIZES:
        label = "small" if size <= SMALL_AT_OR_BELOW else "big"
        img = masters[label].resize((size, size), Image.LANCZOS)

        # A frame with no accent pixel is a rendering failure, not a design choice. This check is
        # here because the first version of this script silently shipped eight empty frames.
        if not accent_pixels(img):
            sys.exit(f"no accent pixels survived the downscale to {size}px ({label} master)")

        frames.append(img)
        print(f"  {size:>3}px  <- {label} master, {accent_pixels(img)} accent px")

    ico = HERE / "launcher-icon.ico"
    # Pillow writes every requested size from the base image, so hand it the largest and let
    # append_images supply the rest -- that is what preserves the per-size artwork.
    frames[-1].save(
        ico,
        format="ICO",
        sizes=[(s, s) for s in ICON_SIZES],
        append_images=frames[:-1],
    )
    print(f"wrote {ico.name}")

    # ---- the transparent mark, for the nav rail ----
    logo = HERE / "launcher-logo.png"
    render(chrome, mark_big, logo, 512, tmp)
    print(f"wrote {logo.name}")

    # ---- what the app embeds ----
    ASSETS.mkdir(parents=True, exist_ok=True)
    shutil.copy2(ico, ASSETS / "launcher-icon.ico")
    shutil.copy2(logo, ASSETS / "launcher-logo.png")
    print(f"copied both into {ASSETS.relative_to(REPO)}")

    shutil.rmtree(tmp, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
