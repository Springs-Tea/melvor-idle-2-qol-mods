"""Draws the banner and the section headers of the README, as SVG, into docs/img/art.

They are original drawings in the visual language of the game's interface (dark notched panels, titles on
torn strips of paper, warm gold accents), not copies of its textures. Run it again after changing a title:

    python tools/make-art.py
"""
import os
import random

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'docs', 'img', 'art')
FONT = "'Noto Sans','Segoe UI',Helvetica,Arial,sans-serif"

# The palette of the game's panels and labels.
PANEL, PANEL_EDGE, PANEL_DEEP = '#1d1916', '#524943', '#111110'
PAPER, PAPER_SHADE, INK = '#e9dfc1', '#b3a886', '#111110'
GOLD, TEXT, MUTED = '#ffd16d', '#ffffff', '#b5b5b5'
ACCENTS = ['#2baae1', '#378c47', '#cd964f', '#0fd9b7', '#cd3600', '#8e8e8e']


def notched(x, y, w, h, cut):
    """A rectangle with its corners cut off, like the game's heavy panels."""
    pts = [(x + cut, y), (x + w - cut, y), (x + w, y + cut), (x + w, y + h - cut),
           (x + w - cut, y + h), (x + cut, y + h), (x, y + h - cut), (x, y + cut)]
    return ' '.join('%.1f,%.1f' % p for p in pts)


def paper(cx, cy, w, h, seed):
    """A strip of paper with torn ends: the same strip for the same seed."""
    rng = random.Random(seed)
    x0, x1, y0, y1 = cx - w / 2, cx + w / 2, cy - h / 2, cy + h / 2
    steps = 5
    left = [(x0 + rng.uniform(0, 7), y1 - (y1 - y0) * i / steps) for i in range(steps + 1)]
    right = [(x1 - rng.uniform(0, 7), y0 + (y1 - y0) * i / steps) for i in range(steps + 1)]
    top = [(x0 + (x1 - x0) * i / 8, y0 + rng.uniform(-1.2, 1.2)) for i in range(1, 8)]
    bottom = [(x1 - (x1 - x0) * i / 8, y1 + rng.uniform(-1.2, 1.2)) for i in range(1, 8)]
    pts = [left[-1]] + top + right + bottom + left[:-1]
    return ' '.join('%.1f,%.1f' % p for p in pts)


def tag(cx, cy, text, size, seed, pad=26):
    """A title on paper, with the darker edge the strip throws under itself."""
    w = len(text) * size * 0.56 + pad * 2
    h = size * 1.62
    return (f'<polygon points="{paper(cx, cy + 3, w, h, seed)}" fill="{PAPER_SHADE}"/>'
            f'<polygon points="{paper(cx, cy, w, h, seed)}" fill="{PAPER}"/>'
            f'<text x="{cx}" y="{cy + size * 0.35:.1f}" font-family="{FONT}" font-size="{size}" font-weight="700" '
            f'fill="{INK}" text-anchor="middle">{text}</text>'), w


def svg(width, height, body, label):
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {width} {height}" width="{width}" height="{height}" '
            f'role="img" aria-label="{label}">{body}</svg>\n')


def banner():
    w, h = 1200, 300
    names = ['Combat Simulator', 'Item Sources', 'Recipe Rates', 'Combat Goals', 'Kills in Combat', 'Dungeon Completions']
    body = [
        # the panel: an outer frame, a lighter rim, the dark face
        f'<polygon points="{notched(6, 10, w - 12, h - 16, 22)}" fill="#0c0a09"/>',
        f'<polygon points="{notched(4, 4, w - 8, h - 14, 22)}" fill="{PANEL_EDGE}"/>',
        f'<polygon points="{notched(8, 8, w - 16, h - 22, 20)}" fill="{PANEL}"/>',
        f'<polygon points="{notched(18, 18, w - 36, h - 42, 14)}" fill="none" stroke="#2c2622" stroke-width="2"/>',
        # corner rivets
        ''.join(f'<circle cx="{x}" cy="{y}" r="4" fill="#3a332e"/><circle cx="{x - 1}" cy="{y - 1}" r="1.6" fill="#6b6058"/>'
                for x in (36, w - 36) for y in (38, h - 46)),
    ]
    title, _ = tag(w / 2, 84, 'Melvor Idle 2 Mods', 46, seed=7, pad=40)
    body.append(title)
    body.append(f'<text x="{w / 2}" y="158" font-family="{FONT}" font-size="21" fill="{MUTED}" text-anchor="middle">'
                'Six quality-of-life mods in the game\'s own look. Separate downloads. Your save is never touched.</text>')
    # the six mods as small dark tags with a coloured bar, like the sections of the game's footer
    size, gap, pad = 17, 14, 16
    widths = [len(n) * size * 0.58 + pad * 2 + 10 for n in names]
    x = (w - (sum(widths) + gap * (len(names) - 1))) / 2
    for name, width, colour in zip(names, widths, ACCENTS):
        body.append(f'<rect x="{x:.1f}" y="194" width="{width:.1f}" height="40" rx="3" fill="#141310" stroke="#6f685c" stroke-width="1"/>'
                    f'<rect x="{x + 1:.1f}" y="195" width="5" height="38" fill="{colour}"/>'
                    f'<text x="{x + width / 2 + 3:.1f}" y="220" font-family="{FONT}" font-size="{size}" font-weight="600" '
                    f'fill="{PAPER}" text-anchor="middle">{name}</text>')
        x += width + gap
    return svg(w, h, ''.join(body), 'Melvor Idle 2 Mods: six quality-of-life mods in the game\'s own look')


def header(text, seed):
    """A section title: a strip of paper on a thin line, the way the game names its columns."""
    w, h = 900, 64
    title, tw = tag(w / 2, 30, text, 26, seed)
    line = (f'<rect x="60" y="29" width="{(w - tw) / 2 - 84:.1f}" height="2" fill="{PANEL_EDGE}"/>'
            f'<rect x="{(w + tw) / 2 + 24:.1f}" y="29" width="{(w - tw) / 2 - 84:.1f}" height="2" fill="{PANEL_EDGE}"/>'
            f'<rect x="52" y="26" width="8" height="8" fill="{GOLD}" transform="rotate(45 56 30)"/>'
            f'<rect x="{w - 60}" y="26" width="8" height="8" fill="{GOLD}" transform="rotate(45 {w - 56} 30)"/>')
    return svg(w, h, line + title, text)


HEADERS = {
    'the-mods': 'The mods',
    'install': 'Install',
    'questions': 'Questions',
    'how-they-are-built': 'How they are built',
    'building': 'Building from source',
    'credits': 'Credits and license',
    # the title of each mod's own page
    'combat-simulator': 'Combat Simulator',
    'item-sources': 'Item Sources',
    'recipe-rates': 'Recipe Rates',
    'combat-goals': 'Combat Goals',
    'kills-in-combat': 'Kills in Combat',
    'dungeon-completions': 'Dungeon Completions',
}

if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    files = {'banner.svg': banner()}
    for i, (name, text) in enumerate(HEADERS.items()):
        files[f'h-{name}.svg'] = header(text, seed=11 + i)
    for name, content in files.items():
        with open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n') as f:
            f.write(content)
        print(name, len(content), 'bytes')
