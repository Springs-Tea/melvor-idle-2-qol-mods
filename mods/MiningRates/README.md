# Mining Rates

> Where do I mine for the most XP, ore or gold per hour?

Every rock has its own time per hit, its own XP and its own ore, and some give gems or other items on the
side. To compare them you would open the rocks one by one and work out the hour in your head. Mining Rates
puts them side by side: one line per rock, with what an hour there gives your character, from the game's own
numbers. Click a line to see that rock on the Mining page.

This first version counts the hits and the XP of an hour. How many hits it takes to break a rock, and so how
much ore an hour brings, is not a number the game shows: the time of an ore, the ore and the gold show `-`
until it is known (see [What is counted](#what-is-counted)).

## Contents

- [Where it is](#where-it-is)
- [What the window shows](#what-the-window-shows)
- [The tooltip of a line](#the-tooltip-of-a-line)
- [What is counted](#what-is-counted)
- [Questions it answers](#questions-it-answers)
- [Good to know](#good-to-know)
- [When something is off](#when-something-is-off)
- [Install](#install)

## Where it is

On the Mining page, a round button with the Mining icon joins the others on the right edge of the screen.
Click it to open the window, and again, or the red X, to close it. On a window folded down to its title, the
button opens it up again. It is lit while the window is open and not folded.

The button is only there on the Mining page. On any other page the window steps aside; back on the Mining
page, it is there again as you left it, open or folded.

Drag the window to move it, drag its bottom right corner to resize it, or fold it down to its title with the
button beside the X. It is never larger than fits above the game's bottom bar, and a list of more than 14
rocks scrolls. Its place, size, fold and sort order are kept in `settings.json` in the mod's folder.

## What the window shows

One line per rock the Mining page shows you, in the order of the page: the rock's picture, its name, its
category, and per hour:

| Column | What it is |
| --- | --- |
| **Time** | How long one ore takes: the time of a hit, times the hits a break takes, over the ore of a break. `-` for now. |
| **Ore** | The rock's ore per hour, which comes with a break. `-` for now. |
| **XP** | Mining XP per hour: the XP of the rock's XP tag on every hit. |
| **Gold** | What the ore and the items of an hour sell for. `-` while the ore's hour is not known. |

- **The best number of each column is gold**: the shortest time, the most ore, XP and gold.
- **Click a column's name to sort by it.** The first click puts the best rocks on top, the next turns the
  order around. "Rock" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The rock the Mining page shows** in its big panel, the one you are mining, has the lit outline of a
  selected line, so it can be found in a sorted list.
- **Click a line to see that rock on the Mining page**: when the page's category filter is on another
  category, it goes to the rock's category, as the page's own category button does, and the page's list of
  rocks scrolls until the rock's card is in sight. Mining is not started or stopped, and the window stays
  open. The pointer turns into a hand over a line a click changes something for; on the rock you are mining,
  and on a rock whose card is in sight already, a click does nothing.
- **A rock you cannot mine yet is greyed**, with the Mining level it needs (`Lv 40`), or `Locked` when it
  needs something else. Its numbers come from the game's own values for it like the others, and it is never
  marked best. In a list sorted by a number, the locked rocks come last.
- Rocks the game keeps hidden from you are not listed until the game shows them. A rock you hid yourself on
  the Mining page is listed only while the page's **Show hidden** box is ticked, so the window lists the
  rocks the page does.

## The tooltip of a line

Put the pointer on a line for the whole hour at that rock:

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the rock is missing, in the game's own words. Only on a locked rock. |
| **Hidden on the Mining page: ...** | Only on a rock you hid yourself, while Show hidden is ticked. |
| **The rock the Mining page shows.** | Only on the line with the lit outline. |
| **One hit every 3s: 1,200 hits an hour.** | The game's time of a hit for the rock, with your bonuses. |
| **Break chance 25%, as its card shows it.** | The rock's break chance, and that the hits a break takes are not shown by the game. |
| **Mining XP** | Per hour, and for one hit. |
| **Every break**, and the ore | The ore a break gives, and how many an hour brings (`-` for now). |
| **Every hit**, and a line per item | Each item a hit can bring, with its chance per hit, how many a hit brings when it is more than one, and how many an hour brings. |
| **The doubling chance (5%) is counted in the ore and the items of a hit.** | Only when you have one. |
| **Gold** | What the hour sells for; while the ore's hour is not known, `-` and what the items of a hit sell for. |
| **Not counted: ...** | A bonus of yours the window does not count, by the name the Mining page gives it in its list of bonuses, or a stone, essence or bar you have a chance at. |
| **Click to show this rock on the Mining page. Mining is not started.** | What a click on the line does. Not there when a click changes nothing. |

## What is counted

The numbers start from the game's own values for each rock, the ones its card on the Mining page shows: the
time of a hit, the XP of a hit, the break chance, the ore and how many a break gives, the items of a hit with
their chances, and the doubling chance. They already hold your level, equipment, potions and every other
bonus, so the list moves with them.

| Rule the hour rests on | Where it comes from |
| --- | --- |
| A hit takes the rock's interval. | The game's value, the one its card shows. |
| Every hit gives the XP of the rock's XP tag. | The game's words: its XP tag is "The Skill XP gained per action", and a hit is the game's action for Mining. |
| The ore comes with a break, and a break gives the rock's ore quantity. | The game's value for the quantity; that it comes with a break, the card's own "per break" list. |
| How many hits a break takes. | Not shown by the game: it keeps how the break bar grows, and when a rock breaks, to itself. The time of an ore, the ore and the gold are `-` until it is known. |
| The items of a hit come at their chance on every hit. | The game's values, the card's own "per hit" list. |
| The doubling chance doubles the ore and the items of a hit. | The mod's: the game hands the doubling chance on when it adds a hit's items; for the ore, nothing the game shows says otherwise. |
| Chances count as their long-run average. | The mod's. |
| Gold is what the ore and the items sell for, in gold pieces. | The game's sell prices. |

## Questions it answers

- **Where do I train Mining?** The gold number under XP.
- **Is the rock I picked last week still the best for XP?** It is the line with the lit outline: compare it
  with the gold number under XP.
- **What else does a hit bring?** The "Every hit" lines of the tooltip, each with its chance.
- **Where is that rock on the Mining page?** Click its line.

## Good to know

- It is an estimate, not a promise: chances average out over many hits, not over ten.
- How many hits it takes to break a rock is not a number the game shows. The window does not make one up, so
  the time of an ore, the ore per hour and the gold show `-` for now.
- A chance to shatter a rock and a chance of more ore are not counted. The tooltip says so when you have one,
  by the name the Mining page gives it.
- A stone, essence or bar of the rock that you have a chance at, but that the game does not list among a
  hit's items, is named in the tooltip as not counted: the game does not say whether it comes on a hit or on
  a break.
- Mining has no mastery XP in this game (its Mastery Guild counts ranks, points and tasks), so there is no
  column for it.
- The window starts nothing. A click on a line only changes the page's category filter and where its list of
  rocks is scrolled; mining, the rocks you hid and the Show hidden box are left as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Mining page | It comes a moment after the page opens. If it never comes, look for `[MiningRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` under Time, Ore and Gold | The game does not show how many hits a break takes (see [What is counted](#what-is-counted)). |
| `-` in every column of a line | The game gives no time of a hit for that rock yet. |
| A line without a picture | The game has no picture for that rock yet, only its grey stand-in, and the window shows none rather than the stand-in. |
| "Not counted: a chance ..." instead of a name | The window has not read the name from the Mining page's list of bonuses yet. If it never does, `MelvorModLoader.log` says the game was updated. |
| A click on a line does nothing | `MelvorModLoader.log` says "A click on a line shows nothing from now on" when a game update changed the Mining page; the numbers still work. |
| No button and no window at all | Look for `[MiningRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\MiningRates\MiningRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: MiningRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\MiningRates`.
Your save is never changed.
