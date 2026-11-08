"""Builds docs/screenshots/hero.png and framed versions of the screenshots rendered by the
'Update README screenshots' workflow.  Usage:  python3 docs/tools/compose_hero.py   (needs Pillow)"""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

SHOTS = Path(__file__).resolve().parents[1] / "screenshots"


def rounded(img, radius):
    mask = Image.new("L", img.size, 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, img.width - 1, img.height - 1], radius=radius, fill=255)
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    out.paste(img.convert("RGBA"), (0, 0), mask)
    ImageDraw.Draw(out).rounded_rectangle([0, 0, img.width - 1, img.height - 1], radius=radius, outline=(58, 64, 78, 255), width=2)
    return out


def with_shadow(img, pad=48, blur=26, opacity=150):
    canvas = Image.new("RGBA", (img.width + pad * 2, img.height + pad * 2), (0, 0, 0, 0))
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    alpha = img.split()[3].point(lambda a: opacity if a > 0 else 0)
    shadow.paste((0, 0, 0, 255), (pad, pad + 14), alpha)
    shadow = shadow.filter(ImageFilter.GaussianBlur(blur))
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(img, (pad, pad))
    return canvas


def gradient(size, top=(17, 20, 32), bottom=(46, 24, 78)):
    w, h = size
    g = Image.new("RGBA", size)
    d = ImageDraw.Draw(g)
    for y in range(h):
        t = y / max(1, h - 1)
        d.line([(0, y), (w, y)], fill=tuple(int(a + (b - a) * t) for a, b in zip(top, bottom)) + (255,))
    # soft colour glows behind the two windows
    glow = Image.new("RGBA", size, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([w * 0.05, h * 0.15, w * 0.50, h * 0.85], fill=(79, 140, 255, 70))
    gd.ellipse([w * 0.50, h * 0.15, w * 0.95, h * 0.85], fill=(166, 92, 255, 70))
    g.alpha_composite(glow.filter(ImageFilter.GaussianBlur(120)))
    return g


def framed(name, radius=28):
    img = Image.open(SHOTS / f"{name}.png")
    return with_shadow(rounded(img, radius))


def frame_raw(name, radius=28):
    """docs/screenshots/raw/<name>.png (2x render) -> framed docs/screenshots/<name>.png at 1x."""
    src = SHOTS / "raw" / f"{name}.png"
    if not src.exists():
        return None
    f = with_shadow(rounded(Image.open(src), radius))
    f = f.resize((f.width // 2, f.height // 2), Image.LANCZOS)
    f.save(SHOTS / f"{name}.png", optimize=True)
    return f


REQUIRED = ["main-microphone", "main-application", "application-picker", "setup-help",
            "raw/soundboard-expanded", "raw/soundboard-collapsed", "raw/soundboard-drag-drop"]


def verify_renders():
    """Fail loudly instead of publishing missing or blank screenshots."""
    problems = []
    for name in REQUIRED:
        path = SHOTS / f"{name}.png"
        if not path.exists():
            problems.append(f"{name}.png is missing")
            continue
        img = Image.open(path).convert("RGBA")
        alpha_min, alpha_max = img.getchannel("A").getextrema()
        lum_min, lum_max = img.convert("L").getextrema()
        if alpha_max == 0 or lum_max - lum_min < 40:
            problems.append(f"{name}.png looks blank (alpha max {alpha_max}, luminance range {lum_min}-{lum_max})")
    if problems:
        raise SystemExit("Screenshot verification failed:\n  " + "\n  ".join(problems))


def main():
    verify_renders()
    for name in ("soundboard-expanded", "soundboard-collapsed", "soundboard-drag-drop"):
        frame_raw(name, 20 if name == "soundboard-drag-drop" else 28)

    # Hero: application mode next to microphone mode with the soundboard open.
    app = framed("main-application")
    sb_raw = SHOTS / "raw" / "soundboard-expanded.png"
    mic = with_shadow(rounded(Image.open(sb_raw), 28)) if sb_raw.exists() else framed("main-microphone")
    gap = -30
    w = app.width + mic.width + gap + 80
    h = max(mic.height, app.height) + 80
    hero = gradient((w, h))
    hero.alpha_composite(app, (40, 40))
    hero.alpha_composite(mic, (40 + app.width + gap, 40))
    hero = hero.resize((w // 2, h // 2), Image.LANCZOS)  # 1x for the README, still sharp on hi-dpi screens
    hero.convert("RGB").save(SHOTS / "hero.png", optimize=True)

    for name in ("application-picker", "setup-help"):
        if (SHOTS / f"{name}.png").exists():
            f = framed(name, 24)
            f = f.resize((f.width // 2, f.height // 2), Image.LANCZOS)
            f.save(SHOTS / f"{name}-framed.png", optimize=True)
    print("ok")


if __name__ == "__main__":
    main()
