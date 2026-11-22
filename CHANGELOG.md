# Changelog

From 0.3.0 on all the mods share one version, the repository's. Before that each mod had its own.

## Unreleased

- **Item Sources**: a tooltip kept open with Alt has a Make button for anything a skill makes from materials.
  Enter how many you want and it lists every step down to what you gather or find, under its skill: how many
  are needed, what your bank already covers, what is still to make or get, and how long each step takes at
  your current rate.
- **Astrology Overview**, a new mod: on the Astrology page, a round button opens one list of your
  constellations. Node choices ready to make come first, then the shortest study time to the next unlock,
  with what that unlock needs and, for a locked constellation, what it still misses. Click a row to open it.
- **Combat Simulator**: a button beside the X folds the window down to its title, as in the Item Encyclopedia,
  and the window remembers it. Folded, the title follows a scan ("Simulator · 4 of 12", then "Simulator · Done:
  12 fights"); opened again, the window has the same tab, list, sort and selected fight. After Cancel the window
  says how far the scan got ("Stopped: 4 of 12"), and in Best for the fight you clicked stays selected when a
  better stance of it comes in.

## 0.4.1

For Melvor Idle 2 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3 or newer (tried with 0.2.0).

- **Combat Simulator**: a long scan with many deaths no longer crashes the game. A simulated fight reached
  into the running game's music each time it started or the copy died; a From bank scan of several hours
  could close the game that way once the food was gone. Simulated fights now leave the music alone (they
  could also change the track you were hearing), and a From bank fight ends once you have died ten times in
  a row without food or a kill, with a note that says so.
- **Combat Simulator**: a Food/kill column, for when food is short: the fight with the smallest number makes
  your food last longest, and the first click on the column puts it on top. Details add food and healing per
  kill and how many kills the food in your bank is enough for, and "Compared with" says how food per kill
  changed. A From bank fight now says when the food ran out, and how many kills it had made by then.
- **Combat Simulator**: the first click on Deaths puts the fights you survive on top. A long note under
  "Other stances" keeps its beginning: it is written on two lines. The window stays above the game's bar
  along the bottom of the screen and is never larger than the room above it.
- **Combat Simulator**: fights with the same number in the sorted column stand with the better XP first, and
  a fight that could not be simulated stays at the bottom whichever way the list is sorted.
- **Item Sources** and **Item Encyclopedia**: rest the pointer on a chance under "Found in" and a small
  tooltip says what it means in kills or tries: "Half the time within 1,386 kills. 9 times in 10 within
  4,605." In Item Sources the list now stays open while you move from "Found in" onto it, in the bank and on
  a tooltip kept open with Alt.
- The small tooltips in the mods' windows (the words that appear when the pointer rests on a button or a
  name) now sit on the game's dark tooltip panel. They were bare words on top of whatever was under them.

## 0.4.0

For Melvor Idle 2 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3 or newer (tried with 0.2.0). A seventh mod.

- **Item Encyclopedia**, a new mod: every item of the game in one window (F9, or the round button with the
  book on the right edge of the screen). Search, sort, filter by kind, slot, combat style, weapon, skill and
  where an item comes from, down to one area or dungeon, and read everything about an item: the game's own
  item panel, how many you have, every enemy, recipe, chest and purchase that gives it, and everything it is
  used in. Click an ingredient to open it; Back returns. A button beside the X folds the window down to its
  title, to put it aside without closing it, and the filters keep your last five searches one click away.

## 0.3.0

For Melvor Idle 2 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3. The result of reading every line of the six
mods before the repository was made public: no new features, many things made right.

### Combat Simulator

- A scan that was cut off by opening another character could show its results under the new character.
- Opening the window again shows the equipment, loadouts, spells and food of that moment, not those of the
  first time it was opened.
- A cancelled fight is no longer listed, ranked and kept in the history as if it had been run.
- The snapshot is only taken from a save the game wrote at that moment; an older file is refused.
- "Unlimited" supplies cover food that is selected but nearly used up; "From bank" no longer hands a swapped
  pile of arrows more than you own.
- The Fight button checks that the area is unlocked for this character, and its note says exactly what it
  will do.
- A run stops by itself, with a message, should a game version keep the simulated copies in memory.
- An error that repeats is written to the log once, not six times a second.

### Item Sources

- A tooltip kept open with Alt no longer stays on the screen after going back to the main menu.
- Alt+Tab, Alt+Enter and AltGr no longer count as the Alt key: it is Alt by itself, when it is let go.
- A long list is cut off cleanly, saying how much was left out.
- When the game's data cannot be read, the tooltip says so instead of "Reading..." for ever.

### Combat Goals

- After a game update that breaks it, it stops with one line in the log instead of writing an error twice a
  second for as long as the game runs.
- Settings are saved once a second at most, and written whole: a crash can no longer leave a broken file,
  and a file that cannot be read is set aside instead of being overwritten.
- A level cap that rises while the window is up is followed.

### All mods

- Work that happens "a few times a second" is timed by the clock, not by counting frames, so it costs the
  same on a fast machine.
- The counters say "once", "twice", "12 times".
- The log names no character.

### For people who build the mods

- One version and one build file for all the mods (`mods/Directory.Build.props`); the game folder comes from
  `Local.props` instead of a path in every project.
- The development helpers are behind `-p:DevTools=true`. A plain build, Debug or Release, is the mod as
  players get it: it no longer presses "Load" by itself or writes pictures.
- The scripts take `-Mod A,B` as written in their own examples, find the game the same way the builds do,
  and `package.ps1` also makes one zip of all the mods.

Not tried in the game for this release: the tags of Kills in Combat and Dungeon Completions (the combat
screen was not open during the test run), cancelling a scan half way, and the simulator's "From bank"
supplies and Fight button. The six mods load, the simulator, the item tooltips and the kept-open tooltip
were run.

## 0.2.0

For Melvor Idle 2 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3. Combat Simulator and Item Sources are new
versions; the other four mods are unchanged at 0.1.0.

### Item Sources 0.2.0

- **Fixed, important**: hovering "Found in" or "Used in" on the item selected in the bank showed nothing.
  The bank hands its panel a stack of items rather than an item, and only the item was looked at. (The
  pictures of 0.1.0 were made through a development shortcut that did not go this way.)
- **Alt keeps a tooltip open**: press Alt while the game's item tooltip is up, anywhere in the game, and a
  copy of it stays where it is, with rows that can be hovered like the ones in the bank. Alt again, Esc or
  a click elsewhere closes it.
- The list is redrawn the way the game draws its own item tooltip: the name with a small tag, the fading
  line, groups with their skill's icon, and no wider than what it holds.
- It opens beside the whole item panel instead of over its icon.

### Combat Simulator 0.2.0

- **Where** is a list you tick: one area, several, a whole category, or nothing for everything. "Best for"
  uses it too, so a search can be kept to the areas you care about.
- **Spells**: when the weapon casts spells, choose which one to fight with; two spells are compared like two
  sets of equipment. With unlimited supplies the copy is handed the runes.
- **History**: the last ten scans of a character are kept on disk and can be looked at again, table and
  details, without simulating anything.
- **Faster**: up to four fights are simulated side by side (about one and a half times as fast; `Workers` in
  `settings.json` changes it).
- **Fixed, important**: every simulated fight left about 120 MB of memory behind until the game was closed,
  so a scan of everything could use tens of gigabytes. The game's settings kept each copy alive; the copy's
  subscriptions are now removed when its fight is over. Measured: 152 fights now grow the game by under
  1 GB, where 38 used to add 5 GB.
- A weapon with a single attack stance is no longer simulated "per stance" in Best for, and such results are
  shared between the two modes.
- Dungeons were run for the first time (two dungeons, an hour each) and give sensible numbers.
- The Fight button also selects the spell of the result, when the result was simulated with what is worn.

## 0.1.0

First release. For Melvor Idle 2 0.11.6-alpha.1 with Melvor Mod Loader 0.1.3.

- **Combat Simulator 0.1.0**: simulates fights by running the game's own combat on a copy of the character.
  Areas, dungeons or everything unlocked; saved loadouts and per-slot item swaps with a search; a "Best for"
  scan that ranks every unlocked fight for a skill; a comparison with the same fight in other equipment; a
  button that starts the selected fight.
- **Item Sources 0.1.0**: on the bank's item panel, what is behind "Found in" and "Used in": enemies and
  where they live, recipes, chests, thieving targets, shop purchases, with quantities and chances.
- **Recipe Rates 0.1.0**: XP per hour on every recipe of a skill's list, and the whole hour in the XP
  tooltip.
- **Combat Goals 0.1.0**: XP per hour and time to a level goal for every combat skill being trained.
- **Kills in Combat 0.1.0**: the kill count of the current enemy next to its name.
- **Dungeon Completions 0.1.0**: the completion count of each dungeon in the combat areas menu.

Known gaps in this release: the simulator's dungeon runs, its "from bank" supplies and the Fight button
were built and rendered but not exercised; the simulator's numbers were compared with the game once
(4,188 XP per hour simulated against 4,125 on the game's ten minute average).
