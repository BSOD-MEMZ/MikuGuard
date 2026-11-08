"""从原版 maimai 贴图生成「着色用的遮罩 / 预先乘好的圆环」。

原实现是 D3D 的 SpritePixel：rgb = tex1.rgb * tex2.rgb * exp2(exposure) * tint.rgb，
alpha = tex.a * tint.a，再按材质走 alpha / additive / screen 混合。
Avalonia 这边没法在 shader 里逐次算，所以把「两张贴图相乘 + 曝光」预烘焙成一张图；
运行时只用 paint 的颜色矩阵乘 tint、用 paint 的 alpha 乘 tint.a。

输入（原样保留，版权见 ASSET-LICENSE.txt）：
  maimai_1519.png  白 + 渐变 alpha  → 星光
  maimai_2694.png  不透明灰度      → 放射光芒
  maimai_3691.png  不透明灰度      → 圆环形状
  maimai_4862.png  不透明彩色      → 圆环颜色
输出：
  mask_1519.png / mask_2694.png  白色 + alpha=原亮度（这两张就靠亮度当形状，见 SpritePixel）
  ring_4862.png                  rgb = 3691×4862×exp2(2.3)，alpha 恒为 255（不透明的加法图）
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
    """圆环 = 3691(形状/亮度) × 4862(色轮)，按 SpritePixel 的公式原样算。

    原 shader（material 50 走 spritePS）:
        rgb   = tex0.rgb * tex1.rgb * exp2(exposure) * tint.rgb      // 两张都是不透明贴图
        a     = tex0.a * tint.a = tint.a                            // tex0.a 实测 254~255
        out   = float4(rgb * a, ...)，混合状态是 ONE / ONE（加法）
    exposure 对两个圆环都是 2.3，所以 exp2 = 4.9246，乘完在 8bit 中间缓冲里就夹到 1 了
    ——这也是官方截图里那圈圆环偏白、不饱和的原因。

    所以这里**不再把亮度挪进 alpha**（之前那样做色相永远满饱和，渲染出来是个彩色盘子）：
        rgb   = clamp(3691.rgb/255 * 4862.rgb/255 * 4.9246) * 255
        alpha = 255（由运行时的 tint.a 单独乘）
    """
    shape = Image.open(f"{BASE}/maimai_3691.png").convert("RGBA").load()
    color = Image.open(f"{BASE}/maimai_4862.png").convert("RGBA")
    w, h = color.size
    c = color.load()
    out = Image.new("RGBA", (w, h))
    d = out.load()
    gain = 2.0 ** 2.3
    for y in range(h):
        for x in range(w):
            r, g, b, _ = c[x, y]
            sr, sg, sb, _ = shape[x, y]
            d[x, y] = (
                min(255, int(r * sr / 255 * gain)),
                min(255, int(g * sg / 255 * gain)),
                min(255, int(b * sb / 255 * gain)),
                255,
            )
    out.save(f"{BASE}/ring_4862.png", "PNG")
    print("bake maimai_3691 x maimai_4862 (x exp2(2.3), alpha=255) -> ring_4862.png")


white_mask("maimai_1519.png")
white_mask("maimai_2694.png")
bake_ring()
