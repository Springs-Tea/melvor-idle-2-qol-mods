"""Turns each mod's README.md into the description of its Nexus Mods page, so the two say the same thing.

    python tools/nexus-pages.py [images.json]

Writes dist/nexus/<Mod>.bbcode: the guide in Nexus formatting (BBCode), ready to paste into the page's
description in source view, and Pack.bbcode for the page of the zip with all the mods: the text of
publish/nexus/Pack.txt with the opening of every mod's guide in place of its list.

Pictures are the ones of the README. Nexus shows a picture only from a web address, so images.json
(publish/nexus/images.json when none is named) maps, per page, a picture's file name to the address it has
once it is uploaded to that page's gallery:

    {"CombatSim": {"combat-simulator.png": "https://..."}, "Pack": {...}}

A picture with no address yet is left out.
"""
import json
import os
import re
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
REPO = 'https://github.com/Springs-Tea/melvor-idle-2-qol-mods'
PAGE = 'https://www.nexusmods.com/melvoridle2/mods/'
# The number of each mod's page on Nexus. None is a mod that has no page there yet: until its number is
# written here, links to it lead to its guide on GitHub.
PAGES = {'CombatSim': 17, 'ItemSources': 18, 'ItemEncyclopedia': 24, 'RecipeRates': 19, 'FishingRates': None, 'MiningRates': None, 'WoodcuttingRates': None, 'FiremakingRates': None, 'HuntingRates': None,
         'ThievingOverview': None, 'SkillBonuses': 35, 'AstrologyOverview': 36, 'AwaySummary': 37, 'CombatGoals': 20,
         'KillsInCombat': 21, 'DungeonCompletions': 22, 'Pack': 23}
MODS = [mod for mod in PAGES if mod != 'Pack']
PAPER, GOLD, GREY = '#e9dfc1', '#edbf59', '#b5b5b5'   # the game's paper, gold and secondary text


def address(mod):
    return PAGE + str(PAGES[mod]) if PAGES.get(mod) else f'{REPO}/tree/main/mods/{mod}'


def heading(title):
    return [f'[size=5][b][color={PAPER}]{title}[/color][/b][/size]', '[line]']


def page_text(mod):
    """The NAME and the DESCRIPTION of publish/nexus/<Mod>.txt."""
    text = open(os.path.join(ROOT, 'publish', 'nexus', mod + '.txt'), encoding='utf-8').read()
    name = re.search(r'^NAME\n(.+)$', text, re.M).group(1).strip()
    return name, text.split('\n', text[:text.index('DESCRIPTION')].count('\n') + 1)[-1].strip()


def inline(text, folder):
    """Bold, code, links."""
    def link(match):
        label, target = match.group(1), match.group(2)
        if not re.match(r'https?://', target):
            path = os.path.normpath(os.path.join('mods', folder, target)).replace('\\', '/')
            other = re.fullmatch(r'mods/(\w+)(/README\.md)?', path)   # another mod's guide: its own page here
            target = address(other.group(1)) if other and other.group(1) in PAGES else REPO + '/tree/main/' + path
        return f'[url={target}]{label}[/url]'
    text = re.sub(r'\[([^\]]+)\]\(([^)]+)\)', link, text)
    text = re.sub(r'\*\*(.+?)\*\*', r'[b]\1[/b]', text)
    text = re.sub(r'(?<![\w*])\*([^*\n]+?)\*(?![\w*])', r'[i]\1[/i]', text)
    text = re.sub(r'`([^`]+)`', r'[font=Courier New]\1[/font]', text)
    return text


def body(lines, folder, images):
    """Markdown lines as BBCode lines."""
    out, i, skipping = [], 0, False

    def pictures(sources):
        shown = [f'[img]{images[name]}[/img]' for name in map(os.path.basename, sources) if name in images]
        if shown:
            out.append('[center]' + ' '.join(shown) + '[/center]')
            out.append('')
        else:   # "..., as in the picture:" with no picture under it
            last = next((n for n in range(len(out) - 1, -1, -1) if out[n]), None)
            if last is not None and out[last].endswith(':'):
                out[last] = out[last][:-1] + '.'

    def paragraph(buffer):
        if buffer:
            out.append(inline(' '.join(buffer), folder))
            out.append('')

    buffer = []
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        # The drawn header and the buttons at the top are the page's own header on Nexus.
        if stripped.startswith('<h1') or stripped.startswith('<p align="center">'):
            while i < len(lines) and '</p>' not in lines[i] and '</h1>' not in lines[i]:
                i += 1
            i += 1
            continue

        if stripped.startswith('## '):
            paragraph(buffer); buffer = []
            title = stripped[3:]
            skipping = title == 'Contents'   # a list of links into the page: Nexus has no anchors
            if not skipping:
                out.extend(heading(title))
            i += 1
            continue
        if skipping:
            i += 1
            continue

        if stripped.startswith('```'):
            paragraph(buffer); buffer = []
            kind, block = stripped[3:], []
            i += 1
            while i < len(lines) and not lines[i].strip().startswith('```'):
                block.append(lines[i])
                i += 1
            i += 1
            if kind != 'mermaid':   # a diagram cannot be drawn in a description
                out.append('[code]' + '\n'.join(block) + '[/code]')
                out.append('')
            continue

        if stripped.startswith('>'):
            paragraph(buffer); buffer = []
            out.append(f'[center][size=4][i][color={GOLD}]{inline(stripped[1:].strip(), folder)}[/color][/i][/size][/center]')
            out.append('')
            i += 1
            continue

        if stripped.startswith('!['):
            paragraph(buffer); buffer = []
            pictures([re.search(r'\(([^)]+)\)', stripped).group(1)])
            i += 1
            continue

        # A paragraph written as <p>...</p> gives only its pictures. One without pictures is for GitHub alone:
        # the line that says where to download from, which on Nexus is the page one is reading.
        if stripped.startswith('<p>') or stripped.startswith('<img'):
            paragraph(buffer); buffer = []
            block = []
            while i < len(lines) and lines[i].strip() and (not block or '</p>' not in block[-1]):
                block.append(lines[i])
                i += 1
            pictures(re.findall(r'src="([^"]+)"', ' '.join(block)))
            continue

        if stripped.startswith('|'):
            paragraph(buffer); buffer = []
            rows = []
            while i < len(lines) and lines[i].strip().startswith('|'):
                cells = [c.strip() for c in lines[i].strip().strip('|').split('|')]
                if not all(re.fullmatch(r':?-+:?', c) for c in cells):
                    rows.append(cells)
                i += 1
            out.append('[list]')
            for cells in rows[1:]:   # without the heading row
                first, rest = inline(cells[0], folder), ' '.join(c for c in cells[1:] if c)
                if first and not first.startswith('[b]'):
                    first = f'[b]{first}[/b]'
                out.append(f'[*]{first}{": " if first and rest else ""}{inline(rest, folder)}')
            out.append('[/list]')
            out.append('')
            continue

        bullet = re.match(r'^(\s*)(-|\d+\.)\s+(.*)$', line)
        if bullet:
            paragraph(buffer); buffer = []
            numbered = bullet.group(2) != '-'
            items = []
            while i < len(lines):
                item = re.match(r'^(\s*)(-|\d+\.)\s+(.*)$', lines[i])
                if item:
                    items.append(item.group(3))
                elif lines[i].startswith('  ') and lines[i].strip() and items:
                    items[-1] += ' ' + lines[i].strip()
                else:
                    break
                i += 1
            out.append('[list=1]' if numbered else '[list]')
            out.extend('[*]' + inline(item, folder) for item in items)
            out.append('[/list]')
            out.append('')
            continue

        if not stripped:
            paragraph(buffer); buffer = []
        else:
            buffer.append(stripped)
        i += 1
    paragraph(buffer)
    return out


def readme(folder):
    return open(os.path.join(ROOT, 'mods', folder, 'README.md'), encoding='utf-8').read().split('\n')


def tidy(out):
    return re.sub(r'\n{3,}', '\n\n', '\n'.join(out)).strip() + '\n'


def convert(folder, images, others):
    """One mod's page: its guide, the other mods, and the closing notes."""
    out = body(readme(folder), folder, images)

    out.extend(heading('The rest of the set'))
    out.append('Sixteen mods made together; each works on its own.')
    out.append('[list]')
    out.extend(f'[*][url={address(mod)}][b]{name}[/b][/url]: {line}' for mod, (name, line) in others.items() if mod != folder)
    out.append('[/list]')
    out.append(f'All sixteen in one zip: [url={PAGE}{PAGES["Pack"]}]Quality of Life Mods (all in one)[/url]')
    out.append('')

    out.extend(heading('About'))
    out.append('Made for game version 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3. The game is in early access: an update '
               'can break the mod until it is rebuilt. It then switches itself off and says so in MelvorModLoader.log; the '
               'game and your other mods keep running.')
    out.append("It uses the game's own textures, fonts and colours, so it looks like part of the game. It gives no items, XP or bonuses.")
    out.append(f'Open source (MIT): [url={REPO}]{REPO}[/url]')
    out.append(f'Problems and ideas: [url={REPO}/issues]{REPO}/issues[/url]')
    out.append(f'[color={GREY}]Unofficial and fan-made. Not affiliated with Games by Malcs or Jagex.[/color]')
    return tidy(out)


def pack(images):
    """The page of the zip with all the mods, and what its list says about each mod."""
    _, text = page_text('Pack')
    text = re.sub(r'^\[size=5\]\[b\].*\[/b\]\[/size\]\n', '', text)   # the page's name is its header
    text = re.sub(r'\[size=4\]\[b\](.+?)\[/b\]\[/size\]', lambda m: '\n' + '\n'.join(heading(m.group(1))), text)
    before, rest = text.split('[list]', 1)
    listed, after = rest.split('[/list]', 1)

    others, out = {}, [before.strip(), '']
    for mod in MODS:
        name, _ = page_text(mod)
        others[mod] = (name, re.search(r'\[\*\]\[b\]' + re.escape(name) + r'\[/b\]: (.+)', listed).group(1).strip())
        lines = readme(mod)
        opening = lines[:next(n for n, line in enumerate(lines) if line.startswith('## '))]
        out.extend(heading(name))
        out.extend(body(opening, mod, images))
        out.append(f'[url={address(mod)}]The full guide is on its own page: {name}[/url]')
        out.append('')
    out.append(after.strip())
    return tidy(out), others


def main():
    known = sys.argv[1] if len(sys.argv) > 1 else os.path.join(ROOT, 'publish', 'nexus', 'images.json')
    images = json.load(open(known, encoding='utf-8')) if os.path.isfile(known) else {}
    folder = os.path.join(ROOT, 'dist', 'nexus')
    os.makedirs(folder, exist_ok=True)

    everywhere = {name: address for page in images.values() for name, address in page.items()}
    text, others = pack({**everywhere, **images.get('Pack', {})})
    pages = {mod: convert(mod, images.get(mod, {}), others) for mod in MODS}
    pages['Pack'] = text
    for mod, text in pages.items():
        with open(os.path.join(folder, mod + '.bbcode'), 'w', encoding='utf-8', newline='\n') as f:
            f.write(text)
        print(f'{mod}: {len(text):,} characters, {text.count("[img]")} pictures')


if __name__ == '__main__':
    main()
