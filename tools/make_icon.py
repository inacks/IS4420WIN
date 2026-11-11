# Draws the program icon: "4-20" in white on an INACKS green rounded square, with a teal
# bar under the text. Each size is drawn for its own pixel grid:
#   16 px       hand-drawn 3x7 digits (text from a font is not readable at this size)
#   24, 32 px   the font drawn straight at that size, so the strokes stay sharp
#   48 px up    drawn 4 times larger and reduced
# Usage: python make_icon.py IS4420WIN.ico preview.png [InterDisplay-Black.otf]
# Needs Pillow and the Inter Display Black font (https://rsms.me/inter/, SIL Open Font License).
import sys
from PIL import Image, ImageDraw, ImageFont

FONT = sys.argv[3] if len(sys.argv) > 3 else "/usr/share/fonts/opentype/inter/InterDisplay-Black.otf"
GREEN = (36, 95, 84)      # INACKS dark green (from the logo)
TEAL = (52, 153, 134)     # INACKS teal (from the logo)
WHITE = (255, 255, 255)
TEXT = "4-20"

GLYPHS_16 = {
    "4": ["X.X", "X.X", "X.X", "XXX", "..X", "..X", "..X"],
    "-": ["..", "..", "..", "XX", "..", "..", ".."],
    "2": ["XXX", "..X", "..X", "XXX", "X..", "X..", "XXX"],
    "0": ["XXX", "X.X", "X.X", "X.X", "X.X", "X.X", "XXX"],
}


def background(size):
    s = 4
    w = size * s
    img = Image.new("RGBA", (w, w), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    m = w * 0.04
    d.rounded_rectangle([m, m, w - m, w - m], radius=w * 0.18, fill=GREEN + (255,))
    return img.resize((size, size), Image.LANCZOS)


def fit_font(d, room, start):
    fs = start
    while d.textbbox((0, 0), TEXT, font=ImageFont.truetype(FONT, fs + 1))[2] <= room:
        fs += 1
    return ImageFont.truetype(FONT, fs)


def large(size):
    s = 4
    w = size * s
    img = background(size).resize((w, w), Image.LANCZOS)
    d = ImageDraw.Draw(img)
    f = fit_font(d, w * 0.74, 10)
    b = d.textbbox((0, 0), TEXT, font=f)
    tw, th = b[2] - b[0], b[3] - b[1]
    bar = max(s, w * 0.055)
    gap = w * 0.07
    y0 = (w - (th + gap + bar)) / 2
    x0 = (w - tw) / 2
    d.text((x0 - b[0], y0 - b[1]), TEXT, font=f, fill=WHITE + (255,))
    bw = tw * 0.62
    d.rounded_rectangle([(w - bw) / 2, y0 + th + gap, (w + bw) / 2, y0 + th + gap + bar],
                        radius=bar / 2, fill=TEAL + (255,))
    return img.resize((size, size), Image.LANCZOS)


def medium(size):
    img = background(size)
    d = ImageDraw.Draw(img)
    f = fit_font(d, size * 0.80, 4)
    b = d.textbbox((0, 0), TEXT, font=f)
    tw, th = b[2] - b[0], b[3] - b[1]
    bar = max(1, round(size * 0.07))
    gap = max(2, round(size * 0.09))
    y0 = round((size - (th + gap + bar)) / 2)
    x0 = round((size - tw) / 2)
    d.text((x0 - b[0], y0 - b[1]), TEXT, font=f, fill=WHITE + (255,))
    bw = round(tw * 0.55)
    bx = (size - bw) // 2
    d.rectangle([bx, y0 + th + gap, bx + bw - 1, y0 + th + gap + bar - 1], fill=TEAL + (255,))
    return img


def small16():
    img = background(16)
    px = img.load()
    x = 1
    for ch in TEXT:
        g = GLYPHS_16[ch]
        for row, line in enumerate(g):
            for col, c in enumerate(line):
                if c == "X":
                    px[x + col, 4 + row] = WHITE + (255,)
        x += len(g[0]) + 1
    for xx in range(5, 11):
        px[xx, 13] = TEAL + (255,)
    return img


def icon(size):
    if size == 16:
        return small16()
    if size <= 32:
        return medium(size)
    return large(size)


sizes = [16, 24, 32, 48, 64, 128, 256]
imgs = [icon(n) for n in sizes]
imgs[-1].save(sys.argv[1], format="ICO", sizes=[(n, n) for n in sizes], append_images=imgs[:-1])
imgs[-1].save(sys.argv[2])
print("icon written")
