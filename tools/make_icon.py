"""生成 MikuGuard 插件图标（icon.png）。纯本地 PIL 绘制，不调用任何生成模型。"""
from PIL import Image, ImageDraw, ImageFilter

S = 4  # 超采样倍数
W = 256
img = Image.new("RGBA", (W * S, W * S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)


def sc(*v):
    return tuple(int(x * S) for x in v)


# 背景：深色圆角方块 + 竖向渐变
bg = Image.new("RGBA", (W * S, W * S), (0, 0, 0, 0))
bdraw = ImageDraw.Draw(bg)
for y in range(W * S):
    t = y / (W * S)
    r = int(27 + (12 - 27) * t)
    g = int(35 + (18 - 35) * t)
    b = int(48 + (28 - 48) * t)
    bdraw.line([(0, y), (W * S, y)], fill=(r, g, b, 255))
mask = Image.new("L", (W * S, W * S), 0)
ImageDraw.Draw(mask).rounded_rectangle(sc(8, 8, W - 8, W - 8), radius=52 * S, fill=255)
img.paste(bg, (0, 0), mask)
d = ImageDraw.Draw(img)

# 摄像头机身
body = (52, 84, 190, 190)
d.rounded_rectangle(sc(*body), radius=20 * S, fill=(246, 250, 255, 255))
d.rounded_rectangle(sc(96, 62, 146, 90), radius=10 * S, fill=(246, 250, 255, 255))

# 镜头
cx, cy = 128, 140
d.ellipse(sc(cx - 34, cy - 34, cx + 34, cy + 34), fill=(20, 30, 42, 255))
d.ellipse(sc(cx - 24, cy - 24, cx + 24, cy + 24), fill=(58, 96, 132, 255))
d.ellipse(sc(cx - 14, cy - 14, cx + 14, cy + 14), fill=(26, 40, 56, 255))
d.ellipse(sc(cx - 18, cy - 22, cx - 4, cy - 8), fill=(200, 230, 255, 210))

# 绿色录制点 + 光晕
glow = Image.new("RGBA", (W * S, W * S), (0, 0, 0, 0))
ImageDraw.Draw(glow).ellipse(sc(186, 42, 236, 92), fill=(67, 200, 96, 150))
glow = glow.filter(ImageFilter.GaussianBlur(7 * S))
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)
d.ellipse(sc(192, 48, 230, 86), fill=(48, 176, 80, 255))
d.ellipse(sc(198, 54, 224, 80), fill=(126, 226, 148, 255))

img = img.resize((W, W), Image.LANCZOS)
img.save("icon.png")
print("icon.png 已生成")
