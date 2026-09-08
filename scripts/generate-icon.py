"""Generate the deterministic DryCut Windows icon."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets" / "DryCut.ico"
SIZES = [16, 24, 32, 48, 64, 128, 256]


def render(size: int) -> Image.Image:
    scale = size / 256
    image = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    margin = round(14 * scale)
    radius = round(54 * scale)
    draw.rounded_rectangle(
        (margin, margin, size - margin - 1, size - margin - 1),
        radius=radius,
        fill=(37, 99, 235, 255),
    )
    # White foreground silhouette: a clean leaf/person hybrid.
    draw.ellipse((round(83*scale), round(48*scale), round(173*scale), round(138*scale)), fill="white")
    draw.rounded_rectangle(
        (round(62*scale), round(119*scale), round(194*scale), round(211*scale)),
        radius=round(50*scale),
        fill="white",
    )
    # A small diagonal cut communicates separating foreground from background.
    width = max(2, round(13 * scale))
    draw.line((round(53*scale), round(202*scale), round(203*scale), round(52*scale)), fill=(147, 197, 253, 255), width=width)
    return image


OUT.parent.mkdir(parents=True, exist_ok=True)
images = [render(size) for size in SIZES]
images[-1].save(OUT, format="ICO", sizes=[(size, size) for size in SIZES], append_images=images[:-1])
print(OUT)
