"""Writes a one-page, image-only 'scan' (Letter, 300 dpi) for the OCR scenario. Needs Pillow."""
import sys
from PIL import Image, ImageDraw, ImageFont

img = Image.new("L", (2550, 3300), 255)
draw = ImageDraw.Draw(img)
try:
    font = ImageFont.truetype("DejaVuSerif.ttf", 64)
except OSError:
    font = ImageFont.load_default(size=64)
for i, line in enumerate(["Invoice number 4711", "Quantum widgets shipped on Monday",
                          "Total amount due: 1,250 dollars", "Please pay within thirty days"]):
    draw.text((250, 400 + i * 160), line, font=font, fill=0)
img.save(sys.argv[1], "PDF", resolution=300)
