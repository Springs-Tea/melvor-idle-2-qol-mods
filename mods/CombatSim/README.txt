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
    deaths, and the lowest your hitpoints got. Click a column to sort: the first click puts the
    best fights on top (the most XP, but the fewest deaths), the next turns the order around.
    A small arrow after the column's name shows which way: down for the most first.
  - Columns, over the list, chooses which numbers it shows and in which order, out of 43 in
    groups (offence, defence, supplies, economy, experience, dungeons, time): runes, ammo and
    prayer points used, what the supplies cost and the gold left after them, damage taken,
    Safe or Risky, the enemy's biggest hit, the XP of each skill, dungeon runs and more. One
    click on a preset (Idle, Money, Supplies, Training, Dungeons) sets several at once; Reset
    brings back the seven above. The choice is kept in settings.json. The window keeps its
    width: when the numbers need more room, a bar under them moves them sideways, a column at
    a time, under the enemy's name. "-" means no number: nothing was killed, the fight is not
    a dungeon, or an earlier scan did not record it.
  - Details: per skill XP, loot item by item, what was used (food, ammo, runes, prayer points),
    damage taken, time of the first death, food and healing per kill, and how many kills the
    food in your bank is enough for. When the same fight was simulated with other equipment,
    the difference is shown for every column of the list, green where it is better; the
    columns in which both are the same share one line.
  - Best for: pick the skill you want to train and the areas to look in. Every fight is tried
    in each attack stance of your weapon and the fights are ranked by what they give that
    skill, with the best stance named on each line.
  - History: the last ten scans are kept, also after the game was closed. Look at one again,
    table and details, without simulating anything. They are old numbers: the window says so.
  - Enemy cards: in the game's combat areas menu, each enemy's card gets a small tag with
    the newest scan of that enemy made with what you have on now: Safe or Risky, then XP and
    deaths per hour. Safe: no deaths, and no hit of that enemy reaches your auto-eat point.
    A grey "Other setup" tag: that scan was made with other equipment, stance, spell, food or
    prayers. An enemy you have not simulated, or a dungeon's enemy, gets no tag. Click a tag
    to open its scan in the simulator. Opening the menu never simulates anything. Untick the
    box at the bottom of History to hide the tags.
  - Fight: the red button under the details starts the selected fight in the game (and
    switches to the stance and the spell of that result). Your equipment is left as it is,
    unless you tick "Wear this setup first" above the button. It is off until you tick it.
    Ticked, Fight first puts on what the result was simulated with, through the game's own
    actions: it changes to that loadout (which comes with the food, prayers, attack stance
    and spells the game keeps in it), puts on each item you swapped in from your bank (what
    it replaces goes back to the bank; ammunition: as many as the bank holds) and selects
    the spell. An item that is not in your bank, that you cannot wear yet, or whose old
    piece your bank has no room for is skipped, and its slot stays as it is. A slot you
    emptied keeps what you wear there: Fight never just takes a piece off. The text above
    the button names every step first, or why nothing goes on, and Fight never does more
    than it says. If something changed since the text was written, nothing is put on and no
    fight starts. When everything went on, the window closes; when a slot stayed as it was
    or a piece could not go on as the text said, the window stays open and says what really
    happened. If the game does not do a step as it should, Fight stops there and starts no
    fight. Nothing is bought, made or put back. When the box puts nothing on for a fight it
    looks greyed; point at it for why.

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

The mod never writes your save: only Fight with "Wear this setup first" ticked changes your
equipment, through the game's own actions. Settings are kept in mods\CombatSim\settings.json,
earlier scans in mods\CombatSim\history
Unofficial, fan-made. Not affiliated with Games by Malcs or Jagex.
