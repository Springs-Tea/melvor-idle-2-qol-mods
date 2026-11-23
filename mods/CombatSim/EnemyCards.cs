using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Components.CombatAreas;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace CombatSim;

/// <summary>
/// The simulator's answer on the enemy cards of the game's combat areas menu: Safe or Risky, XP and deaths per
/// hour, from the newest scan of that fight made with what the character has on now. A result made with another
/// setup is shown in grey and says so; a card without a result shows nothing, and so does a dungeon's enemy (a
/// dungeon is simulated whole). It only reads the history: opening the menu never starts a simulation. A click
/// on the line opens the simulator on the scan it comes from.
///
/// Found and kept up the way Dungeon Completions does it (DungeonCompletionsMod.Update): the menu is looked for
/// once a second until it exists, its cards four times a second while it is open, all inside a try. Anything
/// that fails switches the lines off with one line in the log (<see cref="CardReadings.Stop"/>); the simulator
/// goes on.
/// </summary>
sealed class EnemyCards
{
    const string LineName = "CombatSimLine";

    // The menu's list of enemy cards, and what each card shows, are private to the game's views.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EnemySelect")]
    static extern SelectEnemyPanel EnemySelect(CombatAreasMenu menu);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EnemyContainer")]
    static extern VBoxContainer EnemyContainer(SelectEnemyPanel panel);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Area")]
    static extern CombatArea? AreaOf(EnemyOptionsPanel card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Enemy")]
    static extern Enemy? EnemyOf(EnemyOptionsPanel card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label NameOf(EnemyOptionsPanel card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_FightButton")]
    static extern Button FightOf(EnemyOptionsPanel card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_UnlockedContainer")]
    static extern MarginContainer UnlockedOf(EnemyOptionsPanel card);

    /// <summary>Our tag on one of the game's cards, and the answer it shows.</summary>
    sealed class Line(PanelContainer tag, Label verdict, Label rest)
    {
        public readonly PanelContainer Tag = tag;
        public readonly Label Verdict = verdict, Rest = rest;
        public Scan? Scan;
        public SimResult? Result;
#if DEVTOOLS
        public string Decision = "";
#endif
    }

    readonly ModContext _ctx;
    readonly SimHistory _history;
    readonly Func<Game?> _game;
    readonly Func<CombatEntity?> _combat;
    readonly Func<bool> _enabled;
    readonly Action _readHistory;
    readonly Action<Scan, SimResult> _open;
    readonly GameStyle _style = new();
    readonly Tempo _tick = new(250), _search = new(1000);
    // Keyed by the game's card. The menu keeps a pool of cards and binds them to other enemies as it goes.
    readonly Dictionary<Control, Line> _lines = new();
    // The game's CombatAreasMenu; kept as a Control, so this class still loads when that type is gone.
    Control? _menu;
    Game? _seen;
    bool _addedLogged;

    public EnemyCards(ModContext ctx, SimHistory history, Func<Game?> game, Func<CombatEntity?> combat, Func<bool> enabled,
        Action readHistory, Action<Scan, SimResult> open)
    {
        _ctx = ctx;
        _history = history;
        _game = game;
        _combat = combat;
        _enabled = enabled;
        _readHistory = readHistory;
        _open = open;
    }

    public void OnFrame()
    {
        if (!_tick.Due()) return;   // four times a second is plenty
        if (CardReadings.Off) { if (_lines.Count > 0) Clear(); return; }
        try { Update(); }
        catch (Exception e)
        {
            // A private member or a node of the card that is gone after a game update ends up here.
            CardReadings.Stop(_ctx.Log, e);
            Clear();
        }
    }

    void Update()
    {
        // The character the simulator follows, and only while the game has it open too.
        Game? game = _game();
        if (!ReferenceEquals(game, _seen)) { Clear(); _seen = game; _menu = null; }
        if (game == null || !ReferenceEquals(game, GameAccess.CurrentGame) || _combat() is not { } combat) return;
        if (!_enabled()) { if (_lines.Count > 0) Clear(); return; }
#if DEVTOOLS
        DevRequest(game, combat);
#endif
        if (_menu == null || !GodotObject.IsInstanceValid(_menu))
        {
            _menu = null;
            // The menu is part of the combat screen, which is built when it is first opened. Looking for it
            // walks the whole screen, so that is done once a second.
            if (!_search.Due()) return;
            _menu = GameAccess.FindNode<CombatAreasMenu>(GameAccess.FindGameView());
            if (_menu == null) return;
        }
        if (!_menu.IsVisibleInTree()) return;

        foreach (Control gone in _lines.Keys.Where(card => !GodotObject.IsInstanceValid(card)).ToList()) _lines.Remove(gone);
        // A dungeon's enemies are simulated as the whole dungeon: their cards get nothing.
        CombatArea? area = combat.Areas.VisibleArea?.Area as CombatArea;
        string? setup = null, code = null;
        foreach (Node child in EnemyContainer(EnemySelect((CombatAreasMenu)_menu)).GetChildren())
        {
            if (child is not EnemyOptionsPanel card || !card.IsVisibleInTree()) continue;
            _lines.TryGetValue(card, out Line? line);
            (Scan Scan, SimResult Result, bool Same)? answer = null;
            // The card must be bound to the area on show (the pool may still hold an old binding) and unlocked.
            if (area != null && AreaOf(card) == area && EnemyOf(card) is { } enemy && UnlockedOf(card).IsVisibleInTree())
            {
                _readHistory();
                if (_history.Scans.Count > 0)
                {
                    setup ??= CardSetup.Describe(game, combat);
                    code ??= CardSetup.Code(setup);
                    answer = _history.ForCard(area.Id, enemy.Id, code);
                }
            }
            if (answer is not { } found)
            {
                if (line != null) line.Tag.Visible = false;
                continue;
            }
            line ??= Add(card);
            Show(line, found.Scan, found.Result, found.Same, setup!);
        }
    }

    // The line goes into the card's content column, as its last row: the outermost VBoxContainer of the card
    // that holds both the enemy's name and its Fight button (a column as wide as the card), or else the
    // outermost one that holds the name. At the column's end the line sits under everything the column shows
    // and moves nothing sideways. While the enemy is locked, Update hides it.
    static VBoxContainer? ColumnOf(EnemyOptionsPanel card)
    {
        Button fight = FightOf(card);
        VBoxContainer? withName = null, withBoth = null;
        Node? node = NameOf(card).GetParent();
        for (; node != null && node != card; node = node.GetParent())
        {
            if (node is not VBoxContainer column) continue;
            withName = column;
            if (column.IsAncestorOf(fight)) withBoth = column;
        }
        return node == card ? withBoth ?? withName : null;
    }

    Line Add(EnemyOptionsPanel card)
    {
        VBoxContainer column = ColumnOf(card) ?? throw new InvalidOperationException("the enemy card has no column for the line");
#if DEVTOOLS
        DevLayout(card, column);
#endif
        // The tag of Dungeon Completions in this menu (GameStyle.CountTag), with the simulator's own picture
        // (the one on its round button, CombatSimMod.UpdateStripButton) and the verdict where its number is.
        PanelContainer tag = _style.CountTag(GameStyle.Icon("Statistics") ?? GameStyle.Icon("AttacksIcon") ?? GameStyle.Icon("Slayer"), out Label verdict);
        tag.Name = LineName;
        tag.SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin;
        tag.MouseFilter = Control.MouseFilterEnum.Stop;
        tag.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
        tag.Theme = _style.TooltipTheme();   // its tooltip on the game's tooltip panel
        Label rest = _style.Text("", GameStyle.TextPaper, 12, GameStyle.SemiBold);
        rest.MouseFilter = Control.MouseFilterEnum.Ignore;
        verdict.GetParent().AddChild(rest);
        // Under the pointer its border lights up, as the tags that lead somewhere do (GameStyle.LinkTag).
        if (tag.GetThemeStylebox("panel") is StyleBoxFlat box)
        {
            Color resting = box.BorderColor;
            tag.MouseEntered += () => box.BorderColor = GameStyle.Highlight;
            tag.MouseExited += () => box.BorderColor = resting;
        }
        var line = new Line(tag, verdict, rest);
        // On release over the tag, as the game's buttons do.
        tag.GuiInput += input =>
        {
            if (input is not InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: false } click) return;
            tag.AcceptEvent();
            if (new Rect2(Vector2.Zero, tag.Size).HasPoint(click.Position)) Open(line);
        };
        column.AddChild(tag);
        _lines[card] = line;
        if (!_addedLogged) { _addedLogged = true; _ctx.Log("Tags added to the enemy cards in the combat areas menu."); }
        return line;
    }

    void Show(Line line, Scan scan, SimResult r, bool same, string setup)
    {
        line.Scan = scan;
        line.Result = r;
        // The figures of the simulator's table: XP as the scan ranked it (the trained skill's in Best for),
        // deaths per hour.
        string skill = scan.BestMode && scan.TrainSkillName != "" ? " " + scan.TrainSkillName : "";
        string deaths = SimView.Fmt(r.DeathsPerHour);
        string rest = $"{SimView.Fmt(scan.Rank(r))}{skill} XP/h · " +
                      (r.Deaths == 0 ? "no deaths" : deaths == "1" ? "1 death/h" : deaths + " deaths/h");
        string verdict = !same ? "Other setup" : r.Safe is { } safe ? safe ? "Safe" : "Risky" : "";
        Color tint = !same ? GameStyle.TextDim : r.Safe is { } good ? good ? GameStyle.Good : GameStyle.Bad : GameStyle.TextPaper;
        Set(line.Verdict, verdict, tint);
        line.Verdict.Visible = verdict != "";
        Set(line.Rest, rest, same ? GameStyle.TextPaper : GameStyle.TextDim);
        string tip = Tip(scan, r, same);
        if (line.Tag.TooltipText != tip) line.Tag.TooltipText = tip;
        line.Tag.Visible = true;
#if DEVTOOLS
        string decision = $"{r.Target.AreaId}/{r.Target.EnemyId}: '{verdict} {rest}' from scan {scan.Id} ({(same ? "this setup" : "another setup")}), " +
                          $"XP/h {scan.Rank(r):0}, deaths/h {r.DeathsPerHour:0.###}, biggest hit {r.MaxEnemyHit}, auto eat at {r.AutoEatHp:0.#} HP";
        if (line.Decision == decision) return;
        line.Decision = decision;
        _ctx.Log("Dev: enemy card " + decision);
        if (!same) DevSetups(r, setup);
#endif
    }

    static void Set(Label label, string text, Color color)
    {
        if (label.Text != text) label.Text = text;
        if (label.GetThemeColor("font_color") != color) label.AddThemeColorOverride("font_color", color);
    }

    static string Tip(Scan scan, SimResult r, bool same)
    {
        var tip = new StringBuilder();
        // The stance the scan fought in (issue #51); a scan made before that was kept names the one of a Best for line.
        string stance = r.StanceShown != "" ? $", {r.StanceShown} stance" : "";
        tip.Append(same ? "Combat Simulator: your newest scan with what you have on now, " : "Combat Simulator: your newest scan of this fight, ")
           .Append(SimView.Ago(scan.At)).Append('\n')
           .Append($"{SimText.Clock(r.Hours * 3600)} of fighting{stance}, {(scan.Unlimited ? "unlimited supplies" : "supplies from the bank")}\n");
        if (!same)
            tip.Append("It was made with other equipment, stance, spell, food or prayers than you have on now.\n")
               .Append("Simulate again for Safe or Risky.\n");
        else if (r.Deaths > 0) tip.Append("Risky: you died in this scan.\n");
        else if (r is { MaxEnemyHit: { } hit, AutoEatHp: { } eat })
            tip.Append(eat <= 0 ? "Risky: auto eat cannot heal you here (no food selected, none left, or no auto eat).\n"
                : hit < eat ? $"Safe: no deaths, and the biggest hit ({hit:N0}) stays under your auto-eat point ({eat:#,0.#} HP).\n"
                : $"Risky: the biggest hit ({hit:N0}) does not stay under your auto-eat point ({eat:#,0.#} HP).\n");
        if (r.Note != "") tip.Append(r.Note).Append('\n');
        return tip.Append("Click to see the scan in the simulator").ToString();
    }

    void Open(Line line)
    {
        if (GameAccess.IsComputingOffline || line.Scan == null || line.Result == null) return;
        try { _open(line.Scan, line.Result); }
        catch (Exception e) { _ctx.Log("The simulator could not be opened from an enemy card: " + e.Message); }
    }

    void Clear()
    {
        foreach (Line line in _lines.Values)
        {
            if (!GodotObject.IsInstanceValid(line.Tag)) continue;
            line.Tag.Visible = false;
            line.Tag.QueueFree();
        }
        _lines.Clear();
    }

#if DEVTOOLS
    // Development: a file "cards.request" in the mod's folder holds the names of areas (or parts of them),
    // joined by "+". It works the menu the way a player's hand does. With another screen in front, the combat
    // screen is opened first, the way the sidebar opens it. The simulator window is closed while the request runs,
    // so it covers neither the menu's buttons nor the cards (CombatSimMod.DevCardsAside), and opened again at the
    // end as it was. The menu is opened with the combat screen's own button for it (when that button cannot be
    // pressed, it is shown directly, as before). Then, area by area, the
    // area's category button at the top of the menu and the area's button in its left-hand list are pressed, and
    // once that area's cards are on show, what is really there goes into the log: the menu's category and area,
    // the cards and the tags on them, and where each tag sits against its card, the name and the Fight and View
    // stats buttons. The first card's nodes go into "tree-dump-cards-<n>.txt" and a picture into
    // "shot-cards-<n>.png"; an area that did not come on show gets one line and no picture. At the end the area
    // the menu showed before is pressed again, the menu is closed if it was closed, and the screen that was in
    // front comes back; with a file "cards.keep" the menu stays as it is. Nothing but those buttons is ever
    // pressed (DevPress): no Fight button, nothing that starts or changes anything for the character.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewStatsButton")]
    static extern Button ViewStatsOf(EnemyOptionsPanel card);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Dungeon")]
    static extern Dungeon? DungeonOf(EnemyOptionsPanel card);
    // The menu's buttons and what it shows, private to the game's views (read as DungeonCompletionsMod reads
    // the area buttons and the info panel).
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Area")]
    static extern ActiveCombatArea? AreaOfButton(CombatAreaButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameOfAreaButton(CombatAreaButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ActiveCategory")]
    static extern ActiveCombatAreaCategory? CategoryOfButton(CombatAreaCategoryButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameOfCategoryButton(CombatAreaCategoryButton button);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AreaInfo")]
    static extern CombatAreaInfoPanel? InfoOf(CombatAreasMenu menu);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NoAreaLabel")]
    static extern Label? NoAreaOf(CombatAreasMenu menu);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NameLabel")]
    static extern Label? NameOfInfo(CombatAreaInfoPanel info);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_NoAreaSelectedLabel")]
    static extern Label? NoAreaOfInfo(CombatAreaInfoPanel info);
    // The combat screen's button that opens the menu, and the menu it opens.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ViewAreasButton")]
    static extern GamesByMalcs.Melvor2.CustomControls.PanelButtonContainer? ViewAreasOf(CombatActiveView view);
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_AreaMenu")]
    static extern CombatAreasMenu? AreaMenuOf(CombatActiveView view);
    // The screen in front, private to the game's view.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_CurrentPage")]
    static extern (GamesByMalcs.Melvor2.GameMenus.Pages.GamePage, GamesByMalcs.Melvor2.GameMenus.Pages.ActiveGamePage)? CurrentPageOf(
        GamesByMalcs.Melvor2.GameMenus.GameView view);

    /// <summary>
    /// Set by CombatSimMod: the first closes the simulator window if it is open, the second opens it again as it
    /// was. Each says what it did, or nothing when there was nothing to do.
    /// </summary>
    public Func<string>? DevWindowAside, DevWindowBack;

    // Open: the menu comes on show. Pick: the next area of the request. Category and Area: their buttons are
    // pressed, unless the menu shows them already. Cards: until the area's cards are on show. Shot: the report
    // and the picture. Close, End and Done: the menu, the screen and the window go back, and are read back.
    enum DevPhase { Open, Pick, Category, Area, Cards, Shot, Close, End, Done }

    string[]? _devAreas;
    DevPhase _devPhase;
    int _devStep, _devLayouts;
    ulong _devAt, _devAsked;
    // What the step is to show; while _devRestoring, what the menu showed before the request.
    CombatAreaCategory? _devCategory;
    ActiveCombatArea? _devArea;
    CombatAreaCategory? _devCategoryBefore;
    ActiveCombatArea? _devAreaBefore;
    GamesByMalcs.Melvor2.GameMenus.Pages.GamePage? _devPageBefore;
    Rect2 _devMenuRect;
    bool _devWasOpen, _devBeforeDumped, _devReady, _devPageOpened, _devActed, _devShownDirectly, _devRestoring;

    bool DevKeep => File.Exists(Path.Combine(_ctx.Directory, "cards.keep"));

    void DevRequest(Game game, CombatEntity combat)
    {
        try { DevStep(game, combat); }
        catch (Exception e) { DevEnd("Dev: cards request failed: " + e); }
    }

    // The request ends here, wherever it is: no press goes on, and the simulator window comes back.
    void DevEnd(string line)
    {
        _devAreas = null;
        _devButton = null;
        _ctx.Log(line);
        try
        {
            if (DevWindowBack?.Invoke() is { Length: > 0 } back) _ctx.Log("Dev: cards: " + back + ".");
        }
        catch (Exception e) { _ctx.Log("Dev: cards: the simulator window could not be opened again: " + e.Message); }
    }

    void DevNext(DevPhase phase, ulong at)
    {
        _devPhase = phase;
        _devAt = at;
        _devActed = false;
        _devPressed = null;
    }

    void DevStep(Game game, CombatEntity combat)
    {
        string request = Path.Combine(_ctx.Directory, "cards.request");
        ulong now = Time.GetTicksMsec();
        if (_devAreas == null)
        {
            if (!File.Exists(request)) return;
            string text = File.ReadAllText(request).Trim();
            File.Delete(request);
            _devAreas = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            _devStep = 0;
            _devAsked = now;
            _devReady = false;
            _devPageOpened = false;
            _devPageBefore = null;
            _devShownDirectly = false;
            _devRestoring = false;
            _devButton = null;
            _devMenuRect = default;
            _devCategoryBefore = combat.Areas.VisibleCategory;
            _devAreaBefore = combat.Areas.VisibleArea;
            DevNext(DevPhase.Open, now);
            _ctx.Log($"Dev: cards: colours Good #{GameStyle.Good.ToHtml()}, Bad #{GameStyle.Bad.ToHtml()} (the game's modifier colours), " +
                     $"grey #{GameStyle.TextDim.ToHtml()}, paper #{GameStyle.TextPaper.ToHtml()}");
        }
        if (!_devReady)
        {
            if (!DevMenuReady(game, combat, now)) return;
            _devReady = true;
            _devWasOpen = _menu!.IsVisibleInTree();
            // Before anything is pressed: the window may lie over the menu's buttons as well as over its cards.
            DevAside();
            DevNext(DevPhase.Open, now);
        }
        if (_menu == null || !GodotObject.IsInstanceValid(_menu))
        {
            DevEnd(_devPhase == DevPhase.Done ? $"Dev: cards: done; the combat areas menu went with the combat screen; {DevFront()}."
                : "Dev: cards: the combat areas menu is gone (another character, or the combat screen was built again); the request stopped.");
            return;
        }
        var menu = (CombatAreasMenu)_menu;
        if (DevPressing(now)) return;
        string name = _devArea?.DisplayName.Text ?? "";
        switch (_devPhase)
        {
            case DevPhase.Open:
                if (menu.IsVisibleInTree())
                {
                    // While the menu opens (an animation) its buttons move: wait until it holds still.
                    Rect2 rect = menu.GetGlobalRect();
                    if (rect != _devMenuRect) { _devMenuRect = rect; return; }
                    string how = _devWasOpen ? "it was open already" : _devShownDirectly ? "shown directly" : "with the combat screen's button for it";
                    _ctx.Log($"Dev: cards: the combat areas menu is open ({how}); {DevOnScreen(menu, combat)}");
                    DevNext(DevPhase.Pick, now);
                }
                else if (!_devActed)
                {
                    _devActed = true;
                    _devAt = now;
                    var active = GameAccess.FindNode<CombatActiveView>(GameAccess.FindGameView());
                    var open = active != null && AreaMenuOf(active) == menu ? ViewAreasOf(active) : null;
                    if (open != null && open.IsVisibleInTree()) DevPress(open, null, "the combat screen's button for the combat areas menu");
                    else DevShowDirectly("the combat screen's button for it was not found on show");
                }
                else if (_devPressed == false && !_devShownDirectly) DevShowDirectly("its button was not pressed");
                else if (now - _devAt > 6000)
                    DevEnd("Dev: cards: the combat areas menu did not come on show within 6 seconds" +
                           (_devShownDirectly ? " of being shown directly" : " of pressing its button") + "; no pictures were taken.");
                return;

            case DevPhase.Pick:
                if (_devStep >= _devAreas!.Length) { DevPutMenuBack(now); return; }
                string piece = _devAreas[_devStep];
                var (category, shown) = combat.Areas.ActiveCategories.Values.SelectMany(c => c.Areas.Select(a => (c, a)))
                    .FirstOrDefault(x => x.a.DisplayName.Text.Contains(piece, StringComparison.OrdinalIgnoreCase));
                if (shown == null) { _ctx.Log($"Dev: cards {_devStep + 1}: no area is called '{piece}'; not pictured."); _devStep++; return; }
                _devCategory = category.Category;
                _devArea = shown;
                DevNext(DevPhase.Category, now);
                return;

            case DevPhase.Category:
            {
                string wanted = _devCategory?.DisplayName.Text ?? "";
                // Not pressed when the game has the category and its list on show shows the area's button: what
                // the game holds and what the menu shows are both read, as they may differ.
                bool listed = _devArea == null || DevAreaButton(menu) != null;
                if (_devCategory == null || (combat.Areas.VisibleCategory == _devCategory && listed)) { DevNext(DevPhase.Area, now); return; }
                if (!_devActed)
                {
                    _devActed = true;
                    _devAt = now;
                    var button = DevFind<CombatAreaCategoryButton>(menu).FirstOrDefault(b => b.IsVisibleInTree() && CategoryOfButton(b)?.Category == _devCategory);
                    if (button == null) DevMissed($"the menu has no button on show for the category '{wanted}'", menu, combat, now);
                    else DevPress(button, NameOfCategoryButton(button), $"the '{wanted}' category button at the top of the menu");
                }
                else if (_devPressed == false || now - _devAt > 6000)
                    DevMissed(_devPressed == false ? $"the '{wanted}' category button was not pressed"
                        : combat.Areas.VisibleCategory != _devCategory ? $"6 seconds after its button was pressed, the category '{wanted}' is not on show"
                        : $"6 seconds after the '{wanted}' button was pressed, the menu's left-hand list has no button on show for '{name}'", menu, combat, now);
                return;
            }

            case DevPhase.Area:
                if (_devArea == null) { DevNext(DevPhase.Close, now); return; }
                if (_devPressed == false) { DevMissed($"the '{name}' button was not pressed", menu, combat, now); return; }
                // Without a press only when the game has the area and the menu shows its cards too.
                if (combat.Areas.VisibleArea?.Area == _devArea.Area && (_devPressed == true || _devRestoring || DevCardsOf(menu, _devArea.Area).Bound > 0))
                {
                    if (_devRestoring) DevNext(DevPhase.Close, now);
                    // The cards are waited for from the click (or from now, when the menu showed the area already).
                    else DevNext(DevPhase.Cards, _devPressed == true ? _devAt : now);
                    return;
                }
                if (!_devActed)
                {
                    // After a category is pressed, the menu may still be putting that category's areas into its list.
                    var button = DevAreaButton(menu);
                    if (button != null)
                    {
                        _devActed = true;
                        _devAt = now;
                        DevPress(button, NameOfAreaButton(button), $"the '{name}' button in the menu's left-hand list");
                    }
                    else if (now - _devAt > 6000) DevMissed($"the menu's left-hand list has no button on show for '{name}'", menu, combat, now);
                }
                else if (now - _devAt > 6000) DevMissed($"6 seconds after its button was pressed, '{name}' is not the menu's area", menu, combat, now);
                return;

            case DevPhase.Cards:
            {
                // Two seconds for the menu to bind and lay out the cards, as CombatGoals' Dev.AreasRequest waits
                // (the tags go on four times a second); then, until every card on show is one of this area's, as
                // long as a step of the Astrology check may take (AstrologyOverviewMod.DevRequest, 6 seconds).
                var (cards, bound) = DevCardsOf(menu, _devArea!.Area);
                if (now - _devAt < 2000 || ((cards == 0 || bound < cards) && now - _devAt < 6000)) return;
                DevNext(DevPhase.Shot, now);
                return;
            }

            case DevPhase.Shot:
                // A window closed now is still in the frame drawn last: the picture waits for the next update.
                if (DevAside()) return;
                DevReport(_devStep + 1, menu, game, combat);
                _devStep++;
                DevNext(DevPhase.Pick, now);
                return;

            case DevPhase.Close:
                if (_devWasOpen || DevKeep || !menu.IsVisibleInTree()) { DevNext(DevPhase.End, now); return; }
                if (!_devActed)
                {
                    _devActed = true;
                    _devAt = now;
                    menu.Close();
                }
                else if (now - _devAt > 6000)
                {
                    _ctx.Log("Dev: cards: the combat areas menu was asked to close and is still open 6 seconds later.");
                    DevNext(DevPhase.End, now);
                }
                return;

            case DevPhase.End:
                if (!DevKeep && GameAccess.FindGameView() is { } view) DevPageBack(view);
                DevNext(DevPhase.Done, now);
                return;

            case DevPhase.Done:
                // Read back an update after the screen went back.
                DevEnd($"Dev: cards: done{(DevKeep ? " (cards.keep: the menu stays as it is)" : "")}; the combat areas menu is " +
                       $"{(menu.IsVisibleInTree() ? "open" : "closed")} (it was {(_devWasOpen ? "open" : "closed")} before), {DevOnScreen(menu, combat)}; {DevFront()}.");
                return;
        }
    }

    string DevFront()
    {
        string front = GameAccess.FindGameView() is { } view ? CurrentPageOf(view)?.Item1?.Id ?? "no screen" : "no game view";
        return $"in front: {front} ({(_devPageBefore != null ? $"{_devPageBefore.Id} was in front before the request" : "the screen that was in front before the request")})";
    }

    // Shown the way the helper showed it before, and as CombatGoals' Dev.AreasRequest shows it.
    void DevShowDirectly(string why)
    {
        _devShownDirectly = true;
        _ctx.Log($"Dev: cards: {why}; the combat areas menu is shown directly (Visible = true).");
        _menu!.Visible = true;
    }

    // A step whose area did not come on show: one line and no picture.
    void DevMissed(string why, CombatAreasMenu menu, CombatEntity combat, ulong now)
    {
        if (_devRestoring)
        {
            _ctx.Log($"Dev: cards: the menu's area before the request was not put back: {why}; {DevOnScreen(menu, combat)}.");
            DevNext(DevPhase.Close, now);
            return;
        }
        _ctx.Log($"Dev: cards {_devStep + 1}: not pictured: {why}; {DevOnScreen(menu, combat)}.");
        _devStep++;
        DevNext(DevPhase.Pick, now);
    }

    // After the last area: the area the menu showed before is pressed again (not with cards.keep).
    void DevPutMenuBack(ulong now)
    {
        _devRestoring = true;
        _devCategory = _devCategoryBefore;
        _devArea = _devAreaBefore;
        DevNext(_devCategory == null || DevKeep ? DevPhase.Close : DevPhase.Category, now);
    }

    // The simulator window closed if it is open; true when it was closed just now.
    bool DevAside()
    {
        string done = DevWindowAside?.Invoke() ?? "";
        if (done.Length > 0) _ctx.Log("Dev: cards: " + done + ".");
        return done.Length > 0;
    }

    // What the menu really shows: the game's category and area, what its info panel and its "no area" text say,
    // and the cards on show.
    string DevOnScreen(CombatAreasMenu menu, CombatEntity combat)
    {
        bool open = menu.IsVisibleInTree();
        CombatAreaInfoPanel? info = InfoOf(menu);
        Label? title = info == null ? null : NameOfInfo(info);
        Label? none = new[] { NoAreaOf(menu), info == null ? null : NoAreaOfInfo(info) }.FirstOrDefault(l => l != null && l.IsVisibleInTree());
        var text = new StringBuilder(open ? "the menu shows" : "the menu (closed) is set to")
            .Append($" category '{combat.Areas.VisibleCategory?.DisplayName.Text ?? "none"}', area '{combat.Areas.VisibleArea?.DisplayName.Text ?? "none"}'");
        if (!open) return text.ToString();
        text.Append(title is { } t && t.IsVisibleInTree() ? $", its info panel names '{t.Text}'" : ", its info panel names no area");
        if (none != null) text.Append($", and it reads '{none.Text.Replace("\n", " ")}'");
        var (cards, bound) = DevCardsOf(menu, combat.Areas.VisibleArea?.Area);
        return text.Append($"; {cards} enemy cards on show, {bound} of them of that area").ToString();
    }

    // The enemy cards on show, and how many of them are bound to the area (a combat area or a dungeon).
    static (int Shown, int Bound) DevCardsOf(CombatAreasMenu menu, BaseCombatArea? area)
    {
        int shown = 0, bound = 0;
        foreach (Node child in EnemyContainer(EnemySelect(menu)).GetChildren())
        {
            if (child is not EnemyOptionsPanel card || !card.IsVisibleInTree()) continue;
            shown++;
            if (area != null && ((BaseCombatArea?)AreaOf(card) ?? DungeonOf(card)) == area) bound++;
        }
        return (shown, bound);
    }

    // The step's area's button in the menu's left-hand list, when it is on show.
    CombatAreaButton? DevAreaButton(CombatAreasMenu menu) =>
        _devArea == null ? null : DevFind<CombatAreaButton>(menu).FirstOrDefault(b => b.IsVisibleInTree() && AreaOfButton(b)?.Area == _devArea.Area);

    // The menu's buttons of one kind, found the way DungeonCompletionsMod.Collect finds the area buttons.
    static IEnumerable<T> DevFind<T>(Node node) where T : Node
    {
        foreach (Node child in node.GetChildren())
        {
            if (child is T found) yield return found;
            else foreach (T inner in DevFind<T>(child)) yield return inner;
        }
    }

    // ---- a press of one of the game's buttons, as a player's hand makes it -------------------------------------

    // With the clicks of CombatSimMod.DevClick. First the list the button is in is scrolled to it (as
    // AstrologyOverview's check scrolls to a row); then the pointer goes over the button's name, or else over its
    // middle; and only when the game says, an update later, that what is under the pointer is that button or a
    // part of it that passes a click on to it, is the click made. So a click never lands on anything else, a
    // Fight button least of all. Then the pointer goes back where it was.
    Control? _devButton, _devButtonName;
    string _devWhat = "";
    int _devPressStage;
    Vector2 _devSpot, _devPointer;
    // How the last press ended: true clicked, false not clicked, null none ended in this phase.
    bool? _devPressed;

    void DevPress(Control button, Control? name, string what)
    {
        _devButton = button;
        _devButtonName = name;
        _devWhat = what;
        _devPressStage = 0;
        _devPressed = null;
    }

    // One update of the press under way; true while it goes on.
    bool DevPressing(ulong now)
    {
        if (_devButton is not { } button) return false;
        if (!GodotObject.IsInstanceValid(button) || !button.IsVisibleInTree()) return DevPressed(false, "it is no longer on show", now);
        if (button is GamesByMalcs.Melvor2.CustomControls.BaseButtonContainer { Disabled: true } or BaseButton { Disabled: true })
            return DevPressed(false, "it is disabled", now);
        ScrollContainer? scroll = null;
        for (Node? node = button.GetParent(); node != null && scroll == null; node = node.GetParent()) scroll = node as ScrollContainer;
        switch (_devPressStage)
        {
            case 0:
                scroll?.EnsureControlVisible(button);
                _devPointer = _ctx.Tree.Root.GetMousePosition();
                _devPressStage = _devButtonName is { } label && GodotObject.IsInstanceValid(label) && label.IsVisibleInTree() ? 1 : 3;
                return true;
            case 1:
            case 3:
            {
                // The part of its name (1) or of the whole button (3) that is in sight.
                Rect2 rect = (_devPressStage == 1 ? _devButtonName! : button).GetGlobalRect();
                if (scroll != null) rect = rect.Intersection(scroll.GetGlobalRect());
                rect = rect.Intersection(_ctx.Tree.Root.GetVisibleRect());
                if (!rect.HasArea())
                {
                    if (_devPressStage == 1) { _devPressStage = 3; return true; }
                    return DevPressed(false, "no part of it is in sight", now);
                }
                _devSpot = rect.GetCenter();
                DevPointer(_devSpot);
                _devPressStage++;
                return true;
            }
            default:
            {
                Control? over = button.GetViewport().GuiGetHoveredControl();
                if (!DevPassesOn(over, button))
                {
                    _ctx.Log($"Dev: cards: the pointer over {(_devPressStage == 2 ? "the name" : "the middle")} of {_devWhat} is on {DevName(over)}, " +
                             "which would not pass a click on to it.");
                    if (_devPressStage == 2) { _devPressStage = 3; return true; }
                    DevPointer(_devPointer);
                    return DevPressed(false, "nothing under the pointer passes a click on to it", now);
                }
                Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devSpot;
                Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
                Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
                DevPointer(_devPointer);
                return DevPressed(true, $"the pointer was on {DevName(over)}", now);
            }
        }
    }

    bool DevPressed(bool clicked, string how, ulong now)
    {
        _ctx.Log(clicked ? $"Dev: cards: clicked {_devWhat} ({how})." : $"Dev: cards: did not click {_devWhat}: {how}.");
        _devButton = _devButtonName = null;
        _devPressed = clicked;
        _devAt = now;
        return false;
    }

    void DevPointer(Vector2 at)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    // The button itself, or a part of it that passes mouse events up to it (MouseFilter Pass all the way) and is
    // no button of its own.
    static bool DevPassesOn(Control? over, Control button)
    {
        for (Node? node = over; node != null; node = node.GetParent())
        {
            if (node == button) return true;
            if (node is not Control part || part.MouseFilter != Control.MouseFilterEnum.Pass ||
                part is BaseButton or GamesByMalcs.Melvor2.CustomControls.BaseButtonContainer) return false;
        }
        return false;
    }

    static string DevName(Control? control) => control == null ? "nothing"
        : $"{control.Name} ({control.GetType().Name}{(control is Label label ? $" '{label.Text}'" : "")}, mouse filter {control.MouseFilter})";

    // ---- the combat screen ------------------------------------------------------------------------------------

    // The combat areas menu is part of the combat screen, which the game builds when that screen comes to the
    // front. With another screen in front, the combat screen is opened the way the sidebar opens a screen
    // (AstrologyOverviewMod.DevPlan, "open the Astrology page"); then the menu is looked for on every update
    // until it is there, for as long as a step of that check may take (6 seconds).
    bool DevMenuReady(Game game, CombatEntity combat, ulong now)
    {
        var view = GameAccess.FindGameView() ?? throw new InvalidOperationException("the game's view was not found");
        var front = CurrentPageOf(view)?.Item1;
        if (front is GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage { Entity: var shown } && ReferenceEquals(shown, combat))
        {
            if (_menu == null || !GodotObject.IsInstanceValid(_menu)) _menu = GameAccess.FindNode<CombatAreasMenu>(view);
            if (_menu != null) return true;
        }
        else if (!_devPageOpened)
        {
            // Both sidebars' options (the game has a setting for the older one); they lead to the same screens.
            var option = game.Layout.Sidebar.Options.Concat(game.Layout.Sidebar.LegacyOptions).FirstOrDefault(o =>
                o.Page is GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage p && ReferenceEquals(p.Entity, combat) &&
                o.Requirements?.AreMet != false && o.DisplayRequirements?.AreMet != false);
            if (option?.Page is not GamesByMalcs.Melvor2.GameMenus.Pages.EntityPage page)
            {
                _devAreas = null;
                _ctx.Log("Dev: cards: the sidebar has no combat screen that can be opened; no pictures were taken.");
                return false;
            }
            _devPageBefore = front;
            _devPageOpened = true;
            view.SetCurrentPage(page);
            _ctx.Log($"Dev: cards: opened the combat screen the way the sidebar does ({front?.Id ?? "no screen"} was in front).");
            return false;
        }
        if (now - _devAsked < 6000) return false;
        _devAreas = null;
        _ctx.Log("Dev: cards: the combat areas menu was not found within 6 seconds" +
                 (_devPageOpened ? " of opening the combat screen" : ", with the combat screen in front") +
                 $" ({front?.Id ?? "no screen"} is in front); no pictures were taken.");
        DevPageBack(view);
        return false;
    }

    // The screen that was in front before the request opened the combat screen.
    void DevPageBack(GamesByMalcs.Melvor2.GameMenus.GameView view)
    {
        if (!_devPageOpened || _devPageBefore == null) return;
        _devPageOpened = false;
        view.SetCurrentPage(_devPageBefore);
    }

    // ---- what the picture shows -------------------------------------------------------------------------------

    void DevReport(int n, CombatAreasMenu menu, Game game, CombatEntity combat)
    {
        BaseCombatArea? onShow = combat.Areas.VisibleArea?.Area;
        string area = combat.Areas.VisibleArea?.DisplayName.Text ?? "no area";
        if (onShow != _devArea!.Area || DevCardsOf(menu, onShow).Bound == 0)
        {
            _ctx.Log($"Dev: cards {n}: not pictured: '{_devArea.DisplayName.Text}' is not on show with its cards; {DevOnScreen(menu, combat)}.");
            return;
        }
        // The tags really on the cards, read from the cards; and the cards the history has a result for, by the
        // rule Update goes by (a combat area's unlocked enemy with a usable result).
        _readHistory();
        CombatArea? fights = onShow as CombatArea;
        string? code = fights != null && _history.Scans.Count > 0 ? CardSetup.Code(CardSetup.Describe(game, combat)) : null;
        var tags = new List<string>();
        var untagged = new List<string>();
        int count = 0, due = 0;
        var tree = new StringBuilder();
        var geometry = new List<string>();
        foreach (Node child in EnemyContainer(EnemySelect(menu)).GetChildren())
        {
            if (child is not EnemyOptionsPanel card || !card.IsVisibleInTree()) continue;
            count++;
            string enemyName = EnemyOf(card)?.DisplayName.Text ?? "?";
            Control? tag = card.FindChild(LineName, true, false) as Control;
            bool shown = tag != null && tag.IsVisibleInTree();
            if (shown) tags.Add($"'{enemyName}': '{DevText(tag!)}'");
            if (fights != null && code != null && AreaOf(card) == fights && EnemyOf(card) is { } enemy && UnlockedOf(card).IsVisibleInTree() &&
                _history.ForCard(fights.Id, enemy.Id, code) != null)
            {
                due++;
                if (!shown) untagged.Add(enemyName);
            }
            Rect2 frame = card.GetGlobalRect();
            string where = "no tag";
            if (shown)
            {
                Rect2 at = tag!.GetGlobalRect();
                var hits = new List<string>();
                foreach (var (part, control) in new (string, Control)[] { ("the name", NameOf(card)), ("Fight", FightOf(card)), ("View stats", ViewStatsOf(card)) })
                    if (control.IsVisibleInTree() && control.GetGlobalRect().Intersects(at)) hits.Add(part);
                if (!frame.Encloses(at)) hits.Add("the card's edge");
                where = $"tag at {Rect(at)}, {at.Position.Y - frame.Position.Y:0} px below the card's top, {frame.End.Y - at.End.Y:0} px above its bottom; " +
                        (hits.Count == 0 ? "overlaps nothing" : "OVERLAPS " + string.Join(", ", hits));
            }
            geometry.Add($"Dev: cards {n}, card {count} '{enemyName}': card {Rect(frame)}; name {Rect(NameOf(card))}; " +
                         $"Fight {Rect(FightOf(card))}; View stats {Rect(ViewStatsOf(card))}; {where}");
            if (tree.Length == 0 || (shown && !tree.ToString().Contains(LineName)))
            {
                tree.Clear();
                DevTree(card, 0, tree);
            }
        }
        _ctx.Log($"Dev: cards {n}: {area}: {count} cards on show, {tags.Count} tags (the history has a result for {due} of these cards); " +
                 DevOnScreen(menu, combat) + ".");
        for (int i = 0; i < tags.Count; i++) _ctx.Log($"Dev: cards {n}, tag {i + 1} on {tags[i]}");
        if (due > 0 && tags.Count == 0)
            _ctx.Log($"Dev: cards {n}: NO TAGS on the cards, although the history has a result for {due} of them ({string.Join(", ", untagged)}).");
        else if (untagged.Count > 0)
            _ctx.Log($"Dev: cards {n}: {untagged.Count} of the {due} cards with a result have no tag: {string.Join(", ", untagged)}.");
        else if (due == 0)
            _ctx.Log($"Dev: cards {n}: no tag is due here: " + (fights == null ? "a dungeon's enemies get none."
                : code == null ? "there are no scans." : "no scan has a usable result for these enemies."));
        foreach (string line in geometry) _ctx.Log(line);
        File.WriteAllText(Path.Combine(_ctx.Directory, $"tree-dump-cards-{n}.txt"), tree.ToString());
        _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-cards-{n}.png"));
        _ctx.Log($"Dev: saved shot-cards-{n}.png and tree-dump-cards-{n}.txt; the pointer is on {DevName(_ctx.Tree.Root.GuiGetHoveredControl())}; " +
                 $"tooltips on show: {GameStyle.DevTooltips(_ctx.Tree.Root)}");
    }

    static string DevText(Node node) => string.Join(" ", node.FindChildren("*", "Label", true, false).OfType<Label>()
        .Where(label => label.IsVisibleInTree() && label.Text != "").Select(label => label.Text));

    // Where the line goes, the first few times, and once the whole card before any line was added to it.
    void DevLayout(EnemyOptionsPanel card, VBoxContainer column)
    {
        if (_devLayouts++ < 3)
        {
            var path = new List<string>();
            for (Node? node = column; node != null && node != card; node = node.GetParent())
                path.Insert(0, $"{node.Name} ({node.GetType().Name}{(node is Control c ? $" {c.Size.X:0}x{c.Size.Y:0}" : "")})");
            MarginContainer unlocked = UnlockedOf(card);
            string relation = column.IsAncestorOf(unlocked) ? "holds" : unlocked.IsAncestorOf(column) ? "is inside" : "is beside";
            _ctx.Log($"Dev: card line goes into {card.Name}/{string.Join("/", path)}, after its {column.GetChildCount()} rows; " +
                     $"that column holds the Fight button: {column.IsAncestorOf(FightOf(card))}, {relation} UnlockedContainer; " +
                     $"card {card.Size.X:0}x{card.Size.Y:0}");
        }
        if (_devBeforeDumped) return;
        _devBeforeDumped = true;
        var tree = new StringBuilder();
        DevTree(card, 0, tree);
        File.WriteAllText(Path.Combine(_ctx.Directory, "tree-dump-card-before.txt"), tree.ToString());
        _ctx.Log("Dev: wrote tree-dump-card-before.txt");
    }

    static void DevTree(Node node, int depth, StringBuilder sb)
    {
        sb.Append(' ', depth * 2).Append(node.Name).Append(" (").Append(node.GetType().Name).Append(')');
        if (node is Control c)
        {
            if (!c.IsVisibleInTree()) sb.Append(" hidden");
            sb.Append($" at {Rect(c.GetGlobalRect())}");
            if (c.CustomMinimumSize != Vector2.Zero) sb.Append($" min {c.CustomMinimumSize.X:0}x{c.CustomMinimumSize.Y:0}");
            sb.Append($" flags h={c.SizeFlagsHorizontal} v={c.SizeFlagsVertical}");
            if (c is BoxContainer box) sb.Append($" separation {box.GetThemeConstant("separation")}");
            if (c is Label l) sb.Append($" '{l.Text.Replace("\n", "\\n")}' size {l.GetThemeFontSize("font_size")} #{l.GetThemeColor("font_color").ToHtml()}");
            if (c is Button b) sb.Append($" '{b.Text}'");
        }
        sb.AppendLine();
        if (depth < 14)
            foreach (Node child in node.GetChildren()) DevTree(child, depth + 1, sb);
    }

    static string Rect(Control control) => Rect(control.GetGlobalRect());
    static string Rect(Rect2 r) => $"{r.Position.X:0},{r.Position.Y:0} {r.Size.X:0}x{r.Size.Y:0}";

    // Both setups, part by part, when the card's result was made with another one.
    void DevSetups(SimResult r, string now)
    {
        if (r.SetupText == null) { _ctx.Log($"Dev: setup now {CardSetup.Code(now)} = {now}; the scan's {r.Setup} was read from disk, where only its code is kept."); return; }
        var mine = now.Split("; ").ToHashSet();
        var theirs = r.SetupText.Split("; ").ToHashSet();
        _ctx.Log($"Dev: setup now {CardSetup.Code(now)}, the scan's {r.Setup}. Only now: {string.Join("; ", mine.Except(theirs))}. " +
                 $"Only in the scan: {string.Join("; ", theirs.Except(mine))}.");
    }
#endif
}
