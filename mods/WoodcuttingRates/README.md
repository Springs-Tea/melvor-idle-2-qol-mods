# Woodcutting Rates

> Which tree gives me the most XP, logs or gold per hour?

Every tree has its own time per hit, its own hit points, its own XP and its own logs, and some give other
items on the side. A tree takes several hits to cut, so the time a card shows is not the time of a log. To
compare the trees you would open them one by one and work out the hour in your head. Woodcutting Rates puts
them side by side: one line per tree, with what an hour there gives your character, from the game's own
numbers. Hover a line for the hour at that tree, and click it to see that tree on the Woodcutting page.

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

Until you move it, the window opens at the right side of the page, over its big panel and clear of the round
buttons, at the height where the most of the trees' cards can be shown whole beside it. Drag the window to
move it, drag its bottom right corner to resize it, or fold it down to its title with the button beside the X.
It is never larger than fits above the game's bottom bar, and a list of more than 14 trees scrolls. Its place,
size, fold and sort order are kept in `settings.json` in the mod's folder.

## What the window shows

One line per tree the Woodcutting page shows you, in the order of the page: the tree's picture, its name, its
category, and:

| Column | What it is |
| --- | --- |
| **Time** | How long one log takes: an hour over the logs of an hour. |
| **Logs** | The tree's logs per hour. |
| **XP** | Woodcutting XP per hour. |
| **Gold** | What the logs and the other items of an hour sell for. |

- **The hour rests on the cuts of an hour.** The logs come with a cut, as a tree's card lists them under "Full
  Cut", and so does the XP: a hit that does not cut gives none. The window works out how many hits a cut
  takes from the tree's hit points and your chances: a hit takes 1 hit point and a double hit 2, as measured
  in the game, and a weak spot and an instant cut take what the game's words for them say. See
  [What is counted](#what-is-counted).
- **The best number of each column is gold.**
- **Click a column's name to sort by it.** The first click puts the best trees on top, the next turns the
  order around. "Tree" goes back to the order of the page. The name the list is sorted by is gold, with the
  game's small arrow after it.
- **The tree the Woodcutting page shows** in its big panel, the one you are cutting, has the lit outline of a
  selected line, so it can be found in a sorted list.
- **Click a line to see that tree on the Woodcutting page**: when the page's category filter is on another
  category, it goes to the tree's category, as the page's own category button does, and the page's list of
  trees scrolls until the tree's whole card is in sight beside the window, above or below it when the window
  lies across the card's column. Woodcutting is not started or stopped, and the window stays open where it is.
  The pointer turns into a hand over a line a click changes something for, the tree you are cutting included
  when its card is out of sight; on a tree whose card is in sight beside the window already, a click does
  nothing.
- **When the page has no room for a card beside the window**, the line's tooltip says so, and a click does
  not scroll to it: drag the window aside, off the trees' cards. The card of a tree of another category is
  measured once its category is shown: a click on its line changes the filter, and from then on its tooltip
  says when there is no room.
- **A tree you cannot cut yet is greyed**, with the Woodcutting level it needs (`Lv 40`), or `Locked` when it
  needs something else. Its numbers come from the game's own values for it like the others, and it is never
  marked best. In a list sorted by a number, the locked trees come last.
- Trees the game keeps hidden from you are not listed until the game shows them. A tree you hid yourself on
  the Woodcutting page is listed only while the page lists hidden trees too, so the window lists the trees the
  page does.

## The tooltip of a line

Put the pointer on a line for the hour at that tree, as far as it is counted:

| Line | What it is |
| --- | --- |
| **Locked: ...** | What the tree is missing, in the game's own words. Only on a locked tree. |
| **You hid this tree on the Woodcutting page.** | Only on a tree you hid yourself. |
| **The tree the Woodcutting page shows.** | Only on the line with the lit outline. |
| **One hit every 1s: 3,600 hits an hour.** | The game's time of a hit at that tree, with your bonuses. |
| **Woodcutting XP: 21,637 an hour, 40.6 a cut.** | Per hour, and for one cut: the XP the tree's card shows. |
| **Hit points: 8, with your bonuses. A hit takes 1, a double hit 2.** | The tree's hit points, as its card shows them, and what a hit takes from them. |
| **A cut every 6.76 hits on average, worked out by a rule measured in the game: 533 an hour.** | The hits a cut takes, from the tree's hit points and your chances, and the cuts of an hour. |
| **Counted in the hits a cut takes:**, and a line per chance | Your chances of double damage, of a weak spot and of an instant cut at that tree, by the names the Woodcutting page gives them, when you have any. The weak spot's line says what one takes: `a weak spot taking 3.8: 3 + 10% of 8, not rounded`. |
| **Logs: Maple Logs, 1 a cut: 586 an hour.** | The tree's logs, how many a cut brings with your bonuses, and how many an hour brings. |
| **Every hit, a chance of:**, and a line per item | Each item a hit can bring, as the tree's card lists them under "Per Hit": its chance per hit, how many it brings when it is more than one, and how many an hour brings. |
| **Every cut, a chance of:**, and a line per item | Each item a cut can bring, with its chance per cut and how many an hour brings. |
| **The doubling chance (10%) is counted in the logs and items.** | Only when you have one. |
| **Gold: 1,234 an hour, ...** | What the logs and items of an hour sell for. |
| **Overgrowth on this tree now, its bonuses in the numbers above:**, and a line per effect | The overgrowth on the tree, by the game's names, with the stacks each has left. |
| **Click to show this tree on the Woodcutting page. Woodcutting is not started.** | What a click on the line does. Not there when a click changes nothing. |
| **The Woodcutting page has no room for this tree's card beside this window: drag the window aside.** | Instead of the line above, on two lines, when no scroll of the page's list shows the tree's whole card clear of the window. |
| **The Woodcutting page has no room to show this tree's whole card.** | Instead of the line above, when the page's list is smaller than a card, window or not. |

## What is counted

The numbers start from the game's own values for each tree: the time of a hit, the hit points, the XP, the
logs and how many come at a time, the items of a hit and of a cut with their chances, the doubling chance, the
chances of double damage, of a weak spot and of an instant cut, and the sell prices. They already hold your
level, equipment, potions and every other bonus, so the list moves with them.

| Rule | Where it comes from |
| --- | --- |
| A hit takes the tree's time of a hit. | The game's value for the tree. |
| The hits of an hour are an hour over the time of a hit. | The mod's. |
| A hit takes 1 hit point from the tree, and a double hit 2. | Measured in the game: counted on 597 hits of a Maple Tree, 484 of them taking 1 and the 113 double hits 2. |
| A weak spot takes 3 hit points and 10% of the tree's hit points, not rounded, instead of the hit's 1; a weak spot that is also a double hit takes twice that. | The game's own words, read as they say: "Your chance to hit a weak point, dealing 3 damage + 10% of the Tree's Max HP". No weak spot has been seen in the game yet, so whether the game rounds it is not known. |
| An instant cut cuts the tree with that hit. | The game's own words: "Your chance to instantly reduce a Tree's HP to 0 per hit". |
| The hits a cut takes come from the tree's hit points and these chances; the cuts of an hour are the hits of an hour over them. | The mod's, from the rules above. On the Maple Tree, 6.76 hits a cut were measured, and this rule gives 6.755. |
| A cut gives the tree's XP, and a hit that does not cut gives none. | Measured in the game: XP on 88 of 88 cuts and on none of 509 other hits. The XP tag's words, "The Skill XP gained per action.", fit it with a cut as the action. |
| The logs come with a cut, as many as the game's quantity for the tree. | The game's own words: a tree's card lists the logs under "Full Cut". |
| The items of a hit come at their chance on every hit, and the items of a cut at theirs on every cut. | The game's chances, and the game's own words: a tree's card lists the one under "Per Hit" and the other under "Full Cut". |
| The doubling chance doubles the logs and the items. | The mod's reading of the game's words for it, "Your chance to double the output". |
| The overgrowth on a tree now is in its numbers. | The mod's reading: the game keeps a tree's numbers with its overgrowth. Overgrowth a cut may put on later is not counted. |
| Gold is what the logs and the items sell for. | The game's sell prices. |
| Chances count as their long-run average. | The mod's. |

## Questions it answers

- **Where do I train Woodcutting?** The gold number under XP.
- **Is the tree I picked last week still the best for XP?** It is the line with the lit outline, the tree the
  Woodcutting page shows: compare it with the gold numbers.
- **What else does a hit or a cut bring?** The "Every hit" and "Every cut" lines of the tooltip, each with its
  chance.
- **What do I need to cut that tree?** The greyed line's level, and its tooltip.
- **Where is that tree on the Woodcutting page?** Click its line.

## Good to know

- It is an estimate, not a promise: chances average out over many hits, not over ten.
- How much a hit takes and when the XP comes were measured on one character's cutting. A weak spot and an
  instant cut did not come there, so for them the window follows the game's words.
- Woodcutting has no mastery XP in this game (its Mastery Guild counts ranks, points and tasks), so there is
  no column for it.
- The overgrowth on a tree now is in its numbers; what a cut may put on later is not counted.
- The window starts nothing. A click on a line only changes the page's category filter and where its list of
  trees is scrolled; woodcutting, the trees you hid and whether the page lists them are left as they are.

## When something is off

| You see | What it means |
| --- | --- |
| No round button on the Woodcutting page | It comes a moment after the page opens. If it never comes, look for `[WoodcuttingRates]` in `MelvorModLoader.log`: "the widget strip was not found" means a game update changed the screen. |
| `-` in a line | The game gives no time of a hit for that tree yet. |
| A line without a picture | The game has no picture for that tree yet, only its grey stand-in, and the window shows none rather than the stand-in. |
| "Chance of double damage" instead of the game's name | The window has not read the name from the Woodcutting page's lists of a tree's bonuses yet: it does once one of them has been shown. |
| A line's tooltip says the page has no room for the card beside this window | The window lies over the tree's card, and the page's list cannot scroll the card above or below it. Drag the window aside, off the trees' cards. |
| A click on a line only scrolls, or does nothing | `MelvorModLoader.log` says why when a game update changed the Woodcutting page: "the category filter was not found", "the category button did not set its category", or "A click on a line shows nothing from now on". The numbers still work. |
| No button and no window at all | Look for `[WoodcuttingRates] Stopped` in `MelvorModLoader.log`: the mod switched itself off after a game update. |

## Install

<p>Get the zip from <a href="https://github.com/Springs-Tea/melvor-idle-2-qol-mods/releases">Releases</a>.</p>

1. Install the [Melvor Mod Loader](https://www.nexusmods.com/melvoridle2/mods/2) (0.1.3 or newer).
2. Open the game folder: Steam > right-click Melvor Idle 2 > Manage > Browse local files.
3. Extract the mod's zip there, so the file ends up as `mods\WoodcuttingRates\WoodcuttingRates.dll`.
4. Start the game. `MelvorModLoader.log` in the game folder has a line `Mod loaded: WoodcuttingRates`.

To update, extract the new zip over the old one. To remove the mod, delete `mods\WoodcuttingRates`.
Your save is never changed.
