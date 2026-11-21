# Melvor Idle 2 mods

Six quality-of-life mods for **Melvor Idle 2** (Steam, Windows). Each one is a separate download: take
the ones you want.

They are written in C# for the unofficial [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2),
use the game's own textures, fonts and colours so they look like part of the game, and never touch your
save. Tested on game version **0.11.6-alpha.1** with loader **0.1.3**. The game is in early access: an
update can break a mod until it is rebuilt.

## The mods

### Combat Simulator

What does an hour of this fight give me, what does it cost, and will I die? It does not re-create the
game's combat formulas: it loads a copy of your character into a second, invisible game and runs the
game's own combat on it, in the background. An hour of fighting takes a fraction of a second.

- Simulate an area, a dungeon or everything you have unlocked: kills, XP, gold, food, deaths, lowest health.
- Fight with one of your saved loadouts, or click any equipment slot to try another item (every item of
  the game that fits, with a search box) and see the difference.
- **Best for**: pick the skill you want to train; every unlocked fight is tried in each attack stance of
  your weapon and ranked.
- A Fight button starts the selected fight in the game.

![Combat Simulator](docs/img/combat-simulator.png)

### Item Sources

Hover "Found in" or "Used in" on an item in the bank: which enemy drops it and where that enemy lives,
which thieving target, hunting animal, chest, recipe, quest or shop purchase gives it, how many and how
likely; and every recipe, upgrade or purchase that uses it.

![Item Sources](docs/img/item-sources.png)

### Recipe Rates

XP per hour on every recipe of a skill's list (the best one in gold), and in the XP tooltip the whole
hour: XP, items made and used, what the products sell for, and how long your materials last. For
Smithing, Crafting, Fletching, Runecrafting, Beastcrafting, Cooking, Firemaking and Herblore.

![Recipe Rates](docs/img/recipe-rates.png)

### Combat Goals

The game's footer shows the time left to a level goal for one skill; combat trains several at once. A
small draggable, resizable window with the XP per hour of every combat skill you are training and the
time left to a level goal you set for each.

![Combat Goals](docs/img/combat-goals.png)

### Kills in Combat

The number of times you have killed the enemy you are fighting, next to its name.

![Kills in Combat](docs/img/kills-in-combat.png)

### Dungeon Completions

The number of times you have completed each dungeon, in the combat areas menu.

## Installing

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2): extract its zip into the
   game folder (Steam > Melvor Idle 2 > Manage > Browse local files).
2. Download the zips of the mods you want and extract each into the same game folder. A mod ends up as
   `mods\<Mod>\<Mod>.dll`.
3. Start the game. `MelvorModLoader.log` in the game folder lists the mods that were loaded.

To remove a mod, delete its folder under `mods`. Settings a mod keeps (a window's place and size, your
level goals) are in that folder too.

## Building from source

You need the game with the loader installed, and the .NET 10 SDK. The projects reference the game's
DLLs where the game is installed; pass `-p:GameDir="..."` when that is not
`D:\Steam\steamapps\common\Melvor Idle 2`.

```powershell
dotnet build mods\RecipeRates\RecipeRates.csproj -c Release   # builds into the game's mods folder; close the game first
.\tools\package.ps1                                           # one zip per mod in dist\, the game may stay open
```

| Folder | Contents |
| --- | --- |
| `mods/<Mod>` | One project per mod, with the `README.txt` that ships in its zip. |
| `mods/Shared` | `GameStyle.cs`: the game's look (textures, fonts, colours, small controls), and `GameWindow.cs`: a draggable, resizable window in the style of the game's own pop-ups. Compiled into every mod. |
| `third_party/MelvorModLoader` | The loader's source (MIT, by falorfrozen), kept here so it can be rebuilt after a game update. |
| `publish` | The page texts for Nexus Mods. |
| `tools` | Scripts for building, packaging and looking around the game while developing. |

### While developing

- `tools\dev-cycle.ps1 -Mod <Mod>[,<Mod>]` rebuilds in Debug, restarts the game through Steam and waits
  until the character is open.
- A Debug build of Combat Goals carries `Dev.cs`: it loads the only character by itself and writes theme
  dumps, scene tree dumps and screenshots into its mod folder, on start and on request. Other mods have a
  request file of their own in Debug builds (`sim.request`, `item.request`). Release builds contain none
  of it.
- `tools\ApiDump` lists the types and member signatures of the game's assembly that match a pattern:
  what an editor's auto-completion shows, as text.

No game files are in this repository, and none may be added.

## License

[MIT](LICENSE). The loader in `third_party` is MIT too, by its own author.

Unofficial and fan-made. Not affiliated with Games by Malcs or Jagex.
