"""Renders the hideout chalk outline of the Ray Gun (the client builds the same drawing at runtime from icons/raygun.png).

    C:\\Git\\.venv\\Scripts\\python.exe -I tools\\make_chalk_preview.py
"""
import os
import random

import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SOURCE = os.path.join(HERE, "..", "icons", "raygun.png")
OUT = os.path.join(HERE, "..", "assets", "preview", "chalk_outline.png")
W = 768


def noise(h, w, scale, seed):
    """Smooth value noise in 0..1."""
    rng = np.random.default_rng(seed)
    gh, gw = int(h * scale) + 2, int(w * scale) + 2
    grid = rng.random((gh, gw))
    ys, xs = np.mgrid[0:h, 0:w]
    fy, fx = ys * scale, xs * scale
    y0, x0 = fy.astype(int), fx.astype(int)
    ty, tx = fy - y0, fx - x0
    ty, tx = ty * ty * (3 - 2 * ty), tx * tx * (3 - 2 * tx)
    a, b = grid[y0, x0], grid[y0, x0 + 1]
    c, d = grid[y0 + 1, x0], grid[y0 + 1, x0 + 1]
    return a * (1 - tx) * (1 - ty) + b * tx * (1 - ty) + c * (1 - tx) * ty + d * tx * ty


def main():
    src = Image.open(SOURCE).convert("RGBA")
    h = max(32, W * src.height // src.width)
    src = src.resize((W, h), Image.BILINEAR)
    px = np.asarray(src).astype(np.float32) / 255.0
    solid = px[..., 3] > 0.3
    lum = px[..., :3].mean(axis=2)

    # Silhouette edge: solid and empty texels within 2 px of each other
    padded = np.pad(solid, 2, constant_values=False)
    windows = np.stack([padded[dy:dy + h, dx:dx + W] for dy in range(5) for dx in range(5)])
    edge = windows.any(axis=0) & ~windows.all(axis=0)

    alpha = np.where(edge, 0.95, 0.0)
    gx = np.zeros_like(lum)
    gy = np.zeros_like(lum)
    gx[:, 1:-1] = lum[:, 2:] - lum[:, :-2]
    gy[1:-1, :] = lum[2:, :] - lum[:-2, :]
    inner = (alpha == 0) & solid & (gx * gx + gy * gy > 0.035)
    alpha = np.where(inner, 0.55, alpha)

    # Chalk on a rough wall: uneven pressure and the odd skipped grain
    alpha = alpha * (0.65 + 0.35 * noise(h, W, 0.09, 935))
    skip = np.random.default_rng(935).random((h, W)) < 0.14
    alpha = np.where(skip & (alpha > 0), alpha * 0.25, alpha)

    out = np.zeros((h, W, 4), dtype=np.uint8)
    out[..., 0], out[..., 1], out[..., 2] = 238, 242, 255
    out[..., 3] = (np.clip(alpha, 0, 1) * 255).astype(np.uint8)
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    Image.fromarray(out, "RGBA").save(OUT)
    print(OUT)


if __name__ == "__main__":
    main()
