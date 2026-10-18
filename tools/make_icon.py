"""Draws thunderstore/icon.png (256x256): a gear inside two circular sync arrows."""
import math
import sys
from PIL import Image, ImageDraw

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Background: dark rounded square with a subtle vertical gradient.
bg = Image.new("RGBA", (S, S))
for y in range(S):
    t = y / S
    c = (int(28 + 10 * t), int(36 + 12 * t), int(48 + 14 * t), 255)
    ImageDraw.Draw(bg).line([(0, y), (S, y)], fill=c)
mask = Image.new("L", (S, S), 0)
ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=180, fill=255)
img.paste(bg, (0, 0), mask)

cx, cy = S / 2, S / 2
GOLD = (232, 176, 72, 255)
TEAL = (88, 196, 186, 255)

# Two sync arrows (arcs with arrowheads).
R, W = 380, 70
def arc_arrow(start, end, color):
    d.arc([cx - R, cy - R, cx + R, cy + R], start=start, end=end, fill=color, width=W)
    a = math.radians(end)
    tip_dir = a + math.pi / 2  # clockwise tangent
    px, py = cx + R * math.cos(a), cy + R * math.sin(a)
    size = 120
    tip = (px + size * math.cos(tip_dir), py + size * math.sin(tip_dir))
    left = (px + (W * 1.3) * math.cos(a), py + (W * 1.3) * math.sin(a))
    right = (px - (W * 1.3) * math.cos(a), py - (W * 1.3) * math.sin(a))
    d.polygon([tip, left, right], fill=color)

arc_arrow(200, 320, TEAL)
arc_arrow(20, 140, TEAL)

# Gear.
teeth, r_out, r_in = 10, 230, 185
pts = []
for i in range(teeth * 4):
    ang = 2 * math.pi * i / (teeth * 4)
    r = r_out if (i % 4) in (1, 2) else r_in
    pts.append((cx + r * math.cos(ang), cy + r * math.sin(ang)))
d.polygon(pts, fill=GOLD)
d.ellipse([cx - 85, cy - 85, cx + 85, cy + 85], fill=(28, 36, 48, 255))

out = img.resize((256, 256), Image.LANCZOS)
out.save(sys.argv[1] if len(sys.argv) > 1 else "icon.png")
