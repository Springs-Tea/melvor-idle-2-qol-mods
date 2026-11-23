# Hunting Rates

> Where do I hunt for the most XP, catches or gold per hour?

Every animal has its own hunt: surveying until its tracks are found, a number of tracking steps, and a trap
that may fail. Each step has its own time and chance, and the animal brings its own products, with a chance at
a rare species. To compare them you would open the animals one by one and add up the steps in your head.
Hunting Rates puts them side by side: one line per animal, with the game's own numbers of every step of its
hunt and of what a catch brings. Click a line to open that animal on the Hunting page.

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

On the Hunting page, a round button with the Hunting icon joins the others on the right edge of the screen.
Click it to open the window, and again, or the red X, to close it. On a window folded down to its title, the
button opens it up again. It is lit while the window is open and not folded.

The button is only there on the Hunting page. On any other page the window steps aside; back on the Hunting
page, it is there again as you left it, open or folded.

Until you move it, the window opens at a corner of the Hunting page where it leaves the page's panel of the
animal selected, its location buttons and the most of its animals in sight. Drag the window to move it, drag
its bottom right corner to resize it, or fold it down to its title with the button beside the X. It is never
larger than fits above the game's bottom bar, and a list of more than 14 animals scrolls. Its place, size,
fold and sort order are kept in `settings.json` in the mod's folder.

## What the window shows

One line per animal the Hunting page can show you, in the order of the page (its locations, and in each its
animals): the animal's picture, its name, its location, and per hour:

| Column | What it is |
| --- | --- |
| **Time** | How long one catch takes: surveying, tracking and trapping, escaped hunts included. |
| **Catches** | Animals caught per hour. |
| **XP** | Hunting XP per hour. |
| **Gold** | What the catches and surveys of an hour bring, in gold. |
| **Traps** | Traps used per hour; none for an animal you hunt without a trap. |

- **For now these five columns show `-`**, and a note under the list says why: the window does not know yet
  how surveying, tracking and trapping join into a catch, the XP of a catch within the range the page writes,
  or when traps and bait are used up. The game gives every step of a hunt its own time and chance, and no
  number of the game's says how they add up. Until that is settled, the window shows the game's numbers of
  each step in a line's tooltip instead of a total of its own.
- **The best number of each column is gold**, once a column has numbers: the shortest time, the most catches,
  XP and gold. Traps has no best, since the fastest animal always uses the most.
- **Click a column's name to sort by it.** The first click puts the best animals on top, the next turns the
  order around. "Animal" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The animal the Hunting page shows**, the one selected in its panel, has the lit outline of a selected
  line, so it can be found in a sorted list. Select another animal on the page and the outline follows.
- **Click a line to go to that animal**: the Hunting page opens its location and selects the animal, as your
  own two clicks there would, and its list scrolls until the animal's card is in sight beside the window. When
  the page shows its journal, it shows the hunt again first, as its own button does. Hunting is not started or
  stopped, and the window stays open. The pointer turns into a hand over a line a click takes somewhere.
- **An animal you cannot hunt yet is greyed**, with the Hunting level it needs (`Lv 40`), or `Locked` when it
  needs something else. A click on it opens its location only, and nothing while the location itself is
  locked.
- **Animals and locations the game keeps hidden from you are not listed** until the game shows them.
- **An animal you have not discovered yet** is named and pictured as the Hunting page's own list draws it,
  once the page has shown its location; before that, by the game's own word for something not found yet, and
  without a picture.
- **The game's setting for players who do not want to see spiders** is followed: with it on, a spider's line
  shows the picture the game shows instead, or none, never the spider.

## The tooltip of a line

Put the pointer on a line for the game's numbers of a hunt of that animal:

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the animal is missing, in the game's own words. Only on a locked animal. |
| **The animal the Hunting page shows.** | Only on the line with the lit outline. |
| **The animal you are hunting now.** | Only while you hunt it. |
| **Tool: ...** | The tool the animal is tracked with, or the page's own words for an animal that needs none. |
| **Surveying: one survey every 3s, until the tracks are found.** | The time of a survey, with your bonuses. |
| **Tracking: 3 to 5 steps of 2s to 4s each, 80% success a step.** | The number of steps, the time of one and its chance, with your bonuses; the chance to skip a step under it, by the page's name for it. |
| **Trapping: 4s to 6s a try, 60% success.** | The time of a try of the trap and its chance; the chance to try again under it, by the page's name for it. |
| **A catch: not counted, ...** | The time of a catch and the catches of an hour, once the window knows how the steps join. |
| **Hunting XP: 12 to 18, 15 in the middle, a catch.** | The XP of a catch in the range the Hunting page writes it, once the page has shown that animal; before that, the game's XP before the catch's own range. |
| **Surveying XP**, **Journal XP** | The base XP of a survey at the location, and the base journal XP of the animal for tracking and trapping. Not counted. |
| **Every catch, a chance of:** and a line per item | The animal's products with their chance and how many a catch brings. |
| **A rare species on 1% of catches:** | Each species with its share of the rare ones and its extra products. A species you have never trapped and a collectable you have never found are not named, as the journal does. |
| **Every survey, a chance of:** | What a survey can find. |
| **The doubling chance (5%) is counted in a catch's products only.** | Only when you have one. |
| **Trap:** and **Bait:** | The trap selected for the animal and how many are in the bank, or that you hunt it without a trap; its bait, and whether you use it. |
| **Gold: 35 a catch, ...** | What the products and rare extras of a catch sell for. |
| **Click to open this animal on the Hunting page. Hunting is not started.** | What a click on the line does; on a locked animal, which location it opens. Not there when a click changes nothing. A line also says when the page's panel of the animal selected lies under the window, or when the page has no room for the animal's card beside it. |

## What is counted

The numbers start from the game's own values for each animal, the ones the Hunting page's panel shows for the
animal selected there: the time of a survey, the steps of tracking and the time and chance of one, the chance
to skip a step, the time and chance of a try of the trap, the chance to try again, the XP of a catch, the
products and their chances, the chance of a rare species and the weights of the species, their extra products,
and the doubling and preservation chances. Whatever changes the game's numbers changes the window's too.

| Rule | Where it comes from |
| --- | --- |
| The time, steps and chance of every step of a hunt | The game's values, with your bonuses. |
| The range of the XP of a catch | The Hunting page's: it writes the XP of a catch as a range, and the window reads that range from the page. |
| How the steps join into a catch (how many surveys find the tracks, what a failed step or trap leads to) | Not known yet: Time, Catches, XP and Gold show `-`. |
| When traps and bait are used up | Not known yet: Traps shows `-`, and the bait an hour is not given. |
| The middle of each of the game's ranges of time, and of the XP of a catch | The window's own, as Fishing Rates takes the middle of a catch time. |
| Chances count as their long-run average; a rare species' extra product as the chance of a rare species, of that species among the rare ones, and of the product | The window's own reading of the game's names. |
| The doubling chance doubles a catch's products, not the rare extras or what a survey finds | The window's own reading, as Fishing Rates counts its fish. |
| Gold | What the items sell for in gold. A collectable is not counted. |

## Questions it answers

- **What does each animal ask of me?** Its tooltip: the time of every step, its chance, its tool, trap and
  bait.
- **Which animal gives the most for one catch?** The XP and the gold of a catch in the tooltip.
- **Where are the rare ones?** The rare species line of the tooltip, with the share of each.
- **Is the animal I picked last week still a good one?** It is the line with the lit outline, the animal the
  Hunting page shows.

## Good to know

- It is an estimate, not a promise: chances average out over many hunts, not over ten.
- The hour of each animal needs a rule of the game that no number of the game's gives: how its steps join into
  a catch. Until that is settled, the columns show `-` rather than a guess, and the tooltip has what the game
  does give.
- Hunting has no mastery XP in this game (its Mastery Guild counts tasks), so there is no column for it. An
  animal's journal has XP of its own; the window knows only its base values, and names them in the tooltip.
- Gold is not profit: traps and bait are not taken off.
- The window starts nothing. A click on a line only changes which location and animal the Hunting page
  selects, and shows the hunt instead of the journal; hunting, traps, bait and hunting without a trap are left
  as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Hunting page | It comes a moment after the page opens. If it never comes, look for `[HuntingRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` in every column | The window does not know yet how the steps of a hunt join into a catch: the note under the list says so. |
| "before its own range, which the page has not shown yet" | Open that animal's location on the Hunting page once: the window reads the range there. |
| A line without a picture | The game has no picture for that animal yet, only its grey stand-in; or the spider setting is on and the page has not shown its own picture for a spider yet. |
| A click on a line opens nothing | `MelvorModLoader.log` says "A click on a line opens nothing from now on": a game update changed the Hunting page. |
| No button and no window at all | Look for `[HuntingRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\HuntingRates\HuntingRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: HuntingRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\HuntingRates`. Your save is
never changed.
