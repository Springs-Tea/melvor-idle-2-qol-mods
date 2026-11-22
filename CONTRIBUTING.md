# Contributing

Thanks for wanting to help. Bug reports, ideas and pull requests are all welcome, from people and from the
coding agents that work for them. An agent should read [AGENTS.md](AGENTS.md) first: it is this page cut
down to what must be known before touching anything.

## Reporting a problem, or bringing an idea

[Open an issue](https://github.com/Springs-Tea/melvor-idle-2-qol-mods/issues/new/choose) and pick the form that fits. Each asks for what
is needed to act on it, so nobody has to come back with questions:

| Form | For | It asks for |
| --- | --- | --- |
| **Something is wrong** | A wrong number, a broken look, a mod that does not load. | The mod, what happened, what you expected, the game, mod and loader versions, the log. |
| **Broke after a game update** | The game got a new version and a mod stopped working. | The new game version, the mods affected, what you see, the log. |
| **An idea** | Something a mod could do better, or a new mod. | The mod, the situation in the game, the idea, how you manage today. |

Things that make a report easy to act on:

- **One problem or one idea per issue**, and a look through the open ones first.
- **`MelvorModLoader.log`** from the game folder. It is rewritten on every start, so copy it right after the
  problem happened. It has the versions (`Melvor Mod Loader 0.2.0 starting`, `Mod loaded: RecipeRates 0.3.0`)
  and, when a mod gave up, the reason.
- **A screenshot** when something looks wrong.
- **For an idea, the situation before the solution.** "I never know which enemy to train Ranged on" says
  more than "add a table". [Issue #2](https://github.com/Springs-Tea/melvor-idle-2-qol-mods/issues/2) is a
  filled-in example.
- The mods only show and help: an idea that needs a mod to give items or XP, or to change the save, cannot
  be taken up.

Something that could harm a player is not for a public issue: see [SECURITY.md](SECURITY.md).

## Setting up

You need:

- Melvor Idle 2 with the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) installed,
- the [.NET 10 SDK](https://dotnet.microsoft.com/download),
- PowerShell 7 for the scripts under `tools`.

The projects compile against the game's own DLLs, in the folder the `GameDir` property names. It defaults to
the usual Steam folder, `C:\Program Files (x86)\Steam\steamapps\common\Melvor Idle 2`. When your game is
somewhere else, copy `Local.props.example` to `Local.props` (git ignores it) and set the folder there: the
builds, the scripts and `tools/ApiDump` all read it.

```powershell
dotnet build mods\RecipeRates\RecipeRates.csproj
```

A build writes the DLL straight into the game's `mods` folder. The DLL is locked while the game runs, so
close the game first, or let a script do the closing and the starting:

```powershell
.\tools\restart-game.ps1 -Mod RecipeRates   # closes the game, builds, starts it again
.\tools\dev-cycle.ps1 -Mod RecipeRates      # the same with the development helpers, and waits for the character
```

The development helpers (`#if DEVTOOLS`) are what make a round possible with nobody at the keyboard: Combat
Goals' helper presses "Load" on the main menu, and several mods take requests through files in their folder
and answer with a picture or a dump (see the comments at the top of `mods/CombatGoals/Dev.cs`,
`tools/dev-cycle.ps1` and `tools/sim-shot.ps1`). Only `dev-cycle.ps1` compiles them in; a plain build, Debug
or Release, is the mod as players get it.

Use a test character while you develop: a mod runs inside the real game.

## How the code is laid out

- `mods/<Mod>` is one mod: a project file, its code, a `README.md` (for people browsing the repository) and a
  `README.txt` (ships in the zip).
- `mods/Shared` is compiled into every mod. `GameStyle.cs` is the game's look; `GameWindow.cs` is a window in
  the style of the game's pop-ups; `ModFiles.cs` reads and writes a mod's settings safely and has `Tempo`,
  for work that should happen a few times a second; `ItemIndex.cs` and `ItemLines.cs` work out, from the
  game's data, where every item comes from and what it is used in.
- `mods/Directory.Build.props` holds everything the mods' builds share, including the one version number.
  A mod's own project file is empty: its name is the mod's name.
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
6. **Stay cheap.** `Frame` runs on every frame for every mod. Walk the scene tree or the item list a few
   times a second at most, by the clock (`Tempo`) and not by counting frames: a frame rate is not a tempo.
7. **Keep your files in your folder.** Settings go into `ctx.Directory`, so deleting the mod's folder removes
   everything.
8. **No game files.** Never commit anything from the game's folder (DLLs, the package, textures, saves).
   References to the game's assemblies stay `<Private>false</Private>`. A check on every pull request
   enforces this.

## How a change gets in

`main` changes only through pull requests. That holds for the maintainer too: a rule on the branch refuses
a direct push, a forced push and deleting it.

1. Fork the repository and make a branch from `main`, named after the change: `recipe-rates/sell-price`.
2. Make the change and run `.\tools\check-repo.ps1`. It is what the check on GitHub runs.
3. Open a pull request against `main` and fill in the template.
4. The check, **Repository checks**, has to pass. It guards the rules of the repository (no game files,
   complete mods, working links, art that matches its script). It cannot build or run a mod, because the
   game's DLLs are not on GitHub, so it cannot tell whether your change works.
5. That is why a maintainer builds the branch and tries it in the game before merging. What you write under
   "How I tried it" decides how quickly that goes.
6. It is merged as one commit. Releases, version numbers, tags and the pages on Nexus Mods are the
   maintainer's: leave the version alone.

## Sending a pull request

- One change per pull request, with a description of what it does and how you tried it: the game and loader
  version, and what you did in the game.
- Say what you could not try. "Built and loaded, but I do not have a dungeon unlocked to see it" is useful.
  Never write "tested" for something that was only read.
- Add a picture when the change is visible.
- When players would notice the change, add one line under `## Unreleased` at the top of `CHANGELOG.md` (add
  the heading if it is not there), written for players, and keep the mod's `README.md` and `README.txt` in
  step with the code. A mod's `README.md` is also its page on Nexus Mods.
- If an AI agent wrote some or all of it, say so, and say what a person looked at in the game.
- Development helpers (request files, screenshots, dumps) belong inside `#if DEVTOOLS`; no build that a
  player could get contains them.
- When you add a mod: a folder named like the mod with an empty project file of the same name, both
  readmes, a line in the table of the main `README.md`, a page text in `publish/nexus`, and its name in
  `tools/make-art.py` (the banner's list and the title of its page). `README.txt` and the page text say
  `{version}` where the version goes. Also: its line in `publish/nexus/Pack.txt`, its entry in
  `tools/nexus-headers.py` and in `PAGES` of `tools/nexus-pages.py` (`None` until its page exists), the mod
  lists of the three forms in `.github/ISSUE_TEMPLATE`, and every place that counts the mods.

The drawn pictures of the readmes (the banner, the paper titles, the buttons, the tags under the banner)
come from `tools/make-art.py`; change a title or a version there and run it again rather than editing the
SVG files by hand. The checks run the script and fail when `docs/img/art` is not what it draws.

## Releasing (maintainers)

See [publish/README.md](publish/README.md).
