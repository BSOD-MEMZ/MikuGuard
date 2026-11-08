"""rainbow.hlsl 的逐行参考实现（numpy），用来对照官方渲染图验证移植是否正确。

用法:
  python hanabi_ref.py <out.png> [t=0.5] [off=0.0] [cx=240] [cy=300] [z=1056] [theta=0]
"""
import sys
import numpy as np
from PIL import Image

W, H = 960, 600

PINK = (0.9294118285179138, 0.22745099663734436, 0.6117647290229797)
BLUE = (0.0, 0.6274510025978088, 0.9137256145477295)
LGREEN = (0.7215685844421387, 0.8235294818878174, 0.0)
YELLOW = (1.0, 0.847058892250061, 0.0)
ORANGE = (1.0, 0.3529411852359772, 0.07058823853731155)


def render(t=0.5, off=0.0, cx=240.0, cy=300.0, z=1056.0, theta=0.0, w=W, h=H):
    ys, xs = np.mgrid[0:h, 0:w].astype(np.float32)
    xs += 0.5
    ys += 0.5

    # SpriteVertex 的反变换：uv = R(-theta)*(p-center)/z + 0.5
    dx = xs - cx
    dy = ys - cy
    c, s = np.cos(-theta), np.sin(-theta)
    ux = (c * dx - s * dy) / z + 0.5
    uy = (s * dx + c * dy) / z + 0.5

    # ---- atan2 近似（逐行照抄）----
    r0x = uy * 2.0 - 1.0
    r0y = ux * 2.0 - 1.0
    az = 1.0 / np.maximum(np.abs(r0y), np.abs(r0x))
    aw = np.minimum(np.abs(r0y), np.abs(r0x))
    az = az * aw
    aw = az * az
    t1 = aw * 0.0208351 - 0.085133
    t1 = aw * t1 + 0.180141
    t1 = aw * t1 - 0.3302995
    aw = aw * t1 + 0.999866
    t1 = aw * az
    t1 = t1 * -2.0 + 1.5707964
    t1 = (1.0 - (np.abs(r0y) >= np.abs(r0x))) * t1
    az = az * aw + t1
    aw = (1.0 - (r0y >= -r0y)) * -3.1415927410125732
    az = aw + az

    # 象限修正
    mn = np.minimum(r0y, r0x)
    mx = np.maximum(r0y, r0x)
    quad = (mx >= -mx) * (1.0 - (mn >= -mn))
    ang = np.where(quad >= 0.5, -az, az) + np.pi
    ang = ang * 3.0

    a_y = np.mod(ang * 0.159154937, 1.0)
    a_z = np.mod(ang * 0.795774698, 1.0)
    a_x = np.mod(ang * 0.795774698 + 0.03, 1.0)

    # 色板偏移（floor(9t)/5，这里 off 直接给 0..0.2 的步进）
    Y = np.mod(np.floor(9.0 * t) * 0.2 + a_y, 1.0)

    S = (0.56 >= a_x).astype(np.float32)     # 无网点的那一半
    Bg = (0.5 >= a_z).astype(np.float32)     # 灰色那一半

    # 5 段上色
    col = np.empty(a_y.shape + (3,), np.float32)
    for i, ch in enumerate(range(3)):
        col[..., i] = np.where(Y >= 0.8, PINK[ch],
                             np.where(Y >= 0.6, BLUE[ch],
                                    np.where(Y >= 0.4, LGREEN[ch],
                                           np.where(Y >= 0.2, YELLOW[ch], ORANGE[ch]))))

    # 屏幕空间网点：(position/frame*2-1)*64 取 frac
    gy = (xs / w) * 2.0 - 1.0
    gw = (1.0 - ys / h) * 2.0 - 1.0
    gy = np.mod(gy * 64.0, 1.0) - 0.5
    gw = np.mod(gw * 64.0, 1.0) - 0.5
    dots = 1.0 - np.sqrt(gy * gy + gw * gw)
    dots = dots * dots

    tint_a = 1.0 - t          # 彩虹光环的 alpha 曲线是 1→0
    aw2 = 1.0 - tint_a
    ring_off = 2.0 * aw2
    alpha_gate = np.clip(aw2 / 0.2, 0.0, 1.0)

    radial = np.sqrt((ux - 0.5) ** 2 + (uy - 0.5) ** 2)

    rc = np.mod(radial * 4.0 - ring_off, 1.0)
    ry = rc * rc * 1.125 + 0.1
    z1 = (rc - 0.8) * -25.0 + 1.0
    z2 = (rc - 0.82) * -2.7777777 + 0.5
    sx = (rc <= 0.8).astype(np.float32)
    sy = (rc <= 0.82).astype(np.float32)
    sz = (rc <= 1.0).astype(np.float32)
    band = -z1 + ry
    band = sx * band + z1
    band = -z2 + band
    band = sy * band + z2
    band = band * sz

    mask = dots * band - 0.1
    mask = np.clip(mask * 24.0 + 0.1, 0.0, 1.0)
    mask = 1.0 - mask
    sector = np.where(S >= 0.5, 1.0, mask)

    k = np.abs(radial - 0.25) * 3.0 + 0.25
    rgb = col * (1.0 + sector[..., None] * (k[..., None] - 1.0))
    rgb = np.where(Bg[..., None] >= 0.5, k[..., None], rgb)
    alpha_scale = np.where(Bg >= 0.5, 0.5, 1.0)

    a1 = np.clip((radial - alpha_gate) * -3.0 - 0.5, 0.0, 1.0)
    a2 = np.clip((np.clip(tint_a / 0.4, 0.0, 1.0) + radial) * 2.0 - 1.0, 0.0, 1.0)
    a = a1 * a2
    a = a * (radial <= 0.5) * alpha_scale
    near = 0.66 * 0.2499 + 0.25
    a = np.clip(-(radial - near) / (0.5 - near) + 1.0, 0.0, 1.0) * a
    a = a * 0.75

    out = np.empty((h, w, 4), np.float32)
    out[..., :3] = np.clip(rgb * a[..., None], 0, 1)
    out[..., 3] = a
    return out


if __name__ == "__main__":
    path = sys.argv[1]
    kw = {}
    for arg in sys.argv[2:]:
        k, v = arg.split("=")
        kw[k] = float(v)
    t = kw.pop("t", 0.5)
    off = kw.pop("off", 0.0)
    img = render(t=t, **kw)
    bg = np.zeros_like(img)
    bg[..., 3] = 1.0
    comp = img[..., :3] + bg[..., :3] * (1 - img[..., 3:4])
    Image.fromarray((np.clip(comp, 0, 1) ** (1 / 2.2) * 255).astype(np.uint8)).save(path)
    print("->", path)
