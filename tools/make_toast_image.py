"""生成提醒用的默认配图 Assets/monitoring.jpg（728x360，和 Toast hero 图比例一致）。纯 PIL 绘制。"""
from PIL import Image, ImageDraw, ImageFilter

W, H = 728, 360
S = 2
w, h = W * S, H * S


def sc(*v):
    return tuple(int(x * S) for x in v)


# 1) 底色：竖向渐变
base = Image.new("RGBA", (w, h))
bd = ImageDraw.Draw(base)
for y in range(h):
    t = y / h
    bd.line([(0, y), (w, y)],
            fill=(int(16 + 10 * t), int(22 + 16 * t), int(31 + 26 * t), 255))

# 2) 顶部偏左的冷光
glow = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse(sc(90, -190, 640, 230), fill=(28, 74, 116, 190))
glow = glow.filter(ImageFilter.GaussianBlur(70 * S))
base = Image.alpha_composite(base, glow)

# 3) 细网格（监控画面感，透明度降到很低）
grid = Image.new("RGBA", (w, h), (0, 0, 0, 0))
gd = ImageDraw.Draw(grid)
for x in range(0, w, 26 * S):
    gd.line([(x, 0), (x, h)], fill=(190, 215, 240, 10), width=1)
for y in range(0, h, 26 * S):
    gd.line([(0, y), (w, y)], fill=(190, 215, 240, 10), width=1)
base = Image.alpha_composite(base, grid)

# 4) 摄像头
layer = Image.new("RGBA", (w, h), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)
cx, cy = w // 2, h // 2
bw, bh = int(168 * S), int(104 * S)
body = (cx - bw // 2, cy - bh // 2 + 10 * S, cx + bw // 2, cy + bh // 2 + 10 * S)
line = (228, 240, 252, 235)
d.rounded_rectangle(body, radius=20 * S, outline=line, width=5 * S)
d.rounded_rectangle((cx - int(28 * S), body[1] - int(22 * S), cx + int(28 * S), body[1] + int(8 * S)),
                    radius=9 * S, outline=line, width=5 * S)

lr = int(40 * S)
d.ellipse((cx - lr, cy - lr + 10 * S, cx + lr, cy + lr + 10 * S), outline=line, width=5 * S)
d.ellipse((cx - int(27 * S), cy - int(27 * S) + 10 * S, cx + int(27 * S), cy + int(27 * S) + 10 * S),
          outline=(126, 176, 216, 220), width=4 * S)
d.ellipse((cx - int(15 * S), cy - int(15 * S) + 10 * S, cx + int(5 * S), cy + int(5 * S) + 10 * S),
          fill=(158, 208, 240, 235))

# 5) 录制点 + 绿光
dot = int(11 * S)
gx, gy = body[2] + int(26 * S), body[3] - int(30 * S)
halo = Image.new("RGBA", (w, h), (0, 0, 0, 0))
ImageDraw.Draw(halo).ellipse((gx - dot * 4, gy - dot * 4, gx + dot * 4, gy + dot * 4), fill=(52, 176, 88, 170))
halo = halo.filter(ImageFilter.GaussianBlur(12 * S))
layer = Image.alpha_composite(layer, halo)
d = ImageDraw.Draw(layer)
d.ellipse((gx - dot, gy - dot, gx + dot, gy + dot), fill=(74, 205, 104, 255))

base = Image.alpha_composite(base, layer)

# 6) 四周压暗
vig = Image.new("L", (w, h), 0)
ImageDraw.Draw(vig).ellipse(sc(-w * 0.18, -h * 0.3, w * 1.18, h * 1.3), fill=255)
vig = vig.filter(ImageFilter.GaussianBlur(70 * S))
dark = Image.new("RGBA", (w, h), (9, 12, 18, 255))
base = Image.composite(base, dark, vig)

base = base.convert("RGB").resize((W, H), Image.LANCZOS)
base.save("Assets/monitoring.jpg", quality=93)
print("Assets/monitoring.jpg 已生成")
