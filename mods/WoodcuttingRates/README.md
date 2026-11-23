# Woodcutting Rates

> Which tree gives me the most XP per hour, and what else does it give?

Every tree has its own time per hit, its own hit points, its own XP and its own logs, and some give other
items on the side. To compare them you would open the trees one by one and work out the hour in your head.
Woodcutting Rates puts them side by side: one line per tree, with what an hour there gives your character,
from the game's own numbers. Click a line to show that tree on the Woodcutting page.

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

On the Woodcutting page, a round button with the Woodcutting icon joins the others on the right edge of the
screen. Click it to open the window, and again, or the red X, to close it. On a window folded down to its
title, the button opens it up again. It is lit while the window is open and not folded.

The button is only there on the Woodcutting page. On any other page the window steps aside; back on the
Woodcutting page, it is there again as you left it, open or folded.

Drag the window to move it, drag its bottom right corner to resize it, or fold it down to its title with the
button beside the X. It is never larger than fits above the game's bottom bar, and a list of more than 14
trees scrolls. Its place, size, fold and sort order are kept in `settings.json` in the mod's folder.

## What the window shows

One line per tree the Woodcutting page shows you, in the order of the page: the tree's picture, its name, its
category, and per hour:

| Column | What it is |
| --- | --- |
| **Time** | How long one log takes, every hit of a cut counted in. `-` for now: see below. |
| **Logs** | Logs per hour. `-` for now. |
| **XP** | Woodcutting XP per hour. |
| **Gold** | What the logs and items of an hour sell for. `-` for now. |

- **Time, Logs and Gold show `-` for now.** A tree takes several hits to cut, and the game gives no number
  for the damage of a hit, so the window cannot count the hits a cut takes, and with them the logs of an
  hour. It shows `-` rather than a guess, and says so under the list.
- **The best number of each column is gold.**
- **Click a column's name to sort by it.** The first click puts the best trees on top, the next turns the
  order around. "Tree" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The tree being cut**, the one the big panel of the Woodcutting page shows, has the lit outline of a
  selected line, so it can be found in a sorted list.
- **Click a line to show that tree on the Woodcutting page**: when the page's category filter hides its card,
  the page shows the tree's category, as its own category button would; then the page's list of trees
  scrolls until the card is in sight. Woodcutting is not started or stopped, and the window stays open. The
  pointer turns into a hand over a line a click changes something for.
- **A tree you cannot cut yet is greyed**, with the Woodcutting level it needs (`Lv 40`), or `Locked` when it
  needs something else. Its numbers come from the game's own values for it like the others, and it is never
  marked best. In a list sorted by a number, the locked trees come last.
- Trees the game keeps hidden from you are not listed until the game shows them. A tree you hid yourself on
  the Woodcutting page is listed only while the page's "Show hidden" box is ticked, as the page lists it.

## The tooltip of a line

Put the pointer on a line for the hour at that tree, as far as it is counted:

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the tree is missing, each requirement once, in the game's own words. Only on a locked tree. |
| **The tree being cut.** | Only on the line with the lit outline; "The tree the Woodcutting page shows in its big panel." while woodcutting is stopped. |
| **Hidden by you on the Woodcutting page.** | Only on a tree you hid, while "Show hidden" is ticked. |
| **One hit every 2.5s: 1,440 an hour.** | The game's time of a hit at that tree, with your bonuses. |
| **Woodcutting XP: 14,400 an hour, 10 a hit.** | Per hour, and for one hit. |
| **Hit points: ...** | The tree's hit points, and that the hits a cut takes are not counted. |
| **Your ... change them too.** | Your chances of double damage, of a weak spot and of an instant cut at that tree, by the names the Woodcutting page gives them, when you have any. |
| **Logs: ..., 1 at a time; not counted per hour.** | The tree's logs, and how many come at a time with your bonuses. |
| **Every hit, a chance of:**, and a line per item | Each item a hit can bring besides, with its chance per hit and how many an hour brings. |
| **Every cut, a chance of:**, and a line per item | Each item a cut can bring besides, with its chance per cut. Not per hour, as the cuts of an hour are not counted. |
| **The doubling chance (5%) is counted in these items.** | Only when you have one. |
| **Gold: not counted, ...** | Why there is no gold for now. |
| **Overgrowth on this tree now: ...** | The overgrowth on the tree, by the game's names, with the stacks each has left. Its bonuses are already in the tree's numbers above. |
| **Click to show this tree on the Woodcutting page. Woodcutting is not started.** | What a click on the line does. Not there when a click changes nothing: on the tree being cut, on a tree whose card is in sight, and on one whose category the page does not offer. |

## What is counted

The numbers start from the game's own values for each tree, the ones its tree cards show: the time of a hit,
the hit points, the XP, the logs, what a hit and a cut can bring, the doubling chance and the chances of
double damage, of a weak spot and of an instant cut. Whatever changes the game's numbers changes the
window's too: your level, equipment, potions, the Mastery Guild, and the overgrowth on a tree.

What the hour rests on, and where each part comes from:

| Rule | Where it comes from |
| --- | --- |
| The time of a hit | The game's value for the tree. |
| The hits of an hour | The mod's: an hour divided by the time of a hit. |
| The XP of a hit | The game's value, the XP the tree's card shows. Every hit gives it: a hit is what the game counts as one Woodcutting action. |
| What a hit and a cut can bring | The game's two lists for the tree, with your bonuses. |
| Chances | The mod's: each counts as its long-run average. |
| Doubling | The game hands the doubling chance to what adds a hit's and a cut's items, so it is counted in them, as Fishing Rates counts it. |
| The hits a cut takes | Not counted: the game gives no number for the damage of a hit. |
| The logs of an hour, the time per log, the gold | Not counted, as they rest on the hits a cut takes. |
| Overgrowth on a tree now | The game's: it is in the tree's numbers. |
| Overgrowth a cut may put on later | Not counted. |

## Questions it answers

- **Where do I train Woodcutting?** The gold number under XP.
- **Is the tree I am cutting still the best for XP?** It is the line with the lit outline: compare it with
  the gold numbers.
- **What does a hit at this tree bring besides?** The tooltip of its line.
- **What do I need to cut that tree?** The greyed line's level, and its tooltip.

## Good to know

- It is an estimate, not a promise: chances average out over many hits, not over ten.
- Logs, time per log and gold are `-` for now. The game gives no number for how much damage a hit deals, so
  the window does not know how many hits a cut takes, and it does not guess.
- Woodcutting has no mastery XP in this game (its Mastery Guild counts ranks and tasks), so there is no column
  for it.
- The overgrowth on a tree now is in its numbers; what a cut may put on later is not counted.
- The window starts nothing. A click on a line only changes which category the Woodcutting page shows and
  scrolls its list of trees; woodcutting, the trees you hid and "Show hidden" are left as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Woodcutting page | It comes a moment after the page opens. If it never comes, look for `[WoodcuttingRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` in a line | For Time, Logs and Gold, see above. In XP: the game gives no time for a hit on that tree yet. |
| A line without a picture | The game has no picture for that tree yet, only its grey stand-in, and the window shows none rather than the stand-in. |
| "Your chance of double damage" instead of the game's name | The window has not read the name from the Woodcutting page yet: it does once a list of a tree's bonuses there has been shown. |
| A click shows the tree's category but does not scroll to it | `MelvorModLoader.log` says the page's tree cards could not be read: a game update changed the page. |
| No button and no window at all | Look for `[WoodcuttingRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\WoodcuttingRates\WoodcuttingRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: WoodcuttingRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\WoodcuttingRates`.
Your save is never changed.
