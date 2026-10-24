from PIL import Image, ImageDraw, ImageFilter
import math

S = 1024
img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# rounded square background with vertical gradient
bg = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd = ImageDraw.Draw(bg)
for y in range(S):
    t = y / (S - 1)
    r = int(24 + 12 * t)
    g = int(34 + 22 * t)
    b = int(48 + 34 * t)
    bd.line([(0, y), (S, y)], fill=(r, g, b, 255))
mask = Image.new("L", (S, S), 0)
md = ImageDraw.Draw(mask)
md.rounded_rectangle([0, 0, S - 1, S - 1], radius=int(S * 0.22), fill=255)
img.paste(bg, (0, 0), mask)

d = ImageDraw.Draw(img)

# soft glow ring
glow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gd = ImageDraw.Draw(glow)
gd.ellipse([S*0.18, S*0.18, S*0.82, S*0.82], fill=(59, 130, 246, 26))
glow = glow.filter(ImageFilter.GaussianBlur(40))
img.alpha_composite(glow)
d = ImageDraw.Draw(img)

cx, cy = S / 2, S / 2
# graph nodes (diamond)
nodes = [
    (cx, cy - S*0.235, (59, 130, 246), 1.0),    # top  teal
    (cx - S*0.245, cy + S*0.07, (96, 165, 250), 1.0),  # left blue
    (cx + S*0.245, cy + S*0.07, (251, 146, 60), 1.0),  # right orange
    (cx, cy + S*0.27, (248, 113, 113), 1.0),    # bottom red (warning)
]
edges = [(0, 1), (0, 2), (1, 3), (2, 3), (1, 2)]
for a, b in edges:
    x1, y1, _, _ = nodes[a]
    x2, y2, _, _ = nodes[b]
    d.line([(x1, y1), (x2, y2)], fill=(148, 163, 184, 190), width=int(S*0.018))
    # arrow-ish dot
    ang = math.atan2(y2 - y1, x2 - x1)
    ex, ey = x2 - math.cos(ang) * S*0.075, y2 - math.sin(ang) * S*0.075
    d.ellipse([ex-S*0.012, ey-S*0.012, ex+S*0.012, ey+S*0.012], fill=(226, 232, 240, 220))

for x, y, col, _ in nodes:
    r = S * 0.078
    # halo
    for i in range(6, 0, -1):
        rr = r + i * S * 0.012
        d.ellipse([x - rr, y - rr, x + rr, y + rr], fill=(*col, 12))
    d.ellipse([x - r, y - r, x + r, y + r], fill=(*col, 255))
    d.ellipse([x - r*0.62, y - r*0.62, x + r*0.62, y + r*0.62], fill=(255, 255, 255, 52))

# center small "fish" mark: simple body + tail, white-ish
fx, fy = cx, cy - S*0.025
body_w, body_h = S*0.155, S*0.088
d.ellipse([fx - body_w/2, fy - body_h/2, fx + body_w/2, fy + body_h/2], fill=(241, 245, 249, 235))
d.polygon([
    (fx + body_w*0.42, fy),
    (fx + body_w*0.70, fy - body_h*0.62),
    (fx + body_w*0.70, fy + body_h*0.62),
], fill=(241, 245, 249, 235))
d.ellipse([fx - body_w*0.30, fy - body_h*0.16, fx - body_w*0.18, fy - body_h*0.04], fill=(30, 41, 59, 255))

img = img.resize((256, 256), Image.LANCZOS)
img.save("src/PackDepTool.App/Assets/app.png")
img.save("src/PackDepTool.App/Assets/app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon written")
