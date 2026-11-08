"""Frames the screenshots rendered by the 'Screenshots' workflow (VoxShift.exe --render-screenshots) and builds
docs/screenshots/hero.png.  Usage:  python3 docs/tools/compose_hero.py   (needs Pillow)

Only presentation is added here (rounded corners, a drop shadow, a background gradient for the hero and a 1x
downscale of the 2x renders). The UI itself is never edited: every pixel of the windows comes from the real WPF UI."""
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
    glow = Image.new("RGBA", size, (0, 0, 0, 0))
    gd = ImageDraw.Draw(glow)
    gd.ellipse([w * 0.05, h * 0.15, w * 0.55, h * 0.85], fill=(79, 140, 255, 70))
    gd.ellipse([w * 0.45, h * 0.15, w * 0.95, h * 0.85], fill=(34, 193, 168, 60))
    g.alpha_composite(glow.filter(ImageFilter.GaussianBlur(120)))
    return g


def frame_raw(name, radius=28):
    """docs/screenshots/raw/<name>.png (2x render) -> framed docs/screenshots/<name>.png at 1x."""
    f = with_shadow(rounded(Image.open(SHOTS / "raw" / f"{name}.png"), radius))
    f = f.resize((f.width // 2, f.height // 2), Image.LANCZOS)
    f.save(SHOTS / f"{name}.png", optimize=True)
    return f


FRAMED = ["v005-main-ui", "v005-soundboard-docked-expanded", "v005-soundboard-docked-collapsed",
          "v005-vr-controller-mode", "v005-audio-mixing", "soundboard-drag-drop"]
REQUIRED = ["main-microphone", "main-application", "application-picker", "setup-help"] + [f"raw/{n}" for n in FRAMED]
OUTPUTS = FRAMED + ["hero", "application-picker-framed", "setup-help-framed"]


def check_png(path):
    """Fail loudly instead of publishing missing, corrupt or blank screenshots."""
    if not path.exists():
        return f"{path.relative_to(SHOTS)} is missing"
    try:
        with Image.open(path) as probe:
            probe.verify()  # PNG integrity (CRCs, structure)
    except Exception as ex:  # noqa: BLE001 - any decoder error means a broken file
        return f"{path.relative_to(SHOTS)} is corrupt: {ex}"
    img = Image.open(path).convert("RGBA")
    alpha_min, alpha_max = img.getchannel("A").getextrema()
    lum_min, lum_max = img.convert("L").getextrema()
    if img.width < 100 or img.height < 100 or alpha_max == 0 or lum_max - lum_min < 40:
        return f"{path.relative_to(SHOTS)} looks blank ({img.width}x{img.height}, alpha max {alpha_max}, luminance {lum_min}-{lum_max})"
    return None


def verify(names):
    problems = [p for p in (check_png(SHOTS / f"{n}.png") for n in names) if p]
    if problems:
        raise SystemExit("Screenshot verification failed:\n  " + "\n  ".join(problems))


def main():
    verify(REQUIRED)
    for name in FRAMED:
        frame_raw(name, 20 if name in ("soundboard-drag-drop", "v005-audio-mixing") else 28)

    # Hero: the main window with the soundboard docked on its right, as the user sees it.
    pair = with_shadow(rounded(Image.open(SHOTS / "raw" / "v005-soundboard-docked-expanded.png"), 28))
    hero = gradient((pair.width + 80, pair.height + 80))
    hero.alpha_composite(pair, (40, 40))
    hero = hero.resize((hero.width // 2, hero.height // 2), Image.LANCZOS)
    hero.convert("RGB").save(SHOTS / "hero.png", optimize=True)

    for name in ("application-picker", "setup-help"):
        f = with_shadow(rounded(Image.open(SHOTS / f"{name}.png"), 24))
        f = f.resize((f.width // 2, f.height // 2), Image.LANCZOS)
        f.save(SHOTS / f"{name}-framed.png", optimize=True)

    verify(OUTPUTS)
    print("ok")


if __name__ == "__main__":
    main()
