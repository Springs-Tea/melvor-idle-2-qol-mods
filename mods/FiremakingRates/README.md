# Firemaking Rates

> Which log gives me the most per hour, and how many logs does an hour burn?

Every log has its own time to burn, its own XP, and some give coins or items on the side. Recipe Rates already
writes the XP per hour under each log on the Firemaking page; the rest of the hour, how many logs it uses up,
what they would sell for and what burning gives, you would work out log by log. Firemaking Rates puts the
logs side by side: one line per log, with what an hour of burning it gives and costs your character, from the
game's own numbers. Hover a line for the whole hour, and click it to open that log on the Firemaking page.

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

On the Firemaking page, a round button with the Firemaking icon joins the others on the right edge of the
screen. Click it to open the window, and again, or the red X, to close it. On a window folded down to its
title, the button opens it up again. It is lit while the window is open and not folded.

The button is only there on the Firemaking page. On any other page the window steps aside; back on the
Firemaking page, it is there again as you left it, open or folded.

Until you move it, the window opens at the right side of the page, clear of the round buttons, at the height
where the most of the logs' buttons in the page's list can be shown whole beside it. Drag the window to move
it, drag its bottom right corner to resize it, or fold it down to its title with the button beside the X. It
is never larger than fits above the game's bottom bar, and a list of more than 14 logs scrolls. Its place,
size, fold and sort order are kept in `settings.json` in the mod's folder.

## What the window shows

One line per log the Firemaking page shows you, in the order of the page: the log's picture, its name, its
category when the page has more than one, and:

| Column | What it is |
| --- | --- |
| **Time** | How long one fire takes: the game's time to burn one log. |
| **Logs** | The logs burnt per hour: the logs that leave the bank, the ones your preservation chance keeps not counted. |
| **XP** | Firemaking XP per hour. |
| **Cost** | What the logs burnt in an hour would sell for, in gold: what you give up by burning them instead of selling them. |
| **Gold** | What burning gives in an hour, in gold: its coins, and what the items it makes sell for. |

- **The numbers are Recipe Rates' for the same log.** Where Recipe Rates writes a number under a log, or in
  its XP tag's tooltip, this window has the same one. See [What is counted](#what-is-counted).
- **The best number of each column is gold**: the most XP and gold, the least time and cost. Logs have no
  best: they are logs of different kinds, and burning more of them an hour is as much a faster pace as a
  greater cost.
- **Click a column's name to sort by it.** The first click puts the best logs on top, the next turns the
  order around. "Log" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The log the Firemaking page shows** has the lit outline of a selected line, so it can be found in a sorted
  list.
- **Click a line to open that log on the Firemaking page**: the page shows the log's category and selects the
  log, as its own category button and list of logs do, and its list scrolls until the log's whole button is
  in sight beside the window, above or below it when the window lies across the button's column. Burning is
  not started or stopped, the page's Start button and the bonfire are not touched, and the window stays open
  where it is. A locked log opens its category only, and a log whose category is locked opens nothing. On the
  log the page shows already, a click only scrolls the page's list to it, when its button is out of sight.
  The pointer turns into a hand over a line a click changes something for.
- **When the page's list has no room for a log's button beside the window**, the line's tooltip says so: drag
  the window aside, off the page's list.
- **A log you cannot burn yet is greyed**, with the Firemaking level it needs (`Lv 40`), or `Locked` when it
  needs something else. Its numbers come from the game's own values for it like the others, and it is never
  marked best. In a list sorted by a number, the locked logs come last.
- Logs and categories the game keeps hidden from you are not listed until the game shows them.

## The tooltip of a line

Put the pointer on a line for the whole hour of burning that log. The numbers below are an example: a log of
2.5 seconds and 10 XP, with a preservation chance of 5% and 5,000 of it in the bank.

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the log is missing, in the game's own words. Only on a locked log. |
| **The log the Firemaking page shows.** | Only on the line with the lit outline. |
| **The log you are burning.** | Only on the log your fire burns. |
| **One fire every 2.5s: 1,440 fires an hour.** | The game's time to burn one log, with your bonuses. |
| **Firemaking XP: 14,400 an hour, 10 a fire.** | Per hour, and for one fire: the XP the log's XP tag shows. |
| **Logs burnt: 1,368 Oak Logs an hour; the bank has enough for 3h 39m.** | The logs that leave the bank in an hour, and how long the bank's logs last at that pace, in whole minutes. "The bank has not enough for one fire" when it has too few to start one. |
| **Also used:** or **Also costs:** | What else a fire uses, if anything, per hour. |
| **The preservation chance (5%) is counted in what is used.** | Only when you have one. Once the window has read it, the chance is named as the Firemaking page names it. |
| **Every fire gives:**, and a line per thing | The coins a fire gives, and each item it can make, with its chance and how many an hour brings. |
| **The doubling chance (10%) is counted in the items, not the coins.** | Only when you have one and the log makes items, named as the page names it once read. |
| **Bonfire time: 30s a fire, by the game's value for this log.** | The game's bonfire time for the log, the time a fire of it adds to the bonfire by the game's names. No hour of it is given: see [What is counted](#what-is-counted). |
| **Gold: 1,234 an hour, ...** | What burning gives in an hour, in gold. |
| **Cost: 34,200 gold an hour, what the logs burnt would sell for.** | The Cost column's number (here for a log that sells for 25). |
| **May also come while a log burns, not counted:**, and a line per event | The game's Firemaking events that may come while this log burns, by the game's names, with what each gives. |
| **Click to open this log on the Firemaking page. Burning is not started.** | What a click on the line does. On a locked log: "Click to open *its category* on the Firemaking page". Not there when a click changes nothing. |
| **The Firemaking page has no room for this log's button beside this window: drag the window aside.** | On two lines, when no scroll of the page's list shows the log's whole button clear of the window. |

## What is counted

The numbers start from the game's own values for each log: the time to burn it, the XP, what a fire uses (the
log, and anything else), the preservation and doubling chances, the items a fire can make with their chances,
the coins it gives, the bonfire time, and the sell prices. They already hold your level, equipment, potions,
the bonfire and every other bonus, so the list moves with them.

| Rule | Where it comes from |
| --- | --- |
| A fire burns one log in the log's time; the fires of an hour are an hour over it. | The game's value for the log; the hour is the mod's. |
| Every fire gives the XP of the log's XP tag. | Recipe Rates' rule, which writes the same number under the log. Not measured yet. |
| Every fire takes the log's cost, and the preservation chance keeps it. | Recipe Rates' rule. Not measured yet. |
| Every fire makes each of the log's items at its chance, and the doubling chance doubles them. | Recipe Rates' rule. Not measured yet. |
| Every fire gives the log's coins, not doubled. | Recipe Rates' rule, which lists the coins apart from its "Sell value". Not measured yet. |
| Gold is the gold coins and what the items sell for; Cost is what the logs burnt sell for. | The game's sell prices; the sums are the mod's. |
| The bonfire time of a fire is the game's bonfire time for the log. | The game's value. Whether every fire adds it to the bonfire, and how the bonfire's most time caps it, the window does not know yet: it gives no hour of it. |
| The Firemaking events are not counted. | The window does not know how often the game rolls them. |
| Chances count as their long-run average. | The mod's. |

Mastery XP has no column: Firemaking keeps no mastery XP per log in this game (its Mastery Guild counts
ranks, points and tasks).

## Questions it answers

- **Which log do I burn for XP?** The gold number under XP.
- **How many logs does an hour burn, and how long will my bank last?** The Logs column, and the "Logs burnt"
  line of the tooltip.
- **Is burning worth more than selling?** The Cost column, beside the Gold column.
- **What else does a fire bring?** The "Every fire gives" lines of the tooltip, each with its chance.
- **What do I need to burn that log?** The greyed line's level, and its tooltip.
- **Where is that log on the Firemaking page?** Click its line.

## Good to know

- It is an estimate, not a promise: chances average out over many fires, not over ten.
- The numbers are counted the way Recipe Rates counts them; nobody has measured them on a character's
  burning yet.
- The bonfire time is shown for one fire only, and the game's Firemaking events and its rare events are not
  counted.
- The window starts nothing. A click on a line only selects the log's category and the log on the Firemaking
  page, as your own clicks there would, and scrolls its list; burning, the bonfire and the Start button are
  left as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Firemaking page | It comes a moment after the page opens. If it never comes, look for `[FiremakingRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` in a line | The game gives no time for that log yet. |
| A line without a picture | The game has no picture for that log yet, only its grey stand-in, and the window shows none rather than the stand-in. |
| "The preservation chance" instead of the game's name | The window has not read the name from the Firemaking page's list of the selected log's bonuses yet: it does once that list shows one. |
| A line's tooltip says the page has no room for the log's button beside this window | The window lies over the page's list of logs. Drag the window aside. |
| A click on a line opens nothing | `MelvorModLoader.log` says why when a game update changed the Firemaking page: "did not take the category" or "A click on a line opens nothing from now on". The numbers still work. |
| No button and no window at all | Look for `[FiremakingRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\FiremakingRates\FiremakingRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: FiremakingRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\FiremakingRates`.
Your save is never changed.
