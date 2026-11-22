# For coding agents

This page is for AI coding agents working in this repository, and for people who want the short version.
The full guide is [CONTRIBUTING.md](CONTRIBUTING.md). Where the two disagree, CONTRIBUTING.md is right and
this page needs fixing.

## What this is

Ten mods for the game Melvor Idle 2 (Godot 4, C#, .NET 10), loaded by the unofficial Melvor Mod Loader. A
mod is one DLL built from `mods/<Mod>`; the code in `mods/Shared` is compiled into every one of them. They
show what the game already knows (rates, sources, counts, a combat simulation). They never give items or
XP and never change the save.

## What you can check, and what you cannot

- **The mods do not build without the game.** The projects compile against Melvor Idle 2's own DLLs, in the
  folder `GameDir` names (`Local.props`, or the usual Steam folder). On a machine without the game,
  `dotnet build` fails, and that is not a bug to fix. The checks on GitHub do not build them either.
- **Nothing here runs the game for you.** There are no unit tests: a mod is tried inside the real game. If
  you could not do that, say so in plain words. Never write "tested" for something you only read.
- **What always works**, with PowerShell 7 and Python 3 and nothing else:

  ```powershell
  pwsh tools/check-repo.ps1      # the same rules the pull request check enforces
  python tools/make-art.py       # redraws docs/img/art; the check fails when the folder differs from it
  ```

- **With the game installed** (close it first, a mod's DLL is locked while it runs):

  ```powershell
  dotnet build mods\RecipeRates\RecipeRates.csproj     # builds into the game's mods folder
  .\tools\restart-game.ps1 -Mod RecipeRates            # closes the game, builds, starts it, prints the log
  .\tools\dev-cycle.ps1 -Mod RecipeRates               # the same with the development helpers compiled in
  dotnet run --project tools\ApiDump -- "CombatAreas"  # the game's types and members matching a pattern
  ```

  `MelvorModLoader.log` in the game folder is where a mod says what it did and what went wrong.

## Rules that are never broken

1. **No game files, ever.** Nothing from the game's folder goes into a commit: no DLL, package, scene,
   texture, font, sound, save or log. Do not paste decompiled game code either; name the type and member
   instead. References to the game's assemblies stay `<Private>false</Private>`.
2. **Never touch the save.** No mod writes save files, gives items or XP, or changes a number the game keeps.
3. **Use the game instead of re-creating it.** Take values, tables and methods from the game, not formulas
   of your own. Take textures, fonts and colours from `mods/Shared/GameStyle.cs`, not hard-coded ones.
4. **Fail quietly, and say so.** A game member that is gone after an update ends in one clear line in the log
   and the mod switching itself off, never in a broken game or an error on every frame.
5. **Stay cheap.** `Frame` runs on every frame. Walk the scene tree or the item list a few times a second at
   most, by the clock (`Tempo` in `mods/Shared/ModFiles.cs`), never by counting frames.
6. **A mod's files stay in its folder** (`ctx.Directory`). No network requests.
7. **Development helpers stay inside `#if DEVTOOLS`.** A build a player could get contains none of them.
8. **Nothing personal in a commit.** No path into a user folder, no Steam id, no `Local.props`, nothing from
   `dist/`. The check looks for these.

## Where things are

| Path | What it is |
| --- | --- |
| `mods/<Mod>/` | One mod: an empty project file named like the mod, its code, `README.md` (the guide on GitHub and, converted, on Nexus Mods) and `README.txt` (ships in the zip). |
| `mods/Shared/` | `GameStyle.cs` (the game's look), `GameWindow.cs` (a window like the game's pop-ups), `ModFiles.cs` (settings written safely, `Tempo`), `ItemIndex.cs` and `ItemLines.cs` (where every item comes from and goes to, for Item Sources and the Item Encyclopedia). |
| `mods/Directory.Build.props` | Everything the builds share, and the one version number. |
| `third_party/MelvorModLoader/` | The loader's source at 0.1.3, to read. Not ours: do not edit it and do not replace it with a newer version, whose license no longer allows copying. |
| `tools/` | Scripts: build and restart, packaging, the repository check, the drawn art, the Nexus page texts. Each says what it does in its first lines. |
| `publish/` | The release checklist and the page texts for Nexus Mods. |
| `docs/img/` | The pictures of the readmes. `docs/img/art/*.svg` is drawn by `tools/make-art.py`: change the script, not the files. |
| `CHANGELOG.md` | What players notice, newest first. |

## How the text is written

Everything in the repository is in English: code, comments, readmes, commit messages, what a mod shows.
Write plainly, in whole sentences, the way the existing pages do: say what a thing does for the player,
not how clever it is. A comment says why, or what is not obvious; it does not repeat the line under it.
`.editorconfig` has the formatting (four spaces, braces on their own line, Markdown lines up to 110).
Words a player reads in the game follow the game's own wording and number formats.

## Before you say a change is done

- `pwsh tools/check-repo.ps1` passes.
- If a title, a button or a version on a drawn picture changed: `python tools/make-art.py` was run.
- The mod's `README.md` and `README.txt` say the same as the code. A mod's `README.md` is also its page on
  Nexus Mods, so it is written for players.
- What a player would notice is one line under `## Unreleased` at the top of `CHANGELOG.md` (add the heading
  if it is not there). Leave the version number alone: it changes at a release.
- You have written down what was tried in the game, on which game and loader version, and what was not.

## Issues and pull requests

- `main` changes only through pull requests, for maintainers too. Work on a branch named after the change
  (`recipe-rates/sell-price`), one change per pull request, and fill in the template: what a player sees,
  how it was tried, what could not be tried, pictures when it is visible.
- The check on a pull request is `Repository checks`. It guards the rules above; it cannot tell whether the
  mod works. A maintainer builds and tries a change in the game before merging it.
- When you open an issue for someone, use the forms under `.github/ISSUE_TEMPLATE` and fill every field
  they ask for, under the same headings. A problem: Mod, What happened, What you expected, Game version,
  Mod and loader version, Log. An idea: Mod, The situation, The idea, Today. One problem or idea per issue,
  after a search through the open ones. Issue #2 is a filled-in idea.
- Say that an agent wrote the issue or the pull request, and who asked for it.
- Something that could harm a player is not a public issue: see [SECURITY.md](SECURITY.md).
- Do not tag, release, publish to Nexus Mods or change repository settings. Those are the maintainer's.
