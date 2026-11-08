"""从原版 maimai 贴图生成「着色用的遮罩 / 预先乘好的圆环」。

原实现是 D3D shader：rgb = tex1.rgb * tex2.rgb * tint，alpha = tex.a * tint.a，
再走 screen / additive 混合。Avalonia 没有逐次绘制的混合模式，所以这里把
「亮度 → alpha」这一步预烘焙成遮罩，运行时只做「遮罩 + 纯色填充」着色。

输入（原样保留，版权见 ASSET-LICENSE.txt）：
  maimai_1519.png  白 + 渐变 alpha  → 星光
  maimai_2694.png  不透明灰度      → 放射光芒
  maimai_3691.png  不透明灰度      → 圆环形状
  maimai_4862.png  不透明彩色      → 圆环颜色
输出：
  mask_1519.png / mask_2694.png  白色 + alpha=原亮度
  ring_4862.png                  rgb 取 4862，alpha = 3691 亮度 × 4862 亮度
"""
from PIL import Image

BASE = r"D:/Dev/MikuGuard/Assets/Hanabi"
LUMA = (0.2126, 0.7152, 0.0722)


def luminance_px(px):
    return int(round(px[0] * LUMA[0] + px[1] * LUMA[1] + px[2] * LUMA[2]))


def white_mask(name):
    src = Image.open(f"{BASE}/{name}").convert("RGBA")
    w, h = src.size
    s = src.load()
    out = Image.new("RGBA", (w, h))
    d = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, a = s[x, y]
            if a == 0:
                d[x, y] = (255, 255, 255, 0)
                continue
            v = max(r, g, b) if a == 255 else (a * max(r, g, b)) // 255
            d[x, y] = (255, 255, 255, v)
    out.save(f"{BASE}/mask_{name.split('_')[1]}", "PNG")
    print("mask", name, "->", f"mask_{name.split('_')[1]}")


def bake_ring():
    shape = Image.open(f"{BASE}/maimai_3691.png").convert("RGBA").load()
    color = Image.open(f"{BASE}/maimai_4862.png").convert("RGBA")
    w, h = color.size
    c = color.load()
    out = Image.new("RGBA", (w, h))
    d = out.load()
    for y in range(h):
        for x in range(w):
            r, g, b, _ = c[x, y]
            v = (luminance_px(shape[x, y]) * luminance_px((r, g, b, 255))) // 255
            d[x, y] = (r, g, b, v)
    out.save(f"{BASE}/ring_4862.png", "PNG")
    print("bake maimai_3691 x maimai_4862 -> ring_4862.png")


white_mask("maimai_1519.png")
white_mask("maimai_2694.png")
bake_ring()
