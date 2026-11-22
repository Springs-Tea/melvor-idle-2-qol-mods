"""Draws the header banner of each Nexus Mods page from the README art.

    python tools/nexus-headers.py

Writes dist/nexus/headers/<Mod>.png (1300x372, the size Nexus asks for): the mod's drawn title on the game's
dark panel colour, with the question the mod answers under it. Pack.png, for the page of the zip with all the
mods, is the banner of the main README.

Nexus writes the mod's name and its counters over the lower 40% of a header, so everything is drawn in the
upper 60%. The drawing is done by a browser without a window (Edge or Chrome), found in the usual places.
"""
import os
import re
import shutil
import subprocess
import sys

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
ART = os.path.join(ROOT, 'docs', 'img', 'art')
OUT = os.path.join(ROOT, 'dist', 'nexus', 'headers')
WIDTH, HEIGHT = 1300, 372

HEADERS = {
    'CombatSim': ('h-combat-simulator.svg', 'What does an hour of this fight give me, what does it cost, and will I die?'),
    'ItemSources': ('h-item-sources.svg', 'Where does this drop, and what is it for?'),
    'ItemEncyclopedia': ('h-item-encyclopedia.svg', 'What is this item, what is it good for, and where do I get it?'),
    'RecipeRates': ('h-recipe-rates.svg', 'Which recipe gives more XP per hour?'),
    'CombatGoals': ('h-combat-goals.svg', 'How long until Magic 20, while I am also training Hitpoints and Defence?'),
    'KillsInCombat': ('h-kills-in-combat.svg', 'How many of these have I killed?'),
    'DungeonCompletions': ('h-dungeon-completions.svg', 'How many times have I cleared this dungeon?'),
    'Pack': ('banner.svg', ''),
}


def browser():
    places = [os.path.join(os.environ.get(root, ''), path)
              for root in ('ProgramFiles(x86)', 'ProgramFiles', 'LocalAppData')
              for path in (r'Microsoft\Edge\Application\msedge.exe', r'Google\Chrome\Application\chrome.exe')]
    names = ('msedge', 'google-chrome', 'chromium', 'chromium-browser', 'chrome')
    found = next((p for p in places if os.path.isfile(p)), None) or next(filter(None, map(shutil.which, names)), None)
    if not found:
        sys.exit('No Edge or Chrome found to draw the headers with.')
    return found


def page(name, file, line):
    svg = re.sub(r'<\?xml[^>]*\?>', '', open(os.path.join(ART, file), encoding='utf-8').read())
    size, top = ('height:208px;width:auto', 12) if name == 'Pack' else ('width:1000px;height:auto', 44)
    svg = svg.replace('<svg ', f'<svg style="{size};display:block" ', 1)
    text = (f'<div style="margin-top:20px;font:italic 28px Segoe UI,Arial,sans-serif;color:#edbf59;text-align:center">'
            f'{line}</div>') if line else ''
    return ('<!doctype html><meta charset="utf-8">'
            f'<body style="margin:0;width:{WIDTH}px;height:{HEIGHT}px;overflow:hidden;box-sizing:border-box;'
            'background:radial-gradient(ellipse at 50% 35%,#2a2420 0%,#14110f 75%);display:flex;'
            f'flex-direction:column;align-items:center;justify-content:flex-start;padding-top:{top}px">'
            + svg + text + '</body>')


def main():
    os.makedirs(OUT, exist_ok=True)
    draw = browser()
    for name, (file, line) in HEADERS.items():
        html = os.path.abspath(os.path.join(OUT, name + '.html'))
        png = os.path.abspath(os.path.join(OUT, name + '.png'))
        with open(html, 'w', encoding='utf-8') as f:
            f.write(page(name, file, line))
        subprocess.run([draw, '--headless', '--disable-gpu', '--hide-scrollbars', '--force-device-scale-factor=1',
                        f'--window-size={WIDTH},{HEIGHT}', f'--screenshot={png}', 'file:///' + html.replace('\\', '/')],
                       check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        os.remove(html)
        print(name + '.png')


if __name__ == '__main__':
    main()
