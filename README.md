# melvor-2-mods

Quality-of-life mods for **Melvor Idle 2** (Steam, Windows), written in C# for the unofficial
[Melvor Mod Loader](https://github.com/falorfrozen-cmd/MelvorModLoader). Tested on game version
0.11.6-alpha.1. The game is in early access: an update can break a mod until it is rebuilt.

Every mod uses the game's own textures, fonts and colours, so it looks like part of the game. None
of them changes the save or makes the game easier.

| Mod | What it does |
| --- | --- |
| [Combat Goals](mods/CombatGoals) | A draggable, resizable window with the XP per hour of every combat skill you are training and the time left to a level goal you set for each. |
| [Kills in Combat](mods/KillsInCombat) | The number of times you have killed the current enemy, next to its name. |
| [Dungeon Completions](mods/DungeonCompletions) | The number of times you have completed each dungeon, in the combat areas menu. |
| [Recipe Rates](mods/RecipeRates) | XP per hour on every recipe of a skill's list, and in the XP tooltip the whole hour: XP, items made and used, sell value, and how long your materials last. |
| [Combat Simulator](mods/CombatSim) | What an hour of each fight gives, uses and risks, worked out by running the game's own combat on a hidden copy of your character. Pick a saved loadout or swap any item in to compare, or let it find the best place to train a skill. |
| [Item Sources](mods/ItemSources) | Hover "Found in" or "Used in" on an item: which enemy drops it and where, how many and how likely, which recipe, chest, thieving target or shop purchase gives or uses it. |

## Installing

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (extract its zip into
   the game folder).
2. Extract a mod's zip into the game folder, so that it ends up as `mods\<Mod>\<Mod>.dll`.
3. Start the game. `MelvorModLoader.log` in the game folder lists the mods that were loaded.

To remove a mod, delete its folder under `mods`.

## Repository

| Folder | Contents |
| --- | --- |
| `mods/<Mod>` | One project per mod, with the `README.txt` that ships in its zip. |
| `mods/Shared` | `GameStyle.cs`: the game's look (textures, fonts, colours, small controls), and `GameWindow.cs`: a draggable, resizable window in the style of the game's own pop-ups. Compiled into every mod. |
| `third_party/MelvorModLoader` | The loader's source (MIT, by falorfrozen), kept here so it can be rebuilt after a game update. |
| `tools` | Scripts for building, packaging and looking around the game while developing. |

## Building

You need the game with the loader installed, and the .NET 10 SDK. The projects reference the game's
DLLs where the game is installed; pass `-p:GameDir="..."` when that is not
`D:\Steam\steamapps\common\Melvor Idle 2`.

```powershell
dotnet build mods\RecipeRates\RecipeRates.csproj -c Release   # builds into the game's mods folder; close the game first
.\tools\package.ps1                                           # one zip per mod in dist\, the game may stay open
```

### While developing

- `tools\dev-cycle.ps1 -Mod <Mod>[,<Mod>]` rebuilds in Debug, restarts the game through Steam and waits
  until the character is open.
- A Debug build of Combat Goals carries `Dev.cs`: it loads the only character by itself and writes theme
  dumps, scene tree dumps and screenshots into its mod folder, on start and on request (create a file
  `shot.request`, `probe.request` or `areas.request` there). Release builds contain none of it.
- `tools\ApiDump` lists the types and member signatures of the game's assembly that match a pattern:
  what an editor's auto-completion shows, as text.

No game files are in this repository, and none may be added.

Unofficial and fan-made. Not affiliated with Games by Malcs or Jagex.
