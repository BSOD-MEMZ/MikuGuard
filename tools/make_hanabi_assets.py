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
（圆环不再预烘焙：运行时用 HanabiRenderer.Sprite() 把 3691×4862×2^exposure 合成一张图，
 alpha 取 max(rgb)——原版 SpritePixel 就是这么算的，预烘焙成 PNG 会把 rgb>alpha 夹掉）
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


white_mask("maimai_1519.png")
white_mask("maimai_2694.png")
