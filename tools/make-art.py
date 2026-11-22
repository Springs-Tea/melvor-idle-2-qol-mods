"""Draws the pictures of the readmes, as SVG, into docs/img/art: the banner, the titles, the buttons, the tags.

They are drawings of the game's interface, not copies of its textures: every shape here is a polygon placed
by this script, with the proportions and the colours measured on screenshots of the Combat Simulator's
window (the heavy frame, the dark columns with their notches, the titles on torn paper, the red X, the
green and red buttons, the stat lines). Nothing is random at run time: the torn edges come from a fixed
seed, so the same script always writes the same files, and the repository's checks compare them.

Run it again after changing a title or a version:

    python tools/make-art.py
"""
import os
import random
import zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'docs', 'img', 'art')

# What the tags under the banner say. Change them here when a release needs it, then run the script.
GAME_VERSION = '0.11.6-alpha.1'
LOADER_VERSION = '0.1.3+'

# The game's font is close to Noto Sans; the others are what a visitor's computer falls back to.
FONT = "'Noto Sans','Open Sans','Segoe UI',Roboto,'Helvetica Neue',Arial,sans-serif"

# Colours, sampled from the game's screens.
FRAME_TOP, FRAME_BOTTOM, FRAME_RIM, FRAME_BASE, FRAME_LIP = '#3c3a32', '#2b2a25', '#4a463e', '#171714', '#24231f'
COLUMN_TOP, COLUMN_BOTTOM = '#201f1d', '#161514'
PAPER, PAPER_FACET, PAPER_UNDER, PAPER_PALE, PAPER_SPECK, INK = '#e9dfc1', '#e2d4b2', '#a39a83', '#f1e9cc', '#dccfa6', '#111110'
WHITE, GREY, GOLD, GOOD = '#ffffff', '#b5b5b5', '#edbf59', '#5fd35f'
LINE, STAT, SCROLL = '#565656', '#151514', '#6e635c'
ROW, ROW_EDGE, ROW_ON, ROW_ON_EDGE = '#252422', '#44433f', '#1b3b19', '#3b8637'
BUTTONS = {   # face, darker facet, top edge from, top edge to, bottom band, its share of the height, text
    'green': ('#016101', '#015501', '#016101', '#028702', '#015801', 0.06, '#e8e8e8'),
    'red':   ('#670101', '#5a0101', '#6a0101', '#8c0302', '#540101', 0.14, '#e8e3e3'),
    'dark':  ('#2e2c28', '#292723', '#3a3631', '#5f564c', '#262421', 0.1, '#e2dbcd'),
}

# Advance widths per 1000 units of font size, (regular, bold), of a humanist sans. Text is written with
# textLength, so a title takes this width in whatever font the visitor has and the art around it still fits.
WIDTHS = {
    ' ': (274, 276), '!': (284, 327), '"': (392, 493), '#': (591, 592), '%': (818, 867), '&': (800, 850),
    "'": (230, 293), '(': (302, 369), ')': (302, 369), '+': (684, 707), ',': (217, 271), '-': (400, 404),
    '.': (217, 271), '/': (390, 443), ':': (217, 271), ';': (217, 271), '?': (448, 438), '·': (217, 271),
    'A': (645, 703), 'B': (573, 641), 'C': (619, 624), 'D': (701, 737), 'E': (506, 532), 'F': (488, 520),
    'G': (686, 711), 'H': (710, 766), 'I': (266, 317), 'J': (357, 445), 'K': (580, 649), 'L': (471, 511),
    'M': (898, 957), 'N': (748, 790), 'O': (754, 758), 'P': (560, 614), 'Q': (754, 758), 'R': (598, 653),
    'S': (531, 561), 'T': (524, 586), 'U': (687, 723), 'V': (621, 667), 'W': (934, 1005), 'X': (590, 655),
    'Y': (553, 607), 'Z': (570, 607),
    'a': (509, 538), 'b': (588, 620), 'c': (462, 480), 'd': (589, 619), 'e': (523, 541), 'f': (313, 383),
    'g': (589, 619), 'h': (566, 602), 'i': (242, 284), 'j': (242, 284), 'k': (497, 559), 'l': (242, 284),
    'm': (861, 916), 'n': (566, 605), 'o': (586, 611), 'p': (588, 620), 'q': (589, 619), 'r': (348, 398),
    's': (424, 440), 't': (339, 389), 'u': (566, 605), 'v': (479, 542), 'w': (723, 797), 'x': (459, 552),
    'y': (484, 538), 'z': (452, 479),
}
DIGIT = (539, 575)
WIDER = 1.015   # a little air: the game's font runs wider than the table's


def n(value):
    """A number as SVG wants it: one decimal, no trailing zero."""
    text = '%.1f' % value
    return text[:-2] if text.endswith('.0') else text


def points(pts):
    return ' '.join('%s,%s' % (n(x), n(y)) for x, y in pts)


def polygon(pts, fill, extra=''):
    return f'<polygon points="{points(pts)}" fill="{fill}"{extra}/>'


def rect(x, y, w, h, fill, extra=''):
    return f'<rect x="{n(x)}" y="{n(y)}" width="{n(w)}" height="{n(h)}" fill="{fill}"{extra}/>'


def seed_of(name):
    return zlib.crc32(name.encode('utf-8'))


def text_width(string, size, weight=400):
    column = 0 if weight < 550 else 1
    units = sum((DIGIT if c.isdigit() else WIDTHS[c])[column] for c in string)
    return units * size / 1000 * WIDER


def text(x, y, string, size, fill, weight=400, anchor='start'):
    escaped = string.replace('&', '&amp;').replace('<', '&lt;')
    return (f'<text x="{n(x)}" y="{n(y)}" font-family="{FONT}" font-size="{n(size)}" font-weight="{weight}" fill="{fill}" '
            f'text-anchor="{anchor}" textLength="{n(text_width(string, size, weight))}">{escaped}</text>')


class Art:
    """One picture: its shapes, and the gradients and clips they refer to."""

    def __init__(self, width, height, label):
        self.width, self.height, self.label = width, height, label
        self.defs, self.body, self.count = [], [], 0

    def add(self, *shapes):
        self.body.extend(shapes)

    def gradient(self, stops, x1, y1, x2, y2):
        self.count += 1
        inner = ''.join(f'<stop offset="{n(at)}" stop-color="{colour}"/>' for at, colour in stops)
        self.defs.append(f'<linearGradient id="g{self.count}" gradientUnits="userSpaceOnUse" x1="{n(x1)}" y1="{n(y1)}" '
                         f'x2="{n(x2)}" y2="{n(y2)}">{inner}</linearGradient>')
        return f'url(#g{self.count})'

    def clip(self, pts):
        self.count += 1
        self.defs.append(f'<clipPath id="c{self.count}"><polygon points="{points(pts)}"/></clipPath>')
        return f' clip-path="url(#c{self.count})"'

    def render(self):
        label = self.label.replace('&', '&amp;').replace('"', '&quot;')
        defs = f'<defs>{"".join(self.defs)}</defs>' if self.defs else ''
        return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {self.width} {self.height}" width="{self.width}" '
                f'height="{self.height}" role="img" aria-label="{label}">{defs}{"".join(self.body)}</svg>\n')


# ---------------------------------------------------------------------------------------------------------
# The pieces of the game's windows
# ---------------------------------------------------------------------------------------------------------

def frame(art, x, y, w, h):
    """The heavy frame of the game's pop-up windows: a slab with a lit top rim and a dark base."""
    k = min(1.0, 0.5 + h / 800)                   # a short strip gets a thinner rim and smaller corners
    cw, ch, bw, bh, lip = 46 * k, 14 * k, 42 * k, 16 * k, 6 * k
    outline = [(x + cw, y), (x + w - cw, y), (x + w, y + ch), (x + w, y + h - bh),
               (x + w - bw, y + h), (x + bw, y + h), (x, y + h - bh), (x, y + ch)]
    clip = art.clip(outline)
    face = art.gradient([(0, FRAME_TOP), (1, FRAME_BOTTOM)], 0, y + ch, 0, y + h - bh)
    art.add(polygon(outline, face),
            f'<g{clip}>',
            # the large flat facets of the game's low-poly surfaces
            polygon([(x, y + ch), (x + w * 0.27, y + ch), (x + w * 0.19, y + ch + h * 0.34), (x, y + ch + h * 0.24)],
                    '#000000', ' opacity="0.1"'),
            polygon([(x + w, y + ch), (x + w, y + ch + h * 0.52), (x + w * 0.8, y + ch)], '#ffffff', ' opacity="0.035"'),
            polygon([(x + w * 0.42, y + h), (x + w * 0.74, y + h - bh - h * 0.2), (x + w, y + h - bh - h * 0.12), (x + w, y + h)],
                    '#000000', ' opacity="0.07"'),
            rect(x, y, w, ch, FRAME_RIM),
            rect(x, y + ch, w, 1.5, '#000000', ' opacity="0.16"'),
            rect(x, y + h - bh - lip, w, lip, FRAME_LIP),
            rect(x, y + h - bh, w, bh, FRAME_BASE),
            '</g>')


def column(art, x, y, w, h):
    """A dark column inside a window: cut corners, a notch in its top edge, splinters in its sides."""
    c = min(13, h * 0.3)
    near = min(w * 0.14, 56)                      # the notches sit near the corners
    deep = min(9.5, h * 0.24)
    pts = [(x, y + c), (x + c, y),
           (x + near - 2.5, y), (x + near, y + 3.5), (x + near + 2.5, y),
           (x + w - near - deep * 0.63, y), (x + w - near, y + deep), (x + w - near + deep * 0.63, y),
           (x + w - c, y), (x + w, y + c)]
    tall = h >= 150
    if tall:
        at = y + h * 0.78
        pts += [(x + w, at - 5), (x + w - 6, at), (x + w, at + 5)]
    pts += [(x + w, y + h - c), (x + w - c, y + h), (x + c + 2, y + h), (x, y + h - c - 2)]
    if tall:
        low, high = y + h - c - 16, y + h * 0.84
        pts += [(x, low + 5), (x + 4, low), (x, low - 5), (x, high + 4.5), (x + 7, high), (x, high - 4.5)]
    clip = art.clip(pts)
    face = art.gradient([(0, COLUMN_TOP), (1, COLUMN_BOTTOM)], 0, y, 0, y + h)
    art.add(polygon(pts, face),
            polygon([(x, y), (x + near + 6, y), (x + near - 10, y + min(h * 0.55, 96)), (x, y + min(h * 0.7, 130))],
                    '#000000', f' opacity="0.16"{clip}'))


def paper(art, cx, top, w, h, title, size, seed):
    """A title on a strip of torn paper: ragged lower edge, nicked sides, a darker shard at its right end."""
    rng = random.Random(seed)
    x0, x1, y0, y1 = cx - w / 2, cx + w / 2, top, top + h
    span = lambda low, high: low + (high - low) * rng.random()

    pts = [(x0 + span(0, 1.5), y0 + span(0, 1))]
    steps = max(3, int(w / 60))
    pts += [(x0 + w * i / steps, y0 + span(-0.3, 0.7)) for i in range(1, steps)]
    pts += [(x1 - 4.5, y0 + span(0, 0.6)), (x1 - 1.5, y0 + 2.5), (x1 - span(0, 0.8), y0 + 4)]           # chipped corner
    nick = y0 + h * span(0.34, 0.5)
    pts += [(x1 - span(0, 0.8), nick - 2), (x1 - 2.2, nick), (x1 - span(0, 0.8), nick + 1.6), (x1 - span(0.5, 1.5), y1 - 1.5)]
    # the lower edge, right to left: it wavers, and a few small tears reach up into the paper
    count = max(4, int(w / 22))
    tears = {int(count * span(0.08, 0.3)), int(count * span(0.4, 0.62)), int(count * span(0.7, 0.92))}
    lower = []
    for i in range(count + 1):
        px = x1 - 2 - (w - 4) * i / count
        py = y1 + span(-1.3, 0.6)
        if i in tears:
            pts += [(px + 1.4, py), (px + span(-0.4, 0.4), py - span(2.2, 3.4)), (px - 1.4, py)]
        else:
            pts.append((px, py))
        lower.append((px, py, 2.4 * max(0.0, 1 - i / count / 0.68)))      # how far the underside shows here
    nick = y0 + h * span(0.45, 0.62)
    pts += [(x0 + span(0.5, 1.6), y1 - 2), (x0 + span(0, 0.8), nick + 1.8), (x0 + 2.2, nick), (x0 + span(0, 0.8), nick - 2)]

    clip = art.clip(pts)
    under = [(px, py - 1.5) for px, py, _ in lower] + [(px, py + show) for px, py, show in reversed(lower)]
    shard = min(w * 0.3, h * 2.4)      # the darker end is about as long as two heights of the strip
    art.add(polygon(under, PAPER_UNDER),
            polygon(pts, PAPER),
            f'<g{clip}>',
            polygon([(x1, y0 + h * 0.2), (x1 - shard * 0.42, y0 + h * 0.3), (x1 - shard * 0.2, y0 + h * 0.47),
                     (x1 - shard * 0.62, y0 + h * 0.56), (x1 - shard * 1.25, y1), (x1, y1)], PAPER_FACET),
            polygon([(x1 - shard * 0.08, y0 + h * 0.42), (x1 - shard * 0.5, y0 + h * 0.33), (x1 - shard * 0.3, y0 + h * 0.52)], PAPER),
            polygon([(x0, y0 + h * 0.86), (x0 + w * 0.3, y0 + h * 0.8), (x0 + w * 0.42, y1), (x0, y1)], PAPER_PALE, ' opacity="0.55"'))
    for _ in range(max(4, int(w / 45))):                # the short flecks along the lower edge
        fx, fy, fw = x0 + w * span(0.04, 0.9), y1 - span(1.5, 4), span(4, 13)
        art.add(polygon([(fx, fy), (fx + fw, fy - span(0.2, 0.9)), (fx + fw - 1.5, fy + 1.1), (fx + 1, fy + 1)],
                        PAPER_SPECK if rng.random() < 0.6 else PAPER_PALE))
    art.add('</g>', text(cx, y0 + h / 2 + size * 0.355, title, size, INK, 700, 'middle'))


def paper_width(title, size, pad):
    return text_width(title, size, 700) + pad * 2


def close_button(art, x, y, s):
    """The red X in a window's corner."""
    face = [(x, y + 1), (x + s * 0.32, y), (x + s, y + 1.2), (x + s, y + s), (x, y + s)]
    clip = art.clip(face)
    rim = art.gradient([(0, '#640706'), (0.4, '#7c0c0a'), (1, '#5a2a22')], x, 0, x + s, 0)
    cx, cy, half_w, half_h, bar = x + s / 2, y + s / 2 + 0.5, s * 0.21, s * 0.235, s * 0.14
    art.add(rect(x, y + s - 1, s * 0.19, 3.2, '#070101'), rect(x + s * 0.23, y + s - 1, s * 0.77, 3.2, '#070101'),
            polygon(face, '#630101'),
            f'<g{clip}>',
            polygon([(x + s, y + s * 0.5), (x + s * 0.64, y + s), (x + s, y + s)], '#520101'),
            polygon([(x + s * 0.62, y + s * 0.55), (x + s * 0.74, y + s * 0.52), (x + s * 0.66, y + s * 0.78)], '#560101'),
            rect(x, y, s, 2.4, rim), rect(x, y + s - 2.2, s, 2.2, '#560101'),
            '</g>',
            polygon([(cx - half_w, cy - half_h), (cx - half_w + bar, cy - half_h), (cx + half_w, cy + half_h), (cx + half_w - bar, cy + half_h)], '#e8e3e3'),
            polygon([(cx + half_w, cy - half_h), (cx + half_w - bar, cy - half_h), (cx - half_w, cy + half_h), (cx - half_w + bar, cy + half_h)], '#e8e3e3'))


def button(art, x, y, w, h, label, kind='green', size=None):
    """One of the game's textured buttons: clipped corners, a nick in the right and the lower edge, a lit
    top edge, darker shards at the right end, a black shadow under it."""
    face, facet, rim_from, rim_to, band, depth, ink = BUTTONS[kind]
    size = size or h * 0.45
    tear = x + max(w * 0.2, 18)
    pts = [(x + 4, y), (x + w - 2, y + 0.4), (x + w, y + 2),
           (x + w, y + h * 0.41), (x + w - 3.6, y + h * 0.46), (x + w, y + h * 0.52),
           (x + w, y + h - 2), (x + w - 2.5, y + h),
           (tear + 2.2, y + h), (tear, y + h - 3.6), (tear - 2.2, y + h),
           (x + 5, y + h), (x, y + h - 4), (x, y + 5)]
    clip = art.clip(pts)
    rim = art.gradient([(0, rim_from), (0.3, rim_from), (1, rim_to)], x, 0, x + w, 0)
    back = lambda k: max(x + w * 0.4, x + w - h * k)      # the shards keep their size on a wide button
    art.add(f'<polygon points="{points(pts)}" fill="#020202" transform="translate(0 3)"/>',
            polygon(pts, face),
            f'<g{clip}>',
            polygon([(back(0.85), y), (x + w, y), (x + w, y + h * 0.44)], facet),
            polygon([(x + w, y + h * 0.56), (back(1.15), y + h * 0.7), (back(2.6), y + h), (x + w, y + h)], facet),
            rect(x, y, w, 2.6, rim), rect(x, y + h * (1 - depth), w, h * depth, band),
            '</g>',
            text(x + w / 2, y + h / 2 + size * 0.355, label, size, ink, 700, 'middle'))


def row(art, x, y, w, h, selected=False):
    """A line of the results list; the selected one is green."""
    fill, edge = (ROW_ON, ROW_ON_EDGE) if selected else (ROW, ROW_EDGE)
    art.add(rect(x + 0.75, y + 0.75, w - 1.5, h - 1.5, fill, f' rx="4" stroke="{edge}" stroke-width="1.5"'))


def stat(art, x, y, w, h, label, value, colour, size):
    """A line of the details: a name on the left, its value in bold on the right."""
    base = y + h / 2 + size * 0.355
    art.add(rect(x, y, w, h, STAT, ' rx="5"'),
            text(x + 10, base, label, size, WHITE),
            text(x + w - 10, base, value, size, colour, 700, 'end'))


def heading(art, x, y, w, title, size):
    """A heading inside a column: a short grey title between two thin lines."""
    gap = text_width(title, size) / 2 + 12
    cx = x + w / 2
    art.add(rect(x, y - 0.75, w / 2 - gap, 1.5, LINE), rect(cx + gap, y - 0.75, w / 2 - gap, 1.5, LINE),
            text(cx, y + size * 0.355, title, size, GREY, 400, 'middle'))


# ---------------------------------------------------------------------------------------------------------
# The pictures
# ---------------------------------------------------------------------------------------------------------

MODS = [   # the banner's list: name, what the mod adds to the screen
    ('Combat Simulator', 'window'),
    ('Item Sources', 'tooltip'),
    ('Item Encyclopedia', 'window'),
    ('Recipe Rates', 'tooltip'),
    ('Fishing Rates', 'window'),
    ('Thieving Overview', 'window'),
    ('Skill Bonuses', 'window'),
    ('Astrology Overview', 'window'),
    ('Away Summary', 'window'),
    ('Combat Goals', 'window'),
    ('Kills in Combat', 'tag'),
    ('Dungeon Completions', 'tag'),
]


def banner():
    """The top of the readme as one of the game's windows: the list of the mods, and the details. The window
    is as tall as the list needs, so a new mod gets a line of the same size as the others."""
    line, gap = 29, 4.2                           # a line of the list, and the space between two lines
    top = 112
    bottom = round(top + 32 + len(MODS) * (line + gap) - gap + 12)
    w, h = 1000, bottom + 32
    art = Art(w, h, "Melvor Idle 2 Mods: twelve quality-of-life mods in the game's own look. Separate downloads. "
                    'Your save is never changed.')
    frame(art, 0, 0, w, h)
    title = 'Melvor Idle 2 Mods'
    paper(art, w / 2, 22, paper_width(title, 38, 46), 52, title, 38, seed_of('banner'))
    close_button(art, w - 26 - 46, 24, 46)

    left_x, left_w, right_x, right_w = 22, 566, 604, 374
    column(art, left_x, top, left_w, bottom - top)
    column(art, right_x, top, right_w, bottom - top)
    paper(art, left_x + left_w / 2, top - 17, 214, 31, 'The mods', 20, seed_of('the mods'))
    paper(art, right_x + right_w / 2, top - 17, 214, 31, 'Details', 20, seed_of('details'))

    # the list: the first line is the selected one, like a result that was clicked
    x, y, wide = left_x + 18, top + 32, left_w - 36 - 13
    art.add(rect(x + wide + 5, y, 8, len(MODS) * (line + gap) - gap, '#262321'), rect(x + wide + 5, y, 8, 150, SCROLL))
    for i, (name, kind) in enumerate(MODS):
        row(art, x, y, wide, line, selected=i == 0)
        base = y + line / 2 + 20 * 0.355
        art.add(text(x + 16, base, name, 20, WHITE, 400), text(x + wide - 16, base, kind, 17, GOLD if i == 0 else GREY, 400, 'end'))
        y += line + gap

    # the details of the selection
    x, inner = right_x + 18, right_w - 36
    art.add(text(x + 4, top + 56, 'Quality-of-life mods', 23, WHITE, 700),
            text(x + 4, top + 80, 'Melvor Idle 2 · unofficial, fan-made', 16, GREY))
    heading(art, x, top + 108, inner, 'At a glance', 17)
    y = top + 126
    for label, value, colour in (('Mods', str(len(MODS)), WHITE), ('Downloads', 'one zip each', WHITE),
                                 ('Needs', 'Melvor Mod Loader', GOLD), ('Save file', 'never changed', GOOD)):
        stat(art, x, y, inner, 30, label, value, colour, 18)
        y += 33.5
    return art.render()


def section(title):
    """A section of the readme: a slice of a window, the title on paper over the edge of a column."""
    w, h = 900, 82
    art = Art(w, h, title)
    frame(art, 0, 0, w, h)
    column(art, 18, 33, w - 36, 31)
    paper(art, w / 2, 14, max(paper_width(title, 25, 34), 240), 37, title, 25, seed_of(title))
    return art.render()


def window_title(title):
    """The title of a mod's own page: the top of its window, with the X."""
    w, h = 900, 104
    art = Art(w, h, title)
    frame(art, 0, 0, w, h)
    paper(art, w / 2, 26, max(paper_width(title, 36, 44), 300), 50, title, 36, seed_of(title))
    close_button(art, w - 26 - 44, 29, 44)
    return art.render()


def button_picture(label, kind, w, h=43):
    art = Art(w, h + 3, label)
    button(art, 0, 0, w, h, label, kind, 19.5)
    return art.render()


def tag_picture(label, value):
    """One of the game's small dark tags: a thin light border, a grey name and its value."""
    size, pad, h = 14, 10, 26
    label_w, value_w = text_width(label, size), text_width(value, size, 700)
    w = round(pad + label_w + 7 + value_w + pad)
    art = Art(w, h, f'{label}: {value}')
    art.add(rect(0.5, 0.5, w - 1, h - 1, '#141310', ' rx="3" stroke="#8a8579"'),
            text(pad, h / 2 + size * 0.355, label, size, GREY),
            text(w - pad, h / 2 + size * 0.355, value, size, PAPER, 700, 'end'))
    return art.render()


SECTIONS = {
    'the-mods': 'The mods',
    'install': 'Install',
    'questions': 'Questions',
    'how-they-are-built': 'How they are built',
    'building': 'Building from source',
    'credits': 'Credits and license',
}
WINDOWS = {   # the title of each mod's own page
    'combat-simulator': 'Combat Simulator',
    'item-sources': 'Item Sources',
    'item-encyclopedia': 'Item Encyclopedia',
    'recipe-rates': 'Recipe Rates',
    'fishing-rates': 'Fishing Rates',
    'thieving-overview': 'Thieving Overview',
    'skill-bonuses': 'Skill Bonuses',
    'astrology-overview': 'Astrology Overview',
    'away-summary': 'Away Summary',
    'combat-goals': 'Combat Goals',
    'kills-in-combat': 'Kills in Combat',
    'dungeon-completions': 'Dungeon Completions',
}
BUTTON_PICTURES = {   # file, (text, look, width)
    'btn-download': ('Download', 'green', 190),
    'btn-nexus': ('On Nexus Mods', 'dark', 205),
    'btn-all-mods': ('All mods', 'dark', 170),
    'btn-loader': ('Melvor Mod Loader', 'dark', 250),
    'btn-issue': ('Report a problem', 'red', 240),
}
TAGS = {
    'tag-game': ('Melvor Idle 2', GAME_VERSION),
    'tag-loader': ('Melvor Mod Loader', LOADER_VERSION),
    'tag-platform': ('Steam', 'Windows'),
    'tag-license': ('License', 'MIT'),
}

if __name__ == '__main__':
    os.makedirs(OUT, exist_ok=True)
    files = {'banner.svg': banner()}
    files.update({f'h-{name}.svg': section(title) for name, title in SECTIONS.items()})
    files.update({f'h-{name}.svg': window_title(title) for name, title in WINDOWS.items()})
    files.update({f'{name}.svg': button_picture(*look) for name, look in BUTTON_PICTURES.items()})
    files.update({f'{name}.svg': tag_picture(*words) for name, words in TAGS.items()})
    for name in sorted(os.listdir(OUT)):            # a picture that is no longer drawn must not stay behind
        if name.endswith('.svg') and name not in files:
            os.remove(os.path.join(OUT, name))
            print(name, 'removed')
    for name, content in files.items():
        with open(os.path.join(OUT, name), 'w', encoding='utf-8', newline='\n') as f:
            f.write(content)
        print(name, len(content), 'bytes')
