# Publishing

The mods are distributed on [Nexus Mods](https://www.nexusmods.com/games/melvoridle2/mods), where the Melvor Mod
Loader and the other Melvor Idle 2 mods live. Each mod is a page of its own, so players take the ones they want.

## Before a release

1. Set `<Version>` in the mod's project file and the version at the top of its `README.txt`.
2. `.\tools\package.ps1` builds every mod in Release and writes one zip per mod into `dist\`. A zip holds
   `mods\<Mod>\<Mod>.dll` and `README.txt`, nothing else: never the game's, Godot's or the loader's files.
3. Try the Release build in the game (the Debug builds carry development helpers).

## A mod's page on Nexus

The texts are in `nexus/<Mod>.txt`: name, summary and description, ready to paste.

1. Add a mod to the Melvor Idle 2 section and paste the name, the summary and the description.
2. Upload `dist\<Mod>-<version>.zip` as the main file, with the same version number.
3. Under **Requirements**, add **Melvor Mod Loader** (minimum 0.1.3) as a required file.
4. Add the picture from `docs/img` (Dungeon Completions has none yet).
5. Under permissions, link this repository as the source.

After the first mod is out, the loader's Discord lists new mods on its wiki.

## On GitHub

Attach the same zips to a release, so the repository alone is enough to install from.
