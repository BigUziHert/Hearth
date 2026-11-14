"""Recreate Hearth's geometric app mark (optional authoring tool: Pillow)."""
from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parent.parent / "src" / "Hearth.App" / "Assets"
root.mkdir(parents=True, exist_ok=True)
image = Image.new("RGBA", (256, 256), (0, 0, 0, 0))
draw = ImageDraw.Draw(image)
draw.rounded_rectangle((0, 0, 255, 255), radius=54, fill="#1B3029")
draw.polygon([(128, 39), (217, 128), (128, 217), (39, 128)], fill="#F0EBC8")
draw.polygon([(128, 64), (192, 128), (128, 192), (64, 128)], fill="#1B3029")
draw.polygon([(128, 87), (169, 128), (128, 169), (87, 128)], fill="#D7B16B")
image.save(root / "Hearth.ico", sizes=[(16,16), (24,24), (32,32), (48,48), (64,64), (128,128), (256,256)])
