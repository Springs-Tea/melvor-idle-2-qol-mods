COMBAT SIMULATOR {version}
for Melvor Idle 2 (tested on 0.11.6-alpha.1), needs Melvor Mod Loader

What does an hour of fighting this enemy give me, what does it cost, and will I die? This answers
it without fighting.

It does not re-create the game's combat formulas. It loads a copy of your character into a second,
invisible game and runs the game's own combat on it, in the background. An hour of fighting takes
a fraction of a second; a copy of the character takes about one. Nothing the copy does is saved.

  - The round chart button on the right edge opens and closes the window. Click the button
    beside the red X to fold it down to its title. Until you run a scan after loading your
    character, it says "Combat Simulator". During a scan it counts the finished fights,
    "Simulator · 4 of 12", and at the end it says "Simulator · Done: 12 fights". A scan you
    cancel says "Simulator · Stopped: 4 of 12". "Simulator · Error" means the window has a
    message for you: open it to read it.
  - Click the fold button again or the round chart button to return to the same tab, list,
    sort order and selected fight. The red X closes the window even when folded. A scan
    keeps running while the window is folded or closed. The window remembers its place,
    size and whether you left it folded.
  - Setup: where to fight, for how long, and whether supplies are unlimited or come from your
    bank. "Where" opens a list in which you tick one area, several, or none for everything
    (an area simulates every enemy in it, a dungeon is one fight).
  - Equipment: pick one of your saved loadouts, and click any slot to try another item: every
    item of the game that fits, with a search box, whether you own it or not. When the weapon
    casts spells, choose which one to fight with.
  - Results: one line per fight, per hour: kills, combat XP, gold, food eaten, food per kill,
    deaths, and the lowest your hitpoints got. Click a column to sort.
  - Details: per skill XP, loot item by item, what was used (food, ammo, runes, prayer points),
    damage taken, time of the first death, food and healing per kill, and how many kills the
    food in your bank is enough for. When the same fight was simulated with other equipment,
    the difference is shown.
  - Best for: pick the skill you want to train and the areas to look in. Every fight is tried
    in each attack stance of your weapon and the fights are ranked by what they give that
    skill, with the best stance named on each line.
  - History: the last ten scans are kept, also after the game was closed. Look at one again,
    table and details, without simulating anything. They are old numbers: the window says so.
  - Fight: the red button under the details starts the selected fight in the game (and
    switches to the stance and the spell of that result). Your equipment is left as it is.

Good to know
  - "Unlimited" supplies hand food, ammo, runes and prayer points back as they are used. "From
    bank" uses what you have and says what ran out, and when. Without ammo or runes the fight
    ends there. After the food runs out, ten deaths in a row without a kill end the fight. The
    note says why it stopped and the rates cover the time fought.
  - Farming and Ranching are paused in the copy, so their items do not show up as supplies.
  - Fights are random: two runs of the same fight differ a little. Longer fights are steadier.
  - An item whose requirements you do not meet cannot be equipped in the copy either; the
    result says so.
  - Up to four fights run side by side. If the game stutters during a scan, set "Workers" in
    settings.json to 1 or 2 (0 lets the mod choose).

INSTALL
  Extract this zip into the game folder (Steam > Melvor Idle 2 > Manage > Browse local files),
  so that the file ends up as  mods\CombatSim\CombatSim.dll

UNINSTALL
  Delete the folder mods\CombatSim

Nothing in your save is changed. Settings are kept in mods\CombatSim\settings.json, earlier
scans in mods\CombatSim\history
Unofficial, fan-made. Not affiliated with Games by Malcs or Jagex.
