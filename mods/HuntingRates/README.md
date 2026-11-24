# Hunting Rates

> Where do I hunt for the most XP, catches or gold per hour?

Every animal has its own hunt: surveying until its tracks are found, a number of tracking steps, and a trap
that may fail and let the animal escape. Each step has its own time and chance, and the animal has its own
output, with a chance at a rare species. To compare them you would open the animals one by one and add up the
steps in your head. Hunting Rates puts them side by side: one line per animal, with the time of a catch and
what an hour there gives, from the game's own numbers. Click a line to open that animal on the Hunting page.

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

Until you move it, the window opens at the corner of the Hunting page where the most of its animals and
location buttons stay in sight beside it, then its panel of the animal selected; it may lie over the panel of
the hunt in progress. Drag the window to move it, drag its bottom right corner to resize it, or fold it down
to its title with the button beside the X. It is never larger than fits above the game's bottom bar, and a
list of more than 14 animals scrolls. Its place, size, fold and sort order are kept in `settings.json` in the
mod's folder.

## What the window shows

One line per animal the Hunting page can show you, in the order of the page (its locations, and in each its
animals): the animal's picture, its name, its location, and:

| Column | What it is |
| --- | --- |
| **Time** | How long one catch takes on average: surveying, tracking and trapping, escapes included. |
| **Catches** | Animals caught per hour. |
| **XP** | Hunting XP per hour. |
| **Gold** | What the output, the rare extras and the finds of an hour sell for. |
| **Traps** | Traps used per hour, less what is preserved; none for an animal you hunt without a trap. |

- **A column shows `-` where the window cannot count it for that animal yet**, and a note under the list
  says why. For now that is most animals: an escape sends you back to tracking with more steps to go, and how
  many is known only where every reading of it gives the same number (see
  [What is counted](#what-is-counted)). It is also `-` while the Hunting page has not yet written how many
  surveys a hunt takes (its panel writes it for the animal selected there), for a trap whose bonuses change
  the chance after a failed try, and, for XP alone, until the page has shown the animal's card with its XP
  range.
- **The best number of each column is gold**: the shortest time, the most catches, XP and gold. Traps has no
  best, since the fastest animal always uses the most.
- **Click a column's name to sort by it.** The first click puts the best animals on top, the next turns the
  order around. "Animal" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The animal the Hunting page shows**, the one selected in its panel, has the lit outline of a selected
  line, so it can be found in a sorted list. Select another animal on the page and the outline follows.
- **Click a line to go to that animal.** While you are not hunting, the Hunting page opens the animal's
  location and selects the animal through its own location button and the animal's own card, as your own two
  clicks there would, and its list scrolls until the card is in sight beside the window. When the page shows
  its journal, it shows the hunt again first, by its own button. Hunting is not started, and the window stays
  open. The pointer turns into a hand over a line a click takes somewhere.
- **While you hunt, a click selects nothing**, since selecting another animal might change the hunt in
  progress. It only shows: the hunt instead of the journal, and the animal's card when the page shows its
  location. The tooltip of a line a click cannot serve then says so.
- **An animal you cannot hunt yet is greyed**, with the Hunting level it needs (`Lv 40`), or `Locked` when it
  needs something else. A click on it opens its location only, and shows its card, where the page says what it
  needs; nothing while the location itself is locked. When the page keeps an animal's card from being
  selected, a click does not select it either, and its tooltip says so.
- **Animals and locations the game keeps hidden from you are not listed** until the game shows them.
- **An animal you have not discovered yet** gives nothing away: its line has its name as the Hunting page
  writes it (by the game's own word for something not found until the page has shown its location), its
  location and what it needs, and no picture until the page draws one.
- **The game's setting for players who do not want to see spiders** is followed: with it on, a spider's line
  shows the picture the game shows instead, or none, never the spider.

## The tooltip of a line

Put the pointer on a line for the whole hour of that animal, grouped as the Hunting page's panel groups its
numbers:

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the animal is missing, in the game's own words. Only on a locked animal. |
| **The animal the Hunting page shows.** | Only on the line with the lit outline. |
| **The animal you are hunting now.** | Only while you hunt it. |
| **Tool: ...** | The tool the animal is tracked with, as its card on the page writes it once the page has shown it, or the page's own words for an animal that needs none. |
| **Surveying: 3.84s a survey, 1 to 3 until the tracks are found.** | The time of a survey with your bonuses, and how many a hunt takes, as the page writes it. When the page wrote it for another animal, a line under it says which. |
| **Tracking: 4 to 8 steps of 10s to 12.6s each.** | The number of steps and the time of one, with your bonuses; the chance to skip a step under it, by the page's name for it, with why it is not counted. |
| **Trapping: 7.65s to 11.05s a try, 44% success.** | The time of a try of the trap and its chance; the chance to try again under it, by the page's name for it. |
| **An escape: back to tracking, with 4 more steps, 1.2 a catch on average.** | Where an escape takes you, how many steps it adds, and how many escapes a catch takes. Where the window does not know the steps yet, the numbers its readings give. |
| **A catch every 2m 30s on average, worked out by a rule measured in the game: 23.9 an hour.** | The time of a catch and the catches of an hour; where they cannot be counted, what the window does not know. |
| **Hunting XP: 11,154 an hour, 467 a catch, the middle of 234 to 701.** | The XP of a catch, the middle of the range the page writes, and of an hour. Before the page has shown the animal's card, the game's XP before its range. |
| **Journal XP: 1 a tracking step, 10 a catch: 496 an hour.** | The animal's journal XP, and of an hour. |
| **Every catch, a chance of:** | The animal's output with the chance of each item, how many it brings, and how many an hour. |
| **A rare species, 1%:** | Each species with its share of the rare ones and its extra products. A species you have never trapped and a collectable you have never found are not named, as the journal does. |
| **Every survey, a chance of:** | What a survey can find, and how many an hour. |
| **Output Doubling Chance: 2%, counted in the output, ...** | Only when you have one. |
| **Trap:** and **Bait:** | The trap selected for the animal, or that you hunt it without one; its bait, and whether you use it. Each with an hour of it and how long what is in the bank lasts. The preservation chance under them. |
| **Gold: 1,466 an hour, what the output, rare extras and finds sell for.** | What an hour's items sell for in gold; where an hour cannot be counted, what one catch's output sells for. |
| **Click to open this animal on the Hunting page. Hunting is not started.** | What a click on the line does; on a locked animal, which location it opens; while you hunt, that it only shows. When a click cannot serve the line: why (a hunt runs, the page does not let the animal be selected, or the page has no room for its card beside the window), and when the page's panel of the animal selected lies under the window. |

An animal not discovered yet has only its name, its location, what it needs, and what a click does.

## What is counted

The numbers start from the game's own values for each animal, the ones the Hunting page's panel shows for the
animal selected there: the time of a survey, the steps of tracking and the time of one, the time and chance of
a try of the trap, the chance to try again, the XP, the output and its chances, the chance of a rare species
and the weights of the species, their extra products, and the doubling and preservation chances. Whatever
changes the game's numbers changes the window's too.

How these join into a catch is in no number of the game's. It was measured on a character's own hunting, 15
minutes of Yellowtail Bird: 62 tracking steps, 13 tries of the trap, 7 escapes and 6 catches.

| Rule | Where it comes from |
| --- | --- |
| A hunt starts with surveys until the tracks are found, as many as the middle of the range the page writes for surveying ("3.84s - 11.52s" is one to three surveys: two). | The game's own words, and the window's middle of the range. The Hunting page writes it only for the animal selected there; for another animal, the window takes the last one it read and says so in the tooltip. Measured: 2, 1, 2, 3, 1 and 1 surveys, never more than three. |
| Then tracking: every step takes one tracking interval and moves on by one; no step fails. | Measured: 62 of 62 steps. The tracking chance does not make a step fail. |
| The chance to skip a tracking step is not counted. | Measured: no step was seen skipped in 62, where about 3 were to be expected. What it does is not known yet. |
| Every number of steps in the game's range is as likely, and every time in a range: the window takes the middle of each. | The window's own, as Fishing Rates takes the middle of a catch time. |
| Then a try of the trap, which catches at the trapping chance. | The game's chance. Measured: 6 of 13 tries caught. |
| A failed try is tried again at the reattempt chance; otherwise the animal escapes. | The game's own words: "The chance to reattempt a trapping action when it fails". Measured: 7 of 7 failed tries escaped, where 0.4 tries again were to be expected. |
| An escape takes you back to tracking, never to surveying, with more steps to go, then another try. | Measured: 7 of 7 escapes. |
| An escape adds as many steps as the animal's fewest, as the width of its range, as its own fewest before your bonuses, or 4: which, is not known yet. Where all four give the same number, the window counts it; elsewhere the columns show `-`. | Measured: each of the 7 escapes added 4 steps on Yellowtail Bird, where all four readings give 4. |
| A catch ends the hunt, and the next starts with surveying. | Measured: 6 of 6 catches. |
| The chances of a try count as they are now. A trap whose bonuses change the chance after a failed try is not counted. | The window's own: what those bonuses do is not known yet. |
| Every try uses one trap and one bait, whether it catches or not, less what your Input Preservation Chance keeps. | Measured: 13 tries used 13 traps and 13 Seed Bait. What is preserved is the game's own words, "Your chance to preserve the input". The bait of a hunt without a trap is not counted: it was not measured. |
| Hunting XP comes with a catch only, the middle of the range the page writes. | Measured: XP on 6 of 6 catches and with nothing else, 467.7 on average against the middle, 467.25. |
| The journal XP is the animal's base: for every tracking step and for every catch. | Measured: 1 on each of 62 steps and 10 on each of 6 catches, the game's base values for Yellowtail Bird. |
| The output comes with a catch, each item at its chance; the doubling chance doubles it, not the rare extras or what a survey finds. | Measured: the output came on 6 of 6 catches and at no other time. The doubling is the window's reading of the game's words for it, "Your chance to double the output". |
| What a survey can find comes at its chance on every survey. | The window's reading of the game's list for it. |
| A rare species' extra product comes at the chance of a rare species, of that species among the rare ones, and of the product. | The game's chances; the window's reading of how they join. |
| Chances count as their long-run average. | The window's own. |
| Gold is what the items sell for in gold. A collectable is not counted. | The game's sell prices. |

## Questions it answers

- **Where do I get the most Hunting XP an hour?** Sort by XP.
- **Which animal is the quickest to catch?** Sort by Time.
- **How long do my traps and bait last?** The trap and bait lines of the tooltip.
- **What does each animal ask of me?** Its tooltip: the time of every step, its chance, its tool, trap and
  bait.
- **Where are the rare ones?** The rare species line of the tooltip, with the share of each.

## Good to know

- It is an estimate, not a promise: chances average out over many hunts, not over ten. An escape costs
  several steps, so one hunt can take twice as long as the next.
- The way the steps of a hunt join was measured on one animal. Where an escape's steps are not known yet, the
  columns show `-` rather than a guess.
- Hunting has no mastery XP in this game (its Mastery Guild counts tasks), so there is no column for it. An
  animal's journal XP is in the tooltip.
- Gold is not profit: traps and bait are not taken off.
- The window starts nothing. While you are not hunting, a click on a line changes which location and animal
  the Hunting page selects, through the page's own buttons; while you hunt, it selects nothing. It also shows
  the hunt instead of the journal. Hunting, traps, bait and hunting without a trap are left as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Hunting page | It comes a moment after the page opens. If it never comes, look for `[HuntingRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` on most lines | The window does not know how many steps an escape adds for those animals yet: the note under the list says so. |
| `-` on every line, with "how many surveys a hunt takes" in the note | Select an animal on the Hunting page: its panel writes the time of surveying, which the window reads. |
| "before the range of a catch, which the page has not shown yet" | Open that animal's location on the Hunting page once: the window reads the range there. |
| A line without a picture | The game has no picture for that animal yet, only its grey stand-in; the animal is not discovered yet; or the spider setting is on and the page has not shown its own picture for a spider yet. |
| A click on a line does nothing while you hunt | As meant: while a hunt runs, a click selects nothing. |
| A click on a line opens nothing at all | `MelvorModLoader.log` says "A click on a line opens nothing from now on", or that a page button "did not" do what its click does: a game update changed the Hunting page. |
| No button and no window at all | Look for `[HuntingRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\HuntingRates\HuntingRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: HuntingRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\HuntingRates`. Your save is
never changed.
