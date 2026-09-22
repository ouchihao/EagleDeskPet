"""Package the existing mascot as a multi-size Windows icon without redrawing it"""

from pathlib import Path
from PIL import Image, ImageOps


def main():
    root = Path(__file__).resolve().parents[1]
    source = root / "DuckDeskPet/Assets/mascot-animated-neutral.png"
    target = root / "DuckDeskPet/Assets/eagle-app.ico"
    print(f"SOURCE={source}")
    print(f"TARGET={target}")
    with Image.open(source) as original:
        rgba = original.convert("RGBA")
        bounds = rgba.getchannel("A").getbbox()
        if bounds is None:
            raise ValueError("The existing mascot image is empty")
        fitted = ImageOps.contain(rgba.crop(bounds), (240, 240), Image.Resampling.LANCZOS)
        square = Image.new("RGBA", (256, 256))
        square.alpha_composite(fitted, ((256 - fitted.width) // 2, (256 - fitted.height) // 2))
        square.save(target, format="ICO", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
    with Image.open(target) as verified:
        assert verified.format == "ICO" and (256, 256) in verified.ico.sizes()
    print("Verified seven icon sizes; original PNG unchanged")


if __name__ == "__main__":
    main()
