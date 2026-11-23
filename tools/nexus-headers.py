"""Draws the header banner of each Nexus Mods page from the README art.

    python tools/nexus-headers.py

Writes dist/nexus/headers/<Mod>.png (1300x372, the size Nexus asks for): the mod's drawn title on the game's
dark panel colour, with the question the mod answers under it. Pack.png, for the page of the zip with all the
mods, is the banner of the main README.

Also writes docs/img/social-preview.png (1280x640, the size GitHub asks for): the same banner on a plain dark
ground, the picture shown where a link to the repository is shared. GitHub does not read it from the
repository: when it has changed, upload it by hand under Settings > General > Social preview.

Nexus writes the mod's name and its counters over the lower 40% of a header, so everything is drawn in the
upper 60%. The drawing is done by a browser without a window (Edge or Chrome), found in the usual places. The
picture is asked for through the browser's DevTools protocol: its --screenshot switch stopped writing files
in newer versions (seen with Edge 158).
"""
import base64
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import time
import urllib.request

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..')
ART = os.path.join(ROOT, 'docs', 'img', 'art')
OUT = os.path.join(ROOT, 'dist', 'nexus', 'headers')
WIDTH, HEIGHT = 1300, 372
SOCIAL = os.path.join(ROOT, 'docs', 'img', 'social-preview.png')
SOCIAL_WIDTH, SOCIAL_HEIGHT = 1280, 640
SOCIAL_BANNER, SOCIAL_GROUND = 1160, '#12100e'

HEADERS = {
    'CombatSim': ('h-combat-simulator.svg', 'What does an hour of this fight give me, what does it cost, and will I die?'),
    'ItemSources': ('h-item-sources.svg', 'Where does this drop, and what is it for?'),
    'ItemEncyclopedia': ('h-item-encyclopedia.svg', 'What is this item, what is it good for, and where do I get it?'),
    'RecipeRates': ('h-recipe-rates.svg', 'Which recipe gives more XP per hour?'),
    'FishingRates': ('h-fishing-rates.svg', 'Where do I fish for the most XP, fish or gold per hour?'),
    'MiningRates': ('h-mining-rates.svg', 'Where do I mine for the most XP, ore or gold per hour?'),
    'ThievingOverview': ('h-thieving-overview.svg', 'Which target should I steal from, for XP, for gold or for one item, and will it knock me out?'),
    'SkillBonuses': ('h-skill-bonuses.svg', 'Why is my Woodcutting this fast?'),
    'AstrologyOverview': ('h-astrology-overview.svg', 'Which constellation is closest to its next unlock?'),
    'AwaySummary': ('h-away-summary.svg', 'What changed while I was away?'),
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


def art(file, size):
    svg = re.sub(r'<\?xml[^>]*\?>', '', open(os.path.join(ART, file), encoding='utf-8').read())
    return svg.replace('<svg ', f'<svg style="{size};display:block" ', 1)


def page(name, file, line):
    size, top = ('height:208px;width:auto', 12) if name == 'Pack' else ('width:1000px;height:auto', 44)
    text = (f'<div style="margin-top:20px;font:italic 28px Segoe UI,Arial,sans-serif;color:#edbf59;text-align:center">'
            f'{line}</div>') if line else ''
    return ('<!doctype html><meta charset="utf-8">'
            f'<body style="margin:0;width:{WIDTH}px;height:{HEIGHT}px;overflow:hidden;box-sizing:border-box;'
            'background:radial-gradient(ellipse at 50% 35%,#2a2420 0%,#14110f 75%);display:flex;'
            f'flex-direction:column;align-items:center;justify-content:flex-start;padding-top:{top}px">'
            + art(file, size) + text + '</body>')


def social_page():
    # The whole banner in the middle of a plain dark ground. Sites that show the picture cut it to their own
    # shape, so the banner keeps at least 60 pixels away from every edge.
    return ('<!doctype html><meta charset="utf-8">'
            f'<body style="margin:0;width:{SOCIAL_WIDTH}px;height:{SOCIAL_HEIGHT}px;overflow:hidden;'
            f'background:{SOCIAL_GROUND};display:flex;align-items:center;justify-content:center">'
            + art('banner.svg', f'width:{SOCIAL_BANNER}px;height:auto') + '</body>')


class DevTools:
    """Just enough of a WebSocket client to send DevTools commands to one page and read the answers."""

    def __init__(self, url):
        host, port, path = re.match(r'ws://([^:/]+):(\d+)(/.*)', url).groups()
        self.sock = socket.create_connection((host, int(port)), timeout=30)
        key = base64.b64encode(os.urandom(16)).decode()
        self.sock.sendall((f'GET {path} HTTP/1.1\r\nHost: {host}:{port}\r\nUpgrade: websocket\r\n'
                           f'Connection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n').encode())
        reply = b''
        while b'\r\n\r\n' not in reply:
            reply += self.sock.recv(4096)
        if b' 101 ' not in reply.split(b'\r\n', 1)[0]:
            raise RuntimeError('The browser refused the DevTools connection.')
        self.next_id = 0

    def _read(self, count):
        data = b''
        while len(data) < count:
            chunk = self.sock.recv(count - len(data))
            if not chunk:
                raise RuntimeError('The browser closed the DevTools connection.')
            data += chunk
        return data

    def _message(self):
        payload = b''
        while True:
            first, second = self._read(2)
            size = second & 0x7f
            if size == 126:
                size = int.from_bytes(self._read(2), 'big')
            elif size == 127:
                size = int.from_bytes(self._read(8), 'big')
            payload += self._read(size)
            if first & 0x80:
                return json.loads(payload)

    def call(self, method, **params):
        self.next_id += 1
        data = json.dumps({'id': self.next_id, 'method': method, 'params': params}).encode()
        mask = os.urandom(4)
        size = len(data)
        head = bytes([0x81]) + (bytes([0x80 | size]) if size < 126 else bytes([0x80 | 126]) + size.to_bytes(2, 'big'))
        self.sock.sendall(head + mask + bytes(b ^ mask[i % 4] for i, b in enumerate(data)))
        while True:
            answer = self._message()
            if answer.get('id') == self.next_id:
                if 'error' in answer:
                    raise RuntimeError(f'{method}: {answer["error"]}')
                return answer.get('result', {})


def shoot(tools, html, png, width, height):
    """Draws one page at the given size and writes the picture."""
    file = os.path.join(OUT, 'page.html')
    with open(file, 'w', encoding='utf-8') as f:
        f.write(html)
    try:
        tools.call('Emulation.setDeviceMetricsOverride', width=width, height=height, deviceScaleFactor=1, mobile=False)
        tools.call('Page.navigate', url='file:///' + os.path.abspath(file).replace('\\', '/'))
        time.sleep(1.5)
        shot = tools.call('Page.captureScreenshot', format='png',
                          clip={'x': 0, 'y': 0, 'width': width, 'height': height, 'scale': 1})
        with open(png, 'wb') as f:
            f.write(base64.b64decode(shot['data']))
    finally:
        os.remove(file)


def main():
    os.makedirs(OUT, exist_ok=True)
    profile = tempfile.mkdtemp(prefix='nexus-headers-')
    # A profile of its own: a running Edge or Chrome would otherwise take the command over and draw nothing.
    process = subprocess.Popen([browser(), '--headless=new', '--remote-debugging-port=0', f'--user-data-dir={profile}',
                                '--disable-gpu', '--hide-scrollbars', 'about:blank'],
                               stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        port_file = os.path.join(profile, 'DevToolsActivePort')
        for _ in range(100):
            if os.path.isfile(port_file) and open(port_file).read().strip():
                break
            time.sleep(0.2)
        else:
            sys.exit('The browser did not open its DevTools port.')
        port = open(port_file).read().split()[0]
        pages = json.load(urllib.request.urlopen(f'http://127.0.0.1:{port}/json/list'))
        tools = DevTools(next(p for p in pages if p['type'] == 'page')['webSocketDebuggerUrl'])
        for name, (file, line) in HEADERS.items():
            shoot(tools, page(name, file, line), os.path.join(OUT, name + '.png'), WIDTH, HEIGHT)
            print(name + '.png')
        shoot(tools, social_page(), SOCIAL, SOCIAL_WIDTH, SOCIAL_HEIGHT)
        print('docs/img/social-preview.png')
    finally:
        process.kill()
        process.wait()
        shutil.rmtree(profile, ignore_errors=True)


if __name__ == '__main__':
    main()
