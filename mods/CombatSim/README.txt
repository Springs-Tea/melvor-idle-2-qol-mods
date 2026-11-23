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
    cancel says "Simulator · Stopped: 4 of 12". Find upgrades counts its fights the same way.
    "Simulator · Error" means the window has a message for you: open it to read it.
  - Click the fold button again or the round chart button to return to the same tab, list,
    sort order and selected fight. The red X closes the window even when folded. A scan
    keeps running while the window is folded or closed. The window remembers its place,
    size and whether you left it folded. At the smaller sizes the line above the list, a place
    under "Found in", a long name in Details, in the earlier scans under an empty list and in
    the lists of History, Where and an item's slot go on to a second line instead of being
    cut. A name too long for a line of the list ends in "..." with the whole name in the
    line's tooltip. Where says "2 areas" when two long area names do not fit on it. A long
    enemy or dungeon name, at any size, leaves Fight without its picture, or saying only
    "Fight" where even that is too long. Their tooltips then start with the whole words.
  - Setup: where to fight, for how long, and whether supplies are unlimited or come from your
    bank. "Where" opens a list in which you tick one area, several, or none for everything
    (an area simulates every enemy in it, a dungeon is one fight).
  - Go on until, beside "Each fight lasts": "Time is up", or "± 1% sure" to "± 10% sure".
    Each fight goes on after its time until every number in the list is known to that size,
    12 hours of fighting at most. A number that 12 hours cannot bring that far goes on until
    it is within the next larger size it can reach; one that cannot reach even ± 10% (a few
    deaths, gold that hangs on a rare drop, a number close to 0) does not hold it back, nor
    does a 0. A smaller size never ends a fight sooner than a larger one. Greyed out with
    supplies From bank. The line above Simulate then says the most the run can take: "up to
    2 min". Low HP, First death, Max hit and Safe are of all the time a fight ran: a longer
    fight had more time to get low.
  - Equipment: pick one of your saved loadouts, and click any slot to try another item: every
    item of the game that fits, with a search box, whether you own it or not. When the weapon
    casts spells, choose which one to fight with. A two-handed weapon fills both hands, as on
    the game's equipment screen: an item picked for the Offhand takes it off, and taking it
    off empties both hands.
  - Stance, in Fights: the attack stances of the weapon in the slots, in the game's names and
    with its pictures. The first choice is the stance the game has selected for that weapon, as
    before: "As now: Accurate" with the loadout you wear and a weapon of the kind you hold, "As
    the game has it" otherwise. A stance chosen is a setup of its own, as a spell is, with its
    own luck (in Compare, A and B meet the same dice). A weapon of another kind brings its own
    stances; a stance it does not have goes back to the first choice. Best for has no Stance
    box: it tries every stance.
  - Compare, at the end of the Equipment heading: two setups, A and B. The A and B buttons in
    front of the loadout choose which one the loadout, the slots, the stance (in Fights) and
    the spell show and change; the first time, B is a copy of A. One press of Simulate runs
    every fight with both, from the same moment of your character and with the same dice. Each
    column of the list becomes three: under its name which setup is better and by how much
    ("A by 23%", "A ×4" from twice as much up; for Healed, Items, Fought and Sim time which
    has more, "A +12%"), then the number with A and with B. The better side is green and the
    worse red, grey where it could be luck: green and red where the difference is larger than
    the two 95% ranges side by side would allow, less the share of the luck that A and B met
    alike. That share is measured from the fights at the same kills, and only what is beyond
    its own chance is taken off, so two setups that meet no luck alike are coloured by luck
    about as often as two ranges side by side. The more alike the two fight, the more luck
    they share (two helmets, one weapon in two stances); Warrior and Mage share almost none.
    Details writes the lead with how far luck could move it, "A by 48% ± 47%": green and red
    exactly when the lead is larger than its ±, with one more figure where the two would read
    the same ("A by 64.4% ± 64.1%"). Judged as before, by the two own ranges side by side:
    counts that did not come in every part of either fight (a death or two), a number one of
    them had none of, fights that ended at different times (Go on until, or From bank when
    supplies ran out), fights whose dice could not be kept in step, and scans made before
    this. Low HP, First death, Safe, Max hit and Best drop have no range: where two setups
    differ in one of them it is grey, unless only one of the two died, which goes as Deaths
    does (Low HP, First death, Safe), or the game's own numbers decide it (Safe, Max hit). In
    Details the line under it says which: "A, could be luck, no range", "A, by the deaths" or
    "A, by the game's own numbers" (see Ranges). Click a column's name for the fights where
    the choice matters most; the line above the list counts the fights each setup wins in the
    column it is sorted by, and in Best for says where each setup does best. Details shows the
    fight with both setups side by side, each with its stance ("as the game has it" where both
    fought in the same one and only one chose it). Fight is for the setup on the left. A and B
    last until the game is closed or another character is opened.
  - Find upgrades, the button over "Wear this setup first", opens a panel where you choose
    which items to try against the selected fight, one slot at a time, on top of the setup on
    the left: by slot, by where they come from (Owned, Drops in the fight's area, Enemy drop,
    Skill, Shop, Chest and the other kinds of the Item Encyclopedia), or by name. Each tick
    says how many items it stands for; the line beside Simulate says how many fights a press
    runs and about how long, at most 40 items a press. The choice is kept. Each item is one
    fight from the same moment of your character, one at a time, in the background like a
    scan (Stop, or Cancel on the left, stops it). Every item is fought with the same luck as
    the setup as it is, so a difference comes from the item, not from the dice. Every column
    of the list says what the item changes: "+14%", "-23%", "×2.5", "+7,300" where there was
    none, "+1,200" more Net gold; green better, red worse, grey where it could be luck, "no
    "better" for the same, no colour for Healed, Items, Fought and Sim time. That is judged as
    Compare judges B against A, the item against the setup as it is: the share of the luck
    they met alike is measured at the same kills, so an item that changes how fast you kill (a
    weapon of another speed, boots that add damage) shares less of it. Only words stay the
    item's own: Safe or Risky, the best drop, a first death where one of the two never dies.
    Low HP, First death, Safe, Max hit and Best drop have no range: where two setups differ in
    one of them it is grey, unless only one of the two died, which goes as Deaths does (Low
    HP, First death, Safe), or the game's own numbers decide it (Safe, Max hit). In Details
    the line under it says which: "could be luck, no range", "by the deaths" or "by the game's
    own numbers" (see Ranges). Click a column's name to put the largest gains on
    top. "Comes from" is Item Sources' answer, or "In your bank" when you have one. A
    two-handed weapon takes the shield off and a shield the two-handed weapon, as a pick in
    the slot does. An item that fought in another stance than the setup as it is, such as a
    weapon of another kind, names both stances in its tooltip and Details. Items whose
    requirements you do not meet are never tried; ticked in the panel, they are listed at the
    end with what they need in grey. Click a line for both numbers of every column, with how
    far luck could move the change where the same dice decided it ("± 9.1%" under "+14%";
    "+150%" where the list says "×2.5"), and every place the item comes from (an enemy's area,
    or "Dungeon reward", on a small line under the name where the two do not fit on one line,
    for every enemy of that heading). Put the pointer on a line for the ranges of its numbers
    and, where the same dice decided, each change with its ±. Nothing is put on, and the items
    tried are not kept in History. "Back to the fights" leaves them, and "Show upgrades" under
    Details brings them back.
  - Results: one line per fight, per hour: kills, combat XP, gold, food eaten, food per kill,
    deaths, and the lowest your hitpoints got. Click a column to sort: the first click puts the
    best fights on top (the most XP, but the fewest deaths), the next turns the order around.
    A small arrow after the column's name shows which way: down for the most first.
  - Ranges: every rate comes with its 95% range, which holds the true number 95 times in 100.
    Put the pointer on a line for the stance it was fought in ("Stance: Accurate, as the game
    has it" when you did not choose it) and the range of each of its numbers ("Kills ± 7.9%",
    and "Deaths up to 3.69": no deaths in an hour is not "never"). In Details the range of each
    rate is on the small line under it. Scans made before the ranges were counted have none. A
    rare drop that did not come in the time simulated is in no range: when an enemy's gold
    hangs on one, simulate it for 12 hours. Low HP, First death, Safe, Max hit and Best drop
    have no range: each is one moment of one fight, or hangs on one. Where two setups differ
    in one of them it is grey, "could be luck, no range", unless only one of the two died,
    which goes as Deaths does (Low HP, First death, Safe), or the game's own numbers decide it
    (Safe, Max hit): the enemy's biggest hit as the game gives it, against your auto-eat
    point. A bigger loss of hitpoints seen in the fight, a biggest hit of the game's that rose
    after a prayer or a toolbelt item ran out, and a scan made before this version leave it to
    luck. A number per kill against a fight that killed nothing goes as Kills does, and one
    per run as Runs does. Fought has no colour: it says how long a fight ran, not how good a
    setup is.
  - Columns, over the list, chooses which numbers it shows and in which order, out of 43 in
    groups (offence, defence, supplies, economy, experience, dungeons, time): runes, ammo and
    prayer points used, what the supplies cost and the gold left after them, damage taken,
    Safe or Risky, the enemy's biggest hit, the XP of each skill, dungeon runs and more. One
    click on a preset (Idle, Money, Supplies, Training, Dungeons) sets several at once; Reset
    brings back the seven above. The choice is kept in settings.json. The window keeps its
    width: when the numbers need more room, a bar under them moves them sideways, a column at
    a time, under the enemy's name. "-" means no number: nothing was killed, the fight is not
    a dungeon, or an earlier scan did not record it.
  - Details: under the setup, the attack stance the fight was fought in, with the game's
    picture of it ("as the game has it" when none was chosen); per skill XP, loot item by item,
    what was used (food, ammo, runes, prayer points), damage taken, time of the first death,
    food and healing per kill, and how many kills the food in your bank is enough for. When the
    same fight was simulated with other equipment or another stance, the difference is shown
    for every column of the list, green where it is better, red where it is worse, but only
    when the two ranges do not overlap (for A and B of one compare, when the change is larger
    than its ±, as in Compare): otherwise it is grey and says "could be luck". Low HP, First
    death, Safe, Max hit and Best drop have no range: where two setups differ in one of them
    it is grey, unless only one of the two died, which goes as Deaths does (Low HP, First
    death, Safe), or the game's own numbers decide it (Safe, Max hit). The line under it says
    which: "could be luck, no range", "by the deaths" or "by the game's own numbers" (see
    Ranges). The other setup's stance is under its name when it tells the two apart: another
    stance, or the same one chosen on one side only. XP in a skill one setup does not train at
    all, like Magic with a sword, is a real 0: any XP in it is better, and its line says "not
    trained by one of the two". The columns in which both are the same share one line.
  - Best for: pick the skill you want to train and the areas to look in. Every fight is tried
    in each attack stance of your weapon and the fights are ranked by what they give that
    skill, with the best stance named on each line.
  - History: the last ten scans are kept, also after the game was closed. Look at one again,
    table and details, without simulating anything. They are old numbers: the window says so.
    A scan of two setups is called "A against B" and shows both.
  - Enemy cards: in the game's combat areas menu, each enemy's card gets a small tag with
    the newest scan of that enemy made with what you have on now: Safe or Risky, then XP and
    deaths per hour. Safe: no deaths, and no hit of that enemy reaches your auto-eat point.
    A grey "Other setup" tag: that scan was made with other equipment, stance, spell, food or
    prayers. An enemy you have not simulated, or a dungeon's enemy, gets no tag. Click a tag
    to open its scan in the simulator. Opening the menu never simulates anything. Untick the
    box at the bottom of History to hide the tags.
  - Fight: the red button under the details starts the selected fight in the game (and
    switches to the spell of that result, and to its stance when one was asked for: a Best
    for line's, or the one chosen in the Stance box; with Compare ticked, the result of the
    setup on the left, and the text above it says which). Your equipment is left as it is,
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
  - Fights are random, but the dice of each fight are set from the fight and the setup: the
    same setup and the same character give the same numbers again. In Compare, A and B get
    the dice of the fight alone, so a setup's numbers there can differ a little from its
    numbers alone. The copy is your character as it is when you press Simulate, XP, slayer
    task and potions included; while you play these move on, so a scan a minute later can
    differ a little, and more for the enemy of your slayer task. The ranges say how far luck
    could have moved the numbers; longer fights narrow them.
  - An item whose requirements you do not meet cannot be equipped in the copy either; the
    result says so. It also says so when the copy wears something other than the slots show.
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
