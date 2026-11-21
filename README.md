<h1 align="center">
  <img src="docs/img/art/banner.svg" alt="Melvor Idle 2 Mods: six quality-of-life mods in the game's own look. Separate downloads. Your save is never touched." width="900">
</h1>

<p align="center">
  <img alt="Game version" src="https://img.shields.io/badge/Melvor%20Idle%202-0.11.6--alpha.1-8b5cf6">
  <img alt="Loader" src="https://img.shields.io/badge/Melvor%20Mod%20Loader-0.1.3%2B-2baae1">
  <img alt="Platform" src="https://img.shields.io/badge/Steam-Windows-41454c">
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-cd964f"></a>
</p>

<p align="center">
  <img src="docs/img/combat-simulator-compare.png" alt="The Combat Simulator" width="880">
</p>

<h2 align="center"><img src="docs/img/art/h-the-mods.svg" alt="The mods" width="720"></h2>

Take the ones you want. Each has a page of its own with pictures and details.

<table>
  <tr>
    <td width="330"><a href="mods/CombatSim"><img src="docs/img/combat-simulator-best.png" alt="Combat Simulator"></a></td>
    <td>
      <h3><a href="mods/CombatSim">Combat Simulator</a></h3>
      What an hour of each fight gives, uses and risks, worked out by running the game's own combat on a
      hidden copy of your character. Compare saved loadouts, swap any item in, find the best place to
      train a skill, then start the fight with one click.
    </td>
  </tr>
  <tr>
    <td><a href="mods/ItemSources"><img src="docs/img/item-sources.png" alt="Item Sources"></a></td>
    <td>
      <h3><a href="mods/ItemSources">Item Sources</a></h3>
      Hover "Found in" or "Used in" on an item: which enemy drops it and where it lives, how many and how
      likely, and every recipe, chest, thieving target or shop purchase that gives or uses it.
    </td>
  </tr>
  <tr>
    <td><a href="mods/RecipeRates"><img src="docs/img/recipe-rates.png" alt="Recipe Rates"></a></td>
    <td>
      <h3><a href="mods/RecipeRates">Recipe Rates</a></h3>
      XP per hour on every recipe of a skill's list, the best one in gold. In the XP tooltip the whole
      hour: XP, items made and used, what the products sell for, and how long your materials last.
    </td>
  </tr>
  <tr>
    <td><a href="mods/CombatGoals"><img src="docs/img/combat-goals.png" alt="Combat Goals"></a></td>
    <td>
      <h3><a href="mods/CombatGoals">Combat Goals</a></h3>
      Combat trains several skills at once; the game's footer shows a level goal for one. A small
      draggable, resizable window with the XP per hour of each and the time left to a goal you set.
    </td>
  </tr>
  <tr>
    <td><a href="mods/KillsInCombat"><img src="docs/img/kills-in-combat.png" alt="Kills in Combat"></a></td>
    <td>
      <h3><a href="mods/KillsInCombat">Kills in Combat</a></h3>
      The number of times you have killed the enemy you are fighting, next to its name.
    </td>
  </tr>
  <tr>
    <td align="center"><i>same small tag,<br>on every dungeon</i></td>
    <td>
      <h3><a href="mods/DungeonCompletions">Dungeon Completions</a></h3>
      The number of times you have completed each dungeon, in the combat areas menu.
    </td>
  </tr>
</table>

<h2 align="center"><img src="docs/img/art/h-install.svg" alt="Install" width="720"></h2>

The mods run on the unofficial [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2). Everything
is a plain zip that you extract into the game folder: no installer, nothing outside that folder.

1. **Open the game folder**: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
2. **Install the loader**: extract its zip there. `override.cfg` ends up next to `Melvor2.exe`.
3. **Add the mods you want**: extract each zip from [Releases](https://github.com/Springs-Tea/melvor-2-mods/releases) into the same folder.
4. **Start the game** from Steam. A green line in the top left corner says how many mods were loaded.

```
Melvor Idle 2\
├─ Melvor2.exe
├─ override.cfg                                   the loader
├─ data_MelvorModLoader_windows_x86_64\           the loader
└─ mods\
   ├─ CombatSim\CombatSim.dll
   ├─ ItemSources\ItemSources.dll
   └─ ...                                         one folder per mod
```

**To remove a mod**, delete its folder under `mods`. Its settings (a window's place and size, your level
goals) are in that folder too, so nothing is left behind.

<h2 align="center"><img src="docs/img/art/h-questions.svg" alt="Questions" width="720"></h2>

<details>
<summary><b>Is my save safe?</b></summary>

Yes. No mod writes to the save, and none adds or removes anything in the game. The Combat Simulator works
on a copy of the character that exists only in memory and is thrown away after every fight. The one button
that acts on the game, "Fight", does what the game's own area menu does: it starts a fight.
</details>

<details>
<summary><b>Do they make the game easier?</b></summary>

They show information the game already has (drop tables, recipe values, statistics) or works out the same
way (the simulator runs the game's own combat). They give no items, XP or bonuses.
</details>

<details>
<summary><b>What happens after a game update?</b></summary>

The game is in early access and the loader is unofficial, so an update can break a mod until it is rebuilt.
A mod that cannot find what it needs stops itself and says so in `MelvorModLoader.log`; the game and the
other mods keep running. If the game does not start at all, delete `override.cfg` to play without mods.
</details>

<details>
<summary><b>Something is wrong. What do you need?</b></summary>

Open an [issue](https://github.com/Springs-Tea/melvor-2-mods/issues) with the mod's name, the game version, and `MelvorModLoader.log` from the
game folder (it is rewritten on every start).
</details>

<h2 align="center"><img src="docs/img/art/h-how-they-are-built.svg" alt="How they are built" width="720"></h2>

Every mod is a small C# class library that runs inside the game, compiled against the game's own assembly.

```mermaid
flowchart TB
    subgraph game["Melvor Idle 2"]
        L["Melvor Mod Loader<br/>loads every DLL under mods"]
    end
    subgraph repo["This repository"]
        S["mods/Shared<br/>GameStyle · GameWindow"]
        M1["CombatSim"]
        M2["ItemSources"]
        M3["RecipeRates"]
        M4["CombatGoals"]
        M5["KillsInCombat"]
        M6["DungeonCompletions"]
    end
    S --> M1 & M2 & M3 & M4 & M5 & M6
    M1 & M2 & M3 & M4 & M5 & M6 -->|"one DLL each"| L
```

- **They look like the game because they use the game.** `mods/Shared/GameStyle.cs` loads the game's own
  textures, fonts and colours at runtime; nothing is copied into a mod. `GameWindow.cs` is a draggable,
  resizable window in the style of the game's pop-ups.
- **They read the game instead of re-creating it.** The simulator runs the game's combat; Item Sources reads
  the game's drop tables; Recipe Rates uses the game's own values for a recipe, bonuses included.

| Folder | Contents |
| --- | --- |
| `mods/<Mod>` | One project per mod: its code, a `README.md` for people and the `README.txt` that ships in the zip. |
| `mods/Shared` | The game's look, compiled into every mod. |
| `third_party/MelvorModLoader` | The loader's source (MIT, by falorfrozen), kept so it can be rebuilt after a game update. |
| `publish` | The release checklist and the page texts for Nexus Mods. |
| `tools` | Scripts for building, packaging and looking around the game while developing. |
| `docs/img` | The pictures on these pages. |

<h2 align="center"><img src="docs/img/art/h-building.svg" alt="Building from source" width="720"></h2>

You need the game with the loader installed, and the [.NET 10 SDK](https://dotnet.microsoft.com/download).
The projects reference the game's DLLs where the game is installed; pass `-p:GameDir="..."` when that is not
`D:\Steam\steamapps\common\Melvor Idle 2`.

```powershell
# one mod, straight into the game's mods folder (close the game first: the DLL is locked while it runs)
dotnet build mods\RecipeRates\RecipeRates.csproj -c Release

# every mod as a release zip in dist\ (the game may stay open)
.\tools\package.ps1
```

Want to change something or add a mod? See [CONTRIBUTING.md](CONTRIBUTING.md). What changed between
releases is in the [changelog](CHANGELOG.md), and what the mods do and do not do on your computer is in
[SECURITY.md](SECURITY.md).

<h2 align="center"><img src="docs/img/art/h-credits.svg" alt="Credits and license" width="720"></h2>

- [Melvor Mod Loader](https://github.com/falorfrozen-cmd/MelvorModLoader) by falorfrozen makes mods for this
  game possible. Its wiki is where to start if you want to write one.
- Melvor Idle 2 is made by Games by Malcs and published by Jagex. This project is unofficial and fan-made,
  and is not affiliated with either. No game files are in this repository.

The code is under the [MIT license](LICENSE).
