<h1 align="center"><img src="../../docs/img/art/h-item-sources.svg" alt="Item Sources" width="720"></h1>

<p align="center"><a href="../../README.md">All mods</a> · <a href="https://github.com/Springs-Tea/melvor-2-mods/releases">Download</a></p>

> Where does this drop, and what is it for?

The item panel in the bank says "Found in" and "Used in" with a few skill icons. Hover either row and this
lists what is behind them.

<p>
  <img src="../../docs/img/item-sources.png" alt="Where Big Bones come from" width="400">
  &nbsp;
  <img src="../../docs/img/item-sources-used.png" alt="What Copper Bars are used in" width="330">
</p>

## What it shows

| Row | You see |
| --- | --- |
| **Found in** | Which enemy drops the item and where that enemy lives; which thieving target, hunting animal, chest, recipe, quest or shop purchase gives it; how many, and how likely. |
| **Used in** | Every recipe, upgrade, shop purchase or animal that uses it, and how many it takes. |

Sources are grouped (Combat, Hunting, Opening, Thieving, the skill of a recipe...) with the likeliest on
top. A long group is cut short so the others stay in sight.

## How it works

The game keeps this knowledge spread over its content: an enemy's drop tables, a recipe's products and
ingredients, a thieving target's loot, what a chest holds. Rather than know each of those classes by name,
the mod walks every content registry once when a character is opened.

```mermaid
flowchart LR
    A["Every content registry<br/>enemies, recipes, items,<br/>shop purchases..."] --> B["Walk each object once<br/>about 150 ms"]
    B --> C{"Does it hold<br/>an item, an amount,<br/>a chance?"}
    C -->|"Products, Drops,<br/>Rewards..."| D["Found in"]
    C -->|"Costs, Ingredients,<br/>Eats..."| E["Used in"]
    D --> F["Index by item"]
    E --> F
    F --> G["Tooltip on hover"]
```

- Chances are worked out from the game's own drop tables: the table's chance times the item's weight in it.
- Because nothing is hard-coded, items and enemies a game update adds show up by themselves.

## Good to know

- A few relations are left out on purpose, because they neither give nor take the item: quest steps and
  Mastery Guild tasks that ask for it, and the pieces of an equipment set.
- The chances are the base ones from the game's data; bonuses to drop chance are not applied.

## Install

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2).
2. Extract the mod's zip into the game folder, so the file ends up as `mods\ItemSources\ItemSources.dll`.

To remove it, delete `mods\ItemSources`. Your save is never changed.
