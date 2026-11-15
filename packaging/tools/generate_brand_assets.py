from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[2]
assets = root / "src" / "MemoDesk.App" / "Assets"
image = Image.new("RGBA", (1024, 1024))
draw = ImageDraw.Draw(image)
def box(values):
    return tuple(round(value * 4) for value in values)
draw.rounded_rectangle(box((8, 8, 248, 248)), radius=232, fill="#337DE1")
draw.rounded_rectangle(box((53, 40, 203, 216)), radius=108, fill="white")
for start, end, y in ((83, 174, 91), (83, 146, 121)):
    draw.line(box((start, y, end, y)), fill="#9DBCE6", width=40)
    for x in (start, end):
        draw.ellipse(box((x-5, y-5, x+5, y+5)), fill="#9DBCE6")
draw.ellipse(box((126, 133, 200, 207)), fill="#1B3559")
draw.line([box((145, 170)), box((157, 182)), box((181, 155))], fill="white", width=40, joint="curve")
for x, y in ((145, 170), (181, 155)):
    draw.ellipse(box((x-5, y-5, x+5, y+5)), fill="white")
image = image.resize((256, 256), Image.Resampling.LANCZOS)
assets.mkdir(parents=True, exist_ok=True)
image.save(assets / "memo.png")
image.save(assets / "memo.ico", sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
for name, size in (("Square44x44Logo.png",44), ("Square150x150Logo.png",150), ("StoreLogo.png",50)):
    image.resize((size,size), Image.Resampling.LANCZOS).save(root / "packaging" / "Assets" / name)

icons = {
 "general": ("#667B9B", '<path d="M12 4.4v1.7m0 11.8v1.7M4.4 12h1.7m11.8 0h1.7M6.6 6.6l1.2 1.2m8.4 8.4 1.2 1.2m0-10.8-1.2 1.2m-8.4 8.4-1.2 1.2"/><circle cx="12" cy="12" r="4"/>'),
 "pages": ("#6975B7", '<rect x="5" y="5" width="6" height="6" rx="1"/><rect x="13" y="5" width="6" height="6" rx="1"/><rect x="5" y="13" width="6" height="6" rx="1"/><rect x="13" y="13" width="6" height="6" rx="1"/>'),
 "music": ("#D06C89", '<path d="M10 16V6l7-1v9M10 15c-4-1-5 4-2 4 1.4 0 2-.7 2-2m7-4c-4-1-5 4-2 4 1.4 0 2-.7 2-2"/>'),
 "focus": ("#E28562", '<circle cx="12" cy="13" r="6"/><path d="M12 9v4l2.5 1.5M10 4h4"/>'),
 "tasks": ("#62A99C", '<path d="M6 7h12M6 12h12M6 17h8"/><path d="m15 17 1.7 1.7 3-3.3"/>'),
 "notifications": ("#D69C55", '<path d="M7 16h10l-1.3-2V10a3.7 3.7 0 0 0-7.4 0v4L7 16Zm3.5 2h3"/>'),
 "quota": ("#7B9F7B", '<circle cx="12" cy="12" r="6.5"/><path d="M12 7v5l3 2"/>'),
 "weather": ("#5A9CC1", '<circle cx="9" cy="9" r="3"/><path d="M8 17h8a3 3 0 0 0-.4-6A4 4 0 0 0 8 12.5 2.3 2.3 0 0 0 8 17Z"/>'),
 "memo": ("#4287D5", '<rect x="6" y="4" width="12" height="16" rx="2"/><path d="M9 9h6M9 13h6M9 17h3"/>'),
 "vault": ("#9274BC", '<rect x="6" y="10" width="12" height="9" rx="2"/><path d="M9 10V8a3 3 0 0 1 6 0v2m-3 3v3"/>'),
 "worklogs": ("#CC9A58", '<path d="M5 8h14v10H5zM9 8V6h6v2M5 12h14"/>'),
 "reminders": ("#D57575", '<circle cx="12" cy="12" r="7"/><path d="M12 8v4l3 2"/>'),
 "settings": ("#7C8EAB", '<path d="M5 7h14M5 12h14M5 17h14"/><circle cx="9" cy="7" r="2" fill="#7C8EAB"/><circle cx="15" cy="12" r="2" fill="#7C8EAB"/><circle cx="10" cy="17" r="2" fill="#7C8EAB"/>')
}
directory = root / "integrations" / "daozai" / "icons"
directory.mkdir(parents=True, exist_ok=True)
for name, (color, lines) in icons.items():
    svg = (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24">'
           f'<rect x="1" y="1" width="22" height="22" rx="6" fill="{color}"/>'
           f'<g fill="none" stroke="#fff" stroke-width="1.55" stroke-linecap="round" stroke-linejoin="round">{lines}</g></svg>')
    (directory / f"{name}.svg").write_text(svg, encoding="utf-8")
