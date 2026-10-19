"""Build the logo assets from assets/rosetta-floppy.png.

The floppy is the README banner as-is. The app icon is the stone tablet printed on its
label (bird | あ | A), cut out with a transparent background.
Outputs:
  assets/rosetta.png   the tablet only, transparent, square
  assets/rosetta.ico   multi-size icon (exe, installer, tray)
Run: python scripts/make-icon.py
"""
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / "assets" / "rosetta-floppy.png"
SIZES = [16, 24, 32, 48, 64, 128, 256]

# Region of the floppy that holds the tablet, with a little label around it. The red
# rule under the tablet is outside this box (it would otherwise join the tablet).
BOX = (165, 220, 560, 352)

img = Image.open(SRC).convert("RGB")
crop = img.crop(BOX)
a = np.asarray(crop).astype(np.float32)

# The label is cream; the tablet is pinker and has a dark outline.
label = np.median(np.concatenate([a[:3].reshape(-1, 3), a[-3:].reshape(-1, 3)]), axis=0)
dist = np.abs(a - label).sum(axis=2)
mask = dist > 40
mask = ndimage.binary_closing(mask, structure=np.ones((3, 3)), iterations=2)
lab, n = ndimage.label(mask)
sizes = ndimage.sum(np.ones_like(lab), lab, range(1, n + 1))
tablet = lab == (int(np.argmax(sizes)) + 1)
tablet = ndimage.binary_fill_holes(tablet)
# Halftone dots and label texture leave specks along the edge; open them away, then keep
# only the biggest piece again.
tablet = ndimage.binary_opening(tablet, structure=np.ones((3, 3)), iterations=5)
lab, n = ndimage.label(tablet)
sizes = ndimage.sum(np.ones_like(lab), lab, range(1, n + 1))
tablet = lab == (int(np.argmax(sizes)) + 1)
tablet = ndimage.binary_dilation(tablet, structure=np.ones((3, 3)), iterations=1)

alpha = ndimage.gaussian_filter(tablet.astype(np.float32), 1.2)
alpha = np.clip((alpha - 0.35) / 0.3, 0, 1)

rgba = np.dstack([a, alpha * 255]).astype(np.uint8)
stone = Image.fromarray(rgba, "RGBA")
stone = stone.crop(stone.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox())

side = int(max(stone.size) * 1.04)
canvas = Image.new("RGBA", (side, side), (0, 0, 0, 0))
canvas.paste(stone, ((side - stone.width) // 2, (side - stone.height) // 2), stone)
canvas = canvas.resize((512, 512), Image.LANCZOS)
canvas.save(ROOT / "assets" / "rosetta.png")
canvas.save(ROOT / "assets" / "rosetta.ico", sizes=[(s, s) for s in SIZES])
print("done", stone.size)
