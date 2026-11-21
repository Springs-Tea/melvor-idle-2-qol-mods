# Contributing

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome.

## Reporting a problem

Open an [issue](https://github.com/Springs-Tea/melvor-2-mods/issues/new/choose) and include:

- which mod, and its version (the loader writes it to the log: `Mod loaded: RecipeRates 0.1.0`),
- the game version (top of the screen in the game) and the loader version,
- `MelvorModLoader.log` from the game folder. It is rewritten on every start, so copy it right after the
  problem happened.

A screenshot helps a lot when something looks wrong.

## Setting up

You need:

- Melvor Idle 2 with the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) installed,
- the [.NET 10 SDK](https://dotnet.microsoft.com/download).

The projects compile against the game's own DLLs, found through the `GameDir` property. It defaults to
`D:\Steam\steamapps\common\Melvor Idle 2`; pass your own when it differs:

```powershell
dotnet build mods\RecipeRates\RecipeRates.csproj -c Debug -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\Melvor Idle 2"
```

A build writes the DLL straight into the game's `mods` folder. The DLL is locked while the game runs, so
close the game first, or use the script that does all of it:

```powershell
.\tools\dev-cycle.ps1 -Mod RecipeRates      # closes the game, builds in Debug, starts it again, waits for the character
```

Use a test character while you develop: a mod runs inside the real game.

## How the code is laid out

- `mods/<Mod>` is one mod: a project file, its code, a `README.md` (for people browsing the repository) and a
  `README.txt` (ships in the zip).
- `mods/Shared` is compiled into every mod. `GameStyle.cs` is the game's look; `GameWindow.cs` is a window in
  the style of the game's pop-ups.
- A mod is a public class that implements the loader's `IMod`. The loader's
  [wiki](https://falorfrozen-cmd.github.io/MelvorModLoader/) explains the API and has a map of the game's
  classes.

To see what the game offers, `tools/ApiDump` lists the types and member signatures of the game's assembly
that match a pattern:

```powershell
dotnet run --project tools\ApiDump -- "CombatAreas|EnemyKilled"
```

## What a change should keep to

These are what make the mods feel like part of the game and safe to install.

1. **Look like the game.** Take textures, fonts, colours and button styles from `GameStyle`; add to it when
   something is missing rather than hard-coding a look in one mod. Compare a screenshot with the game's own
   screens before you call it done.
2. **Never touch the save.** No mod writes save files, gives items or XP, or changes a number the game
   keeps. Read the game; let the player act.
3. **Use the game instead of re-creating it.** Prefer the game's own values, tables and methods to formulas
   of your own: they already contain every bonus, and they stay right after a balance change.
4. **Fail quietly, and say so.** A private member that is gone after a game update should end in one clear
   line in the log and the mod switching itself off, never in a broken game. Keep the use of private members
   (`[UnsafeAccessor]`) small and in one place.
5. **Hands off during offline progress.** The loader raises no `Frame` then; your own timers and signals must
   check `GameAccess.IsComputingOffline`.
6. **Stay cheap.** `Frame` runs sixty times a second for every mod. Walk the scene tree or the item list a
   few times a second at most.
7. **Keep your files in your folder.** Settings go into `ctx.Directory`, so deleting the mod's folder removes
   everything.
8. **No game files.** Never commit anything from the game's folder (DLLs, the package, textures, saves).
   References to the game's assemblies stay `<Private>false</Private>`. A check on every pull request
   enforces this.

## Sending a pull request

- One change per pull request, with a description of what it does and how you tried it.
- Say what you could not try. "Built and loaded, but I do not have a dungeon unlocked to see it" is useful.
- Add a picture when the change is visible.
- Development helpers (request files, screenshots, dumps) belong inside `#if DEBUG`; a Release build must
  not contain them.
- When you add a mod: its folder with both readmes, a line in the table of the main `README.md`, and a page
  text in `publish/nexus`.

The pictures at the top of the readmes (the banner and the paper titles) are drawn by `tools/make-art.py`;
add a title there and run it again rather than editing the SVG files by hand.

## Releasing (maintainers)

See [publish/README.md](publish/README.md).
