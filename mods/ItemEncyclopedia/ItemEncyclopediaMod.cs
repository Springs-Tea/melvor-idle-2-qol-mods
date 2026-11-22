using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GamesByMalcs.Melvor2;
using GamesByMalcs.Melvor2.Data.Items;
using Godot;
using MelvorModLoader;
using MelvorMods.Shared;

namespace ItemEncyclopedia;

/// <summary>
/// An encyclopedia of every item in the game: search and filter them by kind, slot, combat style, weapon,
/// skill and where they come from, and read everything about one of them, down to which enemy drops it and
/// what it is made from. It only shows what the game's data says; it never gives, buys or equips anything.
/// A round button on the right edge of the screen and F9 open it.
/// </summary>
public class ItemEncyclopediaMod : IMod
{
    sealed class Settings
    {
        public float? X { get; set; }
        public float? Y { get; set; }
        /// <summary>The size the player dragged the window to; until then it is as large as fits the screen.</summary>
        public float? Scale { get; set; }
        /// <summary>The window is folded down to its title.</summary>
        public bool Minimised { get; set; }
        public bool FiltersOpen { get; set; }
        public List<string>? Folded { get; set; }
        public Query Query { get; set; } = new();
        /// <summary>The last few different searches and filter choices, newest first.</summary>
        public List<Query>? Recent { get; set; }
        /// <summary>Id of the item that was last looked at.</summary>
        public string Selected { get; set; } = "";
    }

    const Key OpenKey = Key.F9;

    ModContext _ctx = null!;
    Settings _settings = new();
    readonly ViewState _state = new();
    Game? _game;
    Catalog? _catalog;
    GameWindow? _window;
    EncyclopediaView? _view;
    Button? _stripButton;
    // Read beside the game, handed over here.
    volatile ItemIndex? _readIndex;
    Dictionary<object, object>? _readParents;
    volatile bool _readFailed;
    bool _stopped, _keyWasDown, _unsaved, _stripMissingLogged;
    readonly Tempo _look = new(500), _owned = new(1500), _save = new(2000);
    GameStats _stats = null!;
    // The game's item stats window is open, and the encyclopedia is put away until it closes; and the time that
    // window is open now has been dealt with (put away, or brought back in front of it by the player).
    bool _aside, _statsSeen;

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
        _stats = new GameStats(ctx.Log);
        _stats.Opened += () => GameStatsSaid(open: true);
        _stats.Closed += () => GameStatsSaid(open: false);
        _settings = ModFiles.ReadJson<Settings>(SettingsFile, ctx.Log);
        _settings.Query ??= new Query();
        _state.Query = _settings.Query;
        _state.Recent = _settings.Recent ??= new List<Query>();
        _state.Recent.RemoveAll(r => r == null);
        _state.FiltersOpen = _settings.FiltersOpen;
        if (_settings.Folded != null) _state.Folded = _settings.Folded.ToHashSet();
        ctx.Frame += OnFrame;
        ctx.Log("Ready. F9, or the round button on the right edge of the screen, opens the encyclopedia.");
    }

    void OnFrame()
    {
        if (_stopped) return;
        try
        {
            if (_look.Due())
            {
                TrackCharacter();
                if (_game != null) UpdateStripButton();
                if (_game != null && (IsOpen || _aside)) CheckStats();
            }
            if (_game == null) return;

            // The key is looked at on every frame, so a short press is not missed; everything else goes by the clock.
            bool down = Input.IsKeyPressed(OpenKey);
            // The key opens a window that is folded down to its title before it closes one. Put away for the game's
            // item stats window, it is not open: the key closes that window, and this one comes back (OpenNow).
            if (down && !_keyWasDown) { if (!IsOpen) Open(); else if (_window!.Minimised) _window.SetMinimised(false); else Close(); }
            _keyWasDown = down;

            TakeIndex();
            if (_stats.Waiting) FollowStats();
            if (IsOpen && !_window!.Minimised && _owned.Due() && _catalog != null && _catalog.RefreshOwned()) _view?.OwnedChanged();
            if (_unsaved && _save.Due()) WriteSettings();
#if DEVTOOLS
            // A request that goes wrong is the helper's fault, not the mod's: it is logged and dropped, and the
            // mod carries on.
            try { DevRequests(); }
            catch (Exception e)
            {
                _ctx.Log("Dev: the request failed and the rest of it was dropped: " + e);
                _devQueue.Clear();
                try { DevPutBack(); } catch (Exception again) { _ctx.Log("Dev: the window could not be put back as it was: " + again.Message); }
                try { File.WriteAllText(Path.Combine(_ctx.Directory, "enc.done"), "failed"); } catch { }
            }
#endif
        }
        catch (Exception e) { Stop(e); }
    }

    // One line, and the mod is out of the way: the window and the button go, the game is left as it is.
    void Stop(Exception e)
    {
        if (_stopped) return;
        _stopped = true;
        _ctx.Log("Stopped (the game was updated?): " + e);
        try { _stats.Forget(); _view?.Release(); _window?.Free(); } catch { }
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) _stripButton.QueueFree();
    }

    // On the screen. Put away for the game's item stats window it is not: F9 and the round button close that window,
    // and it comes back.
    bool IsOpen => _window is { IsAlive: true, Visible: true };

    // CurrentGame is null on the main menu and a new object for every character that is opened.
    void TrackCharacter()
    {
        Game? now = GameAccess.CurrentGame;
        if (now is not { IsSaveLoaded: true }) now = null;
        if (ReferenceEquals(now, _game)) return;
        if (_unsaved) WriteSettings();
        _game = now;
        _view?.Release();
        _window?.Free();
        _window = null;
        _view = null;
        _catalog = null;
        _stripButton = null;
        _readIndex = null;
        _readFailed = false;
        _aside = _statsSeen = false;
        _stats.Forget();
        _state.Selected = null;
        _state.Back.Clear();
        _state.Scroll = 0;
        _state.IndexFailed = false;
    }

    // ---- the window ------------------------------------------------------------------------------------------------

    // Opening and rebuilding are also asked for by a click, outside of OnFrame: a game member that is gone
    // must end in the same one line there.
    void Open()
    {
        try { OpenNow(); }
        catch (Exception e) { Stop(e); }
    }

    void OpenNow()
    {
        if (_game == null || _stopped) return;
        if (_catalog == null)
        {
            // The first time for this character: the items are listed now, and where they come from is read
            // beside the game, because that walks through all of the game's data.
            var clock = Stopwatch.StartNew();
            _catalog = new Catalog(_game);
            _ctx.Log($"{_catalog.Items.Count:N0} items listed in {clock.ElapsedMilliseconds:N0} ms.");
            if (_settings.Selected != "") _state.Selected = _catalog.Items.FirstOrDefault(e => e.Item.Id == _settings.Selected);
            ReadIndex(_game);
        }
        else _catalog.RefreshOwned();
        // The game's item stats window, to be followed while this one is open; and the bank's button for it.
        bool offered = !_stats.Gone;
        _stats.Look();
        if (_window is not { IsAlive: true }) Build();
        else
        {
            _view?.OwnedChanged();
            if (offered && _stats.Gone) _view?.RefillDetails();
        }
        _statsSeen = _stats.IsUp;
        // Asked for while the game's item stats window is open: shown now, this window would lie over that one and
        // its X. So that window is closed the game's way, and this one comes back once it has closed (ComeBack),
        // folded or not as it was. Should the game keep it open, this one stays put away, off its X.
        if (_statsSeen && _stats.Layer <= _window!.Layer.Layer)
        {
            _aside = true;
            _window.Visible = false;
            SyncStripButton();
            bool asked = _stats.CloseWindow();
#if DEVTOOLS
            _ctx.Log($"Dev: the encyclopedia was asked for while the game's item stats window is open: that window was {(asked ? "" : "NOT ")}asked to close; " +
                     $"the encyclopedia is {(_aside ? "put away until it has closed" : "back")} (folded {_window.Minimised})");
#endif
            return;
        }
        _aside = false;
        _window!.Visible = true;
        SyncStripButton();
    }

    void Close()
    {
        // Putting the window away is one of the moments that keep what was asked among the recent ones.
        _view?.NoteAsked();
        _aside = false;
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
        if (_unsaved) WriteSettings();
    }

    // A window built again is shown when the one before was, but not while the game's item stats window is in front.
    void ShowAgain(bool open) => _window!.Visible = open && !_aside;

    // ---- the game's item stats window ---------------------------------------------------------------------------

    // Details' button: the game is asked for its own window, the way its bank asks for it. A click, outside of
    // OnFrame: what goes wrong ends in the same one line.
    void ShowStats(Entry entry)
    {
        if (_stopped) return;
        try { StatsAnswered(_stats.Open(entry.Item), "after the button"); }
        catch (Exception e) { Stop(e); }
    }

    // An ask the game did not answer at once, on the frames after it.
    void FollowStats() => StatsAnswered(_stats.Follow(), "a moment after the button");

    void StatsAnswered(GameStats.Asked answer, string when)
    {
        // Asked for by the player: put away, also when it had been brought back in front of that window before.
        if (answer == GameStats.Asked.Open) { _statsSeen = true; StepAside(when); }
        // It did not open: one line in the log says why, and Details no longer offers the button.
        else if (answer == GameStats.Asked.NotOpened) _view?.RefillDetails();
    }

    // The game says its item stats window opened or closed, whoever opened it: this button, or the bank's own. Called
    // inside the game's own call: what goes wrong here stops this mod, never the game's window.
    void GameStatsSaid(bool open)
    {
        if (_stopped) return;
        try
        {
            // Closing, it may still be on the screen for a moment: it stays dealt with until it is gone.
            if (open) { _statsSeen = true; StepAside("the game said it opened"); }
            else ComeBack("the game said it closed");
        }
        catch (Exception e) { Stop(e); }
    }

    // The net under the game's own word, twice a second while this window is open or put away: a window that closed
    // some other way still brings this one back, and one that opened unheard still puts it away.
    void CheckStats()
    {
        if (!_stats.IsUp)
        {
            _statsSeen = false;
            ComeBack("it was seen closed");
        }
        else if (!_statsSeen)
        {
            _statsSeen = true;
            StepAside("it was seen open");
        }
    }

    // The game draws its item stats window on its own screen, under this window's layer: while it is open, this
    // window is put away, folded or not, and nothing in it changes.
    void StepAside(string when)
    {
        if (_window is not { IsAlive: true, Visible: true }) return;
        int theirs = _stats.Layer, mine = _window.Layer.Layer;
#if DEVTOOLS
        _ctx.Log($"Dev: the game's item stats window is open ({when}), on canvas layer {theirs}; the encyclopedia is on {mine}: {(theirs <= mine ? "put away until it closes" : "it is drawn above, the encyclopedia stays")}");
#endif
        if (theirs > mine) return;
        // The keyboard goes with it: a search box that kept it would take the Esc that closes the game's window.
        if (_window.Root.GetViewport().GuiGetFocusOwner() is { } focus && _window.Layer.IsAncestorOf(focus)) focus.ReleaseFocus();
        _aside = true;
        _window.Visible = false;
        SyncStripButton();
    }

    void ComeBack(string when)
    {
        if (!_aside) return;
        _aside = false;
        if (_window is { IsAlive: true }) _window.Visible = true;
        SyncStripButton();
#if DEVTOOLS
        _ctx.Log($"Dev: the game's item stats window closed ({when}); the encyclopedia is back (folded {_window?.Minimised})");
#endif
    }

    void Build()
    {
        _view?.Release();
        _window?.Free();
        Vector2? at = _settings is { X: { } x, Y: { } y } ? new Vector2(x, y) : null;
        _window = new GameWindow(_ctx.Tree, "Item Encyclopedia", ShownScale(), at, canMinimise: true, minimised: _settings.Minimised);
        _window.CloseRequested += Close;
        _window.MinimisedChanged += minimised =>
        {
            _settings.Minimised = minimised;
            _unsaved = true;
            if (minimised) _view?.NoteAsked();
            // Opened up again: what the character has may have changed while only the title was showing.
            if (!minimised && _catalog != null && _catalog.RefreshOwned()) _view?.OwnedChanged();
        };
        _window.Moved += position => { _settings.X = position.X; _settings.Y = position.Y; WriteSettings(); };
        _window.ResizeRequested += scale =>
        {
            // The same window again at another size.
            _settings.Scale = Math.Min(scale, LargestScale());
            _settings.X = _window!.Position.X;
            _settings.Y = _window.Position.Y;
            WriteSettings();
            Rebuild();
        };
        _view = new EncyclopediaView(_window, _catalog!, _state, Remember, Rebuild, _ctx.Log, _stats, ShowStats);
    }

    // The window is 1312 by 743 at full size. It is never larger than the screen, whatever size was chosen on
    // another screen or dragged to: the largest size that fits, in the same steps of 5% the corner resizes in.
    float LargestScale()
    {
        Vector2 screen = _ctx.Tree.Root.GetVisibleRect().Size;
        float fits = Math.Min(screen.X / 1316f, screen.Y / 746f);
        return Math.Clamp(MathF.Floor(fits * 20f) / 20f, GameWindow.MinScale, GameWindow.MaxScale);
    }

    // Until the player has chosen a size: full size, or less on a small screen.
    float ShownScale() => Math.Min(_settings.Scale ?? 1f, LargestScale());

    void Rebuild()
    {
        try
        {
            if (_stopped) return;
            Build();
            ShowAgain(true);
        }
        catch (Exception e) { Stop(e); }
    }

    void ReadIndex(Game game)
    {
        Task.Run(() =>
        {
            try
            {
                var clock = Stopwatch.StartNew();
                ItemIndex index = ItemIndex.Build(game);
                Dictionary<object, object> parents = Catalog.FindParents(game, index);
                if (!ReferenceEquals(game, _game)) return;
                // Written before the index: the index is what the game's thread waits for.
                _readParents = parents;
                _ctx.Log($"Read {index.Owners:N0} game objects in {clock.ElapsedMilliseconds:N0} ms: sources or uses known for {index.Count:N0} items.");
                _readIndex = index;
            }
            catch (Exception e)
            {
                _ctx.Log("Could not read where the items come from (the game was updated?): " + e);
                if (ReferenceEquals(game, _game)) _readFailed = true;
            }
        });
    }

    // On the game's thread: what was read is put into the catalogue, and the window is built again with it.
    void TakeIndex()
    {
        if (_catalog == null) return;
        if (_readFailed && !_state.IndexFailed)
        {
            _state.IndexFailed = true;
            if (_window is { IsAlive: true }) { bool open = IsOpen; Build(); ShowAgain(open); }
        }
        if (_readIndex is not { } index) return;
        _readIndex = null;
        _catalog.Attach(index, _readParents ?? new Dictionary<object, object>(ReferenceEqualityComparer.Instance));
        _readParents = null;
        if (_window is { IsAlive: true }) { bool open = IsOpen; Build(); ShowAgain(open); }
    }

    // ---- the button in the game's strip of round buttons on the right edge ------------------------------------

    void UpdateStripButton()
    {
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) { SyncStripButton(); return; }
        _stripButton = null;
        if (GameAccess.FindGameView()?.FindChild("FavouritesContainer", true, false) is not BoxContainer strip)
        {
            if (!_stripMissingLogged) _ctx.Log("The game's widget strip was not found (the game was updated?). F9 still works.");
            _stripMissingLogged = true;
            return;
        }
        // The strip may be the one of the character before: its button goes, this one takes its place.
        if (strip.GetNodeOrNull("ItemEncyclopediaButton") is { } stale) { stale.Name = "ItemEncyclopediaButtonGone"; stale.QueueFree(); }
        _stripButton = new Button
        {
            Name = "ItemEncyclopediaButton",
            ThemeTypeVariation = "SidebarCategoryButton",
            CustomMinimumSize = new Vector2(36, 36),
            Icon = GameStyle.Icon("codex") ?? GameStyle.Icon("GuideBook") ?? GameStyle.Icon("Bank"),
            ExpandIcon = true,
            IconAlignment = HorizontalAlignment.Center,
            ToggleMode = true,
            TooltipText = "Item Encyclopedia: every item, what it does and where it comes from (F9)",
        };
        _stripButton.Toggled += open =>
        {
            if (open && !IsOpen) Open();
            else if (!open && IsOpen) Close();
        };
        strip.AddChild(_stripButton);
        // The style's own "pressed" look is a wider circle that is cut off at this size: keep the resting shape.
        if (_stripButton.GetThemeStylebox("normal") is StyleBoxFlat resting)
        {
            var open = (StyleBoxFlat)resting.Duplicate();
            open.BgColor = new Color("275124");
            _stripButton.AddThemeStyleboxOverride("pressed", open);
            _stripButton.AddThemeStyleboxOverride("hover_pressed", open);
        }
        SyncStripButton();
    }

    void SyncStripButton()
    {
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) _stripButton.SetPressedNoSignal(IsOpen);
    }

    // ---- settings.json in the mod's folder ------------------------------------------------------------------------

    string SettingsFile => Path.Combine(_ctx.Directory, "settings.json");

    // Called for every keystroke in the search box: the file is written a little later, not every time.
    void Remember()
    {
        _settings.FiltersOpen = _state.FiltersOpen;
        _settings.Folded = _state.Folded.OrderBy(f => f, StringComparer.Ordinal).ToList();
        _settings.Selected = _state.Selected?.Item.Id ?? "";
        _unsaved = true;
    }

    void WriteSettings()
    {
        _unsaved = false;
        ModFiles.WriteJson(SettingsFile, _settings, _ctx.Log);
    }

#if DEVTOOLS
    // Development: a file "enc.request" drives the window with nobody at the keyboard, one command per line:
    //   open | close | reset | filters=open | filters=closed | fold=types,slots (the groups to fold away)
    //   scale=0.8 | scale=min | scale=max   (the window's size; put back as it was when the request ends)
    //   window=unfolded | window=folded   (the window opened up or folded down; put back as it was when the request ends)
    //   search=bow | sort=3 | kind=Weapon,Armour | slot=Torso | style=Ranged | weapon=Bow
    //   skill=Cooking | mode=1 | from=Enemy drop | area=part of an area's name | show=owned,found,missing,nospoilers
    //   select=part of an item's name | press=link | press=more | press=back | press=part of a linked item's name
    //   scroll=5 | details=400 | wait | shot=name   (saves shot-name.png, the whole screen, and crop-name.png, the window)
    //   dump   (items-dump.txt and icons-dump.txt, see Dev.cs)
    //   bank | shop   (the game's own screens, to compare with) | screen=name   (shot-name.png, the whole screen)
    //   pick=longest | pick=sources | pick=uses | pick=nosource | pick=plain   (the items that are hardest to show)
    //   pick=slot:Weapon | pick=slot:Ring,unowned | pick=slot:Helmet,owned   (the first item of a slot, in the game's order)
    //   pick=notworn | pick=outfit   (the first item whose details are shown and that cannot be worn: one with no slot,
    //                                 and a skilling outfit, which has a slot but goes on a tool belt)
    //   detailsbutton=name   (the details' View Item Stats scrolled into sight and pictured: crop-button-name.png, with
    //                 its place in its row)
    //   stats=name | stats=name,tab | stats=name,outside | stats=name,keep
    //                 (a click on that button in Details; the game's item stats window, read back and pictured as
    //                 shot-stats-name.png and crop-stats-name.png, then closed with its own X; the log says whether the
    //                 encyclopedia came back. With no button, one line says so.
    //                 tab: TAB is pressed in the game's window until it compares with the first loadout again, and the
    //                 worn items are read before and after. outside: closed with a click beside it, not the X, and the
    //                 screen pictured after it: shot-outside-name.png. keep: left open for the next line, key or
    //                 statsclose.) Both unfold a folded window first; the fold is put back when the request ends.
    //   statsclose=name   (the game's item stats window closed with its own X, however it was opened)
    // The bank (after "bank"), to compare with and to open the game's window the bank's way:
    //   banksel=part of a name | banksel=wearable   (a click on that item in the bank's tab on screen: it is selected,
    //                 and the encyclopedia selects it too) | bankbutton=name   (the bank's View Item Stats pictured:
    //                 crop-bankbutton-name.png, with its look beside the one read from the bank's scene)
    //   bankstats=name   (a click on the bank's own View Item Stats; the log says whether the encyclopedia stepped aside)
    // And through the game's own input, as a player's hand would (the pointer is not moved, the events are made):
    //   key | key=name   (F9 down and up; a moment later the log says whether the game's item stats window closed and
    //                 whether the encyclopedia lies over its X; with a name, shot-key-name.png)
    //   button   (the round button in the strip) | minimise   (the button beside the X)
    //   click=row5 | click=search | click=Filters
    //   wheel=row5 | type=bow | drag=window,-200,60 | drag=corner,-150,-90
    //   hover=chance1   (rests on the first chance of the details that says more when hovered; the log has the tooltip's words)
    // Every click on a button is made in two steps: the pointer first, the press only when the game says the pointer
    // is on that button (DevAim, DevPressAimed). When the last line is done, the window's size and fold are put back
    // as they were when the request came, and "enc.done" is written.
    Queue<string> _devQueue = new();
    readonly Tempo _devLook = new(300);
    ulong _devNext;
    // The window as the request found it, put back when it ends.
    float? _devScaleBefore;
    bool _devFoldedBefore;

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "enc.request");
        if (_devQueue.Count == 0)
        {
            if (!_devLook.Due() || !File.Exists(request)) return;
            foreach (string line in File.ReadAllLines(request)) if (line.Trim() != "") _devQueue.Enqueue(line.Trim());
            File.Delete(request);
            _devNext = Time.GetTicksMsec() + 200;
            _devScaleBefore = _settings.Scale;
            _devFoldedBefore = _settings.Minimised;
            return;
        }
        if (Time.GetTicksMsec() < _devNext) return;
        string command = _devQueue.Dequeue();
        int cut = command.IndexOf('=');
        string verb = cut < 0 ? command : command[..cut], value = cut < 0 ? "" : command[(cut + 1)..];
        // A window of the game opens and closes with a short animation: what follows a click on one waits for it.
        _devNext = Time.GetTicksMsec() + (ulong)(verb is "shot" or "wait" or "_press" or "_tabup" or "_keyup" ? 900 : 250);
        Query q = _state.Query;
        void Pick(HashSet<string> into, List<Choice>? from)
        {
            into.Clear();
            foreach (string name in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (from?.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } choice) into.Add(choice.Id);
                else _ctx.Log($"Dev: no choice '{name}' among: {string.Join(", ", from?.Select(c => c.Name) ?? [])}");
        }
        bool rebuild = true;
        switch (verb)
        {
            case "open": Open(); rebuild = false; break;
            case "close": Close(); rebuild = false; break;
            case "reset": q.Reset(); break;
            case "filters": _state.FiltersOpen = value == "open"; break;
            case "fold": _state.Folded = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(); break;
            case "scale":
                _settings.Scale = value switch
                {
                    "min" => GameWindow.MinScale,
                    "max" => LargestScale(),
                    _ => float.Parse(value, System.Globalization.CultureInfo.InvariantCulture),
                };
                break;
            case "window":
                rebuild = false;
                if (_window is { IsAlive: true }) _window.SetMinimised(value == "folded");
                _ctx.Log($"Dev: window={value} -> folded {_window?.Minimised}, on screen {IsOpen}, put away {_aside}");
                break;
            case "search": q.Search = value; break;
            case "sort": q.Sort = int.Parse(value); break;
            case "kind": Pick(q.Types, _catalog?.Types); break;
            case "slot": Pick(q.Slots, _catalog?.Slots); break;
            case "style": Pick(q.Styles, _catalog?.Styles); break;
            case "weapon": Pick(q.Weapons, _catalog?.Weapons); break;
            case "skill": Pick(q.Skills, _catalog?.Skills); break;
            case "mode": q.SkillMode = int.Parse(value); break;
            case "from": Pick(q.Sources, _catalog?.Sources); break;
            case "area":
                q.Area = value == "" ? "" : _catalog?.Areas.SelectMany(a => a.Areas).FirstOrDefault(a => a.Name.Contains(value, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
                if (value != "" && q.Area == "") _ctx.Log("Dev: areas = " + string.Join(" / ", _catalog?.Areas.Select(a => a.Category + ": " + string.Join(", ", a.Areas.Select(x => x.Name))) ?? []));
                break;
            case "show":
                q.Owned = value.Contains("owned"); q.Found = value.Contains("found"); q.Missing = value.Contains("missing"); q.SpoilerFree = value.Contains("nospoilers");
                break;
            case "select":
                Entry? pick = _catalog?.Items.FirstOrDefault(e => e.Name.Equals(value, StringComparison.OrdinalIgnoreCase))
                              ?? _catalog?.Items.FirstOrDefault(e => e.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
                if (pick != null) _view?.Select(pick, false);
                _ctx.Log($"Dev: select '{value}' -> {pick?.Item.Id ?? "nothing"}" + (pick == null ? "" : $": kinds {string.Join(",", pick.Types)}; slot {pick.Slot?.DisplayName.Text}; weapon {pick.Weapon?.DisplayName.Text}; " +
                    $"styles {string.Join(",", pick.Styles)}; made by {string.Join(",", pick.MadeBy)}; used by {string.Join(",", pick.UsedBy)}; tool for {string.Join(",", pick.ToolFor)}; level {pick.Level}; " +
                    $"from {string.Join(",", pick.Sources)}; places {string.Join(",", pick.Places.Select(p => p.DisplayName.Text))}; bank {pick.InBank}, worn {pick.Worn}, found {pick.Found}"));
                rebuild = false;
                break;
            case "press": _view?.DevPress(value); rebuild = false; break;
            case "scroll": _view?.DevScroll(int.Parse(value)); rebuild = false; break;
            case "details": _view?.DevScrollDetails(int.Parse(value)); rebuild = false; break;
            case "wait": rebuild = false; break;
            // The game's own screens, to compare the window with: its bank and its shop.
            case "bank": rebuild = false; GameAccess.FindGameView()?.OpenBank(); break;
            case "shop": rebuild = false; GameAccess.FindGameView()?.OpenShop(); break;
            case "screen":
                rebuild = false;
                _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-{value}.png"));
                _ctx.Log($"Dev: shot-{value}.png (the whole screen)");
                break;
            // The item that is hardest to fit: the longest name, or the most places it comes from or goes to.
            case "pick":
                rebuild = false;
                Entry? odd = value switch
                {
                    "longest" => _catalog?.Items.MaxBy(e => e.Name.Length),
                    "sources" => _catalog?.Items.MaxBy(e => _catalog.Index?.For(e.Item)?.Sources.Count ?? 0),
                    "uses" => _catalog?.Items.MaxBy(e => _catalog.Index?.For(e.Item)?.Uses.Count ?? 0),
                    "nosource" => _catalog?.Items.FirstOrDefault(e => e.Sources.Contains("none")),
                    "plain" => _catalog?.Items.FirstOrDefault(e => e.Types.Contains("material") && e.Item.BankTags.Count == 0),
                    // Only items whose details are shown: with "No spoilers" on, a hidden item has no button anyway.
                    "notworn" => _catalog?.Items.FirstOrDefault(e => !e.Wearable && e.Slot == null && DevShown(e)),
                    "outfit" => _catalog?.Items.FirstOrDefault(e => !e.Wearable && e.Types.Contains("outfit") && DevShown(e)),
                    _ when value.StartsWith("slot:", StringComparison.Ordinal) => DevPickSlot(value[5..]),
                    _ => null,
                };
                if (odd != null) _view?.Select(odd, false);
                _ctx.Log(odd == null ? $"Dev: pick {value} -> nothing"
                    : $"Dev: pick {value} -> {odd.Name} ({_catalog?.Index?.For(odd.Item)?.Sources.Count ?? 0} sources, {_catalog?.Index?.For(odd.Item)?.Uses.Count ?? 0} uses; " +
                      $"kinds {string.Join(",", odd.Types)}, slot {odd.Slot?.DisplayName.Text ?? "-"}, can be worn {odd.Wearable}, owned {odd.Owned}); the details show {_view?.DevDetailsShown}");
                break;
            // The details' button, scrolled into sight, and its picture once it is drawn there.
            case "detailsbutton":
                rebuild = false;
                if (!DevDetailsOnScreen($"detailsbutton {value}")) break;
                if (_view?.DevStatsButton() == null) _ctx.Log($"Dev: detailsbutton {value}: no '{BankButton.Word}' button; the details show {_view?.DevDetailsShown}");
                // Scrolled into sight once the details are laid out, and pictured once that is drawn.
                else _devQueue = new Queue<string>(new[] { "_detailsseek", "_detailsbutton=" + value }.Concat(_devQueue));
                break;
            case "_detailsseek": rebuild = false; _view?.DevStatsButton(); break;
            case "_detailsbutton": rebuild = false; DevDetailsButton(value); break;
            // The details' button for the game's item stats window, pressed as a player presses it; then the
            // window is read back, pictured, and closed with its own X or a click beside it (see DevStatsUp).
            case "stats": rebuild = false; DevStats(value); break;
            case "statsclose":
                rebuild = false;
                _devQueue = new Queue<string>(new[] { "_closeaim=" + value, "_press", "_statsback=" + value }.Concat(_devQueue));
                break;
            case "_statsaim":
                rebuild = false;
                // Only into this window while it shows and the game's window is not up: anywhere else a click could
                // press one of the game's buttons.
                if (_window is { IsAlive: true, Visible: true, Minimised: false } && !_stats.IsUp && _view?.DevStatsButton() is { } statsButton)
                    DevAim(statsButton, $"the '{statsButton.Text}' button in Details");
                else DevDrop($"stats {value}: the encyclopedia is not showing the details, or the game's item stats window is open already: nothing was pressed");
                break;
            case "_press":
                rebuild = false;
                if (!DevPressAimed()) DevDrop("the rest of this step is left out");
                break;
            case "_statsup":
                rebuild = false;
                if (!DevStatsUp(value)) DevDrop($"stats {value}: the rest of this step is left out");
                break;
            case "_tab": rebuild = false; DevTab(value); break;
            case "_tabup":
                rebuild = false;
                Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = false });
                break;
            case "_closeaim": rebuild = false; DevAimClose(value); break;
            case "_outside": rebuild = false; DevAimOutside(value); break;
            case "_statsback":
                rebuild = false;
                // "name,outside": closed with a click beside it; the whole screen is pictured, to show whether it did close.
                string[] back = value.Split(',');
                string backShot = back.Length > 1 ? $"; shot-{back[1]}-{back[0]}.png" : "";
                if (backShot != "") _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-{back[1]}-{back[0]}.png"));
                _ctx.Log($"Dev: stats {back[0]}: after it was closed the game's item stats window is {(_stats.IsUp ? "STILL OPEN" : "closed")}; the encyclopedia is " +
                         $"{(IsOpen ? "back" : "NOT back")} (put away {_aside}, folded {_window?.Minimised}), selected {_state.Selected?.Name ?? "-"}, scroll {_state.Scroll}{backShot}");
                break;
            // The bank, after "bank": an item of its tab on screen selected as a player selects it, its own button
            // pictured, and pressed.
            case "banksel": rebuild = false; DevBankSelect(value); break;
            case "_banksel": rebuild = false; DevBankSelected(value); break;
            case "bankbutton": rebuild = false; DevBankButton(value); break;
            case "bankstats":
                rebuild = false;
                if (_stats.DevBankButton() is { } bankStats && bankStats.IsVisibleInTree() && !_stats.IsUp)
                {
                    DevAim(bankStats, "the bank's own View Item Stats");
                    _devQueue = new Queue<string>(new[] { "_press", "_bankstatsup=" + value }.Concat(_devQueue));
                }
                else _ctx.Log($"Dev: bankstats {value}: the bank shows no View Item Stats, or the game's item stats window is open already: nothing was pressed");
                break;
            case "_bankstatsup":
                rebuild = false;
                _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-bankstats-{value}.png"));
                _ctx.Log($"Dev: bankstats {value}: shot-bankstats-{value}.png; the game's item stats window is {(_stats.IsUp ? "open" : "NOT open")} for '{DevStatsTexts().FirstOrDefault()}'; " +
                         $"the encyclopedia is {(IsOpen ? "ON THE SCREEN" : _aside ? "put away" : "closed")} (folded {_window?.Minimised}); the round button shows it {(DevStripPressed ? "open" : "closed")}");
                break;
            case "key":
                rebuild = false;
                Input.ParseInputEvent(new InputEventKey { Keycode = OpenKey, PhysicalKeycode = OpenKey, Pressed = true });
                _devQueue = new Queue<string>(_devQueue.Prepend("_keyup=" + value));
                break;
            case "_keyup":
                rebuild = false;
                Input.ParseInputEvent(new InputEventKey { Keycode = OpenKey, PhysicalKeycode = OpenKey, Pressed = false });
                _ctx.Log($"Dev: F9 pressed -> window open = {IsOpen}, put away {_aside}, folded {_window?.Minimised}; the game's item stats window is {(_stats.IsUp ? "open" : "closed")}");
                // Looked at again once the game's window has had the time to close.
                _devQueue = new Queue<string>(_devQueue.Prepend("_keyafter=" + value));
                break;
            case "_keyafter": rebuild = false; DevAfterKey(value); break;
            case "button":
                rebuild = false;
                if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) DevMouse(_stripButton.GetGlobalRect().GetCenter(), MouseButton.Left, "strip button");
                else _ctx.Log("Dev: no strip button");
                break;
            // The pointer comes to rest there, and nothing is pressed: what a tooltip waits for.
            case "hover":
                rebuild = false;
                // Asked twice: the first time scrolls it into sight, and the place is right once that is laid out.
                _view?.DevWhere(value);
                _devQueue = new Queue<string>(_devQueue.Prepend("_rest=" + value));
                break;
            case "_rest":
                rebuild = false;
                if (_view?.DevWhere(value) is { } rest)
                {
                    Vector2 over = _ctx.Tree.Root.GetFinalTransform() * rest;
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = over, GlobalPosition = over });
                    _devQueue = new Queue<string>(_devQueue.Prepend("_hovered=" + value));
                    _devNext = Time.GetTicksMsec() + 1100;
                }
                else _ctx.Log($"Dev: nothing called '{value}' to hover");
                break;
            case "_hovered":
                rebuild = false;
                Control? under = _ctx.Tree.Root.GuiGetHoveredControl();
                _ctx.Log($"Dev: hover {value} -> the pointer is on {(under == null ? "nothing" : under.GetType().Name + (under is Label label ? " '" + label.Text + "'" : ""))}; tooltip: {GameStyle.DevTooltips(_ctx.Tree.Root)}");
                break;
            case "click" or "wheel":
                rebuild = false;
                if (_view?.DevWhere(value) is { } at) DevMouse(at, verb == "click" ? MouseButton.Left : MouseButton.WheelDown, value);
                else _ctx.Log($"Dev: nothing called '{value}' to {verb}");
                break;
            case "type":
                rebuild = false;
                foreach (char letter in value)
                {
                    Input.ParseInputEvent(new InputEventKey { Unicode = letter, Keycode = (Key)char.ToUpperInvariant(letter), Pressed = true });
                    Input.ParseInputEvent(new InputEventKey { Unicode = letter, Keycode = (Key)char.ToUpperInvariant(letter), Pressed = false });
                }
                _devQueue = new Queue<string>(_devQueue.Prepend("_typed"));
                break;
            case "_typed":
                rebuild = false;
                _ctx.Log($"Dev: typed, the search box says '{_view?.DevSearchText}', the query '{q.Search}' -> {_view?.DevCount} items: {_view?.DevRows(6)}");
                break;
            case "drag":
                rebuild = false;
                if (_window is not { IsAlive: true }) break;
                string[] how = value.Split(',');
                Rect2 box = _window.Root.GetGlobalRect();
                // The window is taken by its frame beside the title; its size by the lines in its bottom right corner.
                Vector2 from = how[0] == "corner" ? box.End - new Vector2(16, 20) * _window.Style.Scale : box.Position + new Vector2(60, 24) * _window.Style.Scale;
                Vector2 by = new(float.Parse(how[1], System.Globalization.CultureInfo.InvariantCulture), float.Parse(how[2], System.Globalization.CultureInfo.InvariantCulture));
                FormattableString steps = $"_down={from.X},{from.Y}|_move={from.X + by.X / 2},{from.Y + by.Y / 2},{by.X / 2},{by.Y / 2}|_move={from.X + by.X},{from.Y + by.Y},{by.X / 2},{by.Y / 2}|_up={from.X + by.X},{from.Y + by.Y}";
                _devQueue = new Queue<string>(FormattableString.Invariant(steps).Split('|').Concat(_devQueue));
                _ctx.Log($"Dev: dragging the {how[0]} from {from} by {by}; window at {box.Position}, scale {_window.Style.Scale}");
                break;
            case "_down" or "_up" or "_move":
                rebuild = false;
                float[] n = value.Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                Transform2D toScreen = _ctx.Tree.Root.GetFinalTransform();
                Vector2 spot = toScreen * new Vector2(n[0], n[1]);
                if (verb == "_move") Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot, Relative = toScreen.BasisXform(new Vector2(n[2], n[3])), ButtonMask = MouseButtonMask.Left });
                else Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = verb == "_down", ButtonMask = verb == "_down" ? MouseButtonMask.Left : 0 });
                if (verb == "_up") _devQueue = new Queue<string>(_devQueue.Prepend("_dragged"));
                break;
            case "_dragged":
                rebuild = false;
                _ctx.Log($"Dev: after the drag the window is at {(_window is { IsAlive: true } ? _window.Root.GetGlobalRect().Position : Vector2.Zero)}, scale {_window?.Style.Scale}; settings say {_settings.X},{_settings.Y} scale {_settings.Scale}");
                break;
            case "dump":
                if (_game != null && _catalog != null) Dev.DumpItems(_game, _catalog.Index, _catalog.Lines, _ctx.Directory, _ctx.Log);
                Dev.DumpIcons(_ctx.Directory, _ctx.Log);
                rebuild = false;
                break;
            case "shot":
                rebuild = false;
                Image screen = _ctx.Tree.Root.GetTexture().GetImage();
                screen.SavePng(Path.Combine(_ctx.Directory, $"shot-{value}.png"));
                if (_window is { IsAlive: true })
                {
                    // The picture is in the screen's pixels, the window in the game's own, which the game stretches.
                    Transform2D stretch = _ctx.Tree.Root.GetFinalTransform();
                    Rect2 frame = stretch * _window.Root.GetGlobalRect().Grow(10);
                    var whole = new Rect2I(0, 0, screen.GetWidth(), screen.GetHeight());
                    Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(whole);
                    if (part.Size.X > 0 && part.Size.Y > 0) screen.GetRegion(part).SavePng(Path.Combine(_ctx.Directory, $"crop-{value}.png"));
                    _ctx.Log($"Dev: shot-{value}.png, window {(int)frame.Position.X},{(int)frame.Position.Y} {(int)frame.Size.X}x{(int)frame.Size.Y} on {screen.GetWidth()}x{screen.GetHeight()}; {_view?.DevCount} items: {_view?.DevRows(8)}");
                }
                else _ctx.Log($"Dev: shot-{value}.png, no window");
                break;
            case "minimise":
                rebuild = false;
                if (_window is { IsAlive: true } && _window.Root.FindChildren("*", "Button", true, false).OfType<Button>().FirstOrDefault(b => b.TooltipText is "Fold down to the title" or "Open the window again") is { } fold)
                    DevMouse(fold.GetGlobalRect().GetCenter(), MouseButton.Left, $"minimise button (window was {_window.Root.GetGlobalRect()}, minimised {_window.Minimised})");
                else _ctx.Log("Dev: no minimise button");
                break;
            case "_clicked":
                rebuild = false;
                if (value.StartsWith("minimise", StringComparison.Ordinal)) _ctx.Log($"Dev: now minimised = {_window?.Minimised}, window = {_window?.Root.GetGlobalRect()}, settings minimised = {_settings.Minimised}");
                _ctx.Log($"Dev: recent = {_view?.DevRecent}");
                _ctx.Log($"Dev: {value} -> window open = {IsOpen}, put away {_aside}, folded {_window?.Minimised}, filters open = {_state.FiltersOpen}, selected = {_state.Selected?.Name ?? "-"}, scroll = {_state.Scroll}, {_view?.DevCount} items: {_view?.DevRows(4)}");
                break;
            default: _ctx.Log("Dev: unknown command " + command); rebuild = false; break;
        }
        if (rebuild && _catalog != null)
        {
            _state.Scroll = 0;
            bool open = IsOpen;
            Build();
            ShowAgain(open);
            _ctx.Log($"Dev: {command} -> {_view?.DevCount} items (window scale {_window?.Style.Scale}): {_view?.DevRows(6)}");
        }
        if (_devQueue.Count == 0)
        {
            DevPutBack();
            File.WriteAllText(Path.Combine(_ctx.Directory, "enc.done"), DateTime.Now.ToString("O"));
        }
    }

    // The window's size and fold as the request found them: the player's settings, not the request's.
    void DevPutBack()
    {
        var put = new List<string>();
        if (_settings.Scale != _devScaleBefore)
        {
            _settings.Scale = _devScaleBefore;
            _unsaved = true;
            put.Add($"size {(_devScaleBefore?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "as large as fits")}");
            if (_window is { IsAlive: true }) { bool open = IsOpen; Build(); ShowAgain(open); }
        }
        if (_window is { IsAlive: true } ? _window.Minimised != _devFoldedBefore : _settings.Minimised != _devFoldedBefore)
        {
            if (_window is { IsAlive: true }) _window.SetMinimised(_devFoldedBefore);
            else { _settings.Minimised = _devFoldedBefore; _unsaved = true; }
            put.Add(_devFoldedBefore ? "folded" : "unfolded");
        }
        if (put.Count > 0) _ctx.Log("Dev: the window put back as the request found it: " + string.Join(", ", put));
    }

    // A press and a release of a mouse button where a control is, made through the game's input like a real one.
    void DevMouse(Vector2 at, MouseButton button, string what)
    {
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * at;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = button, Pressed = true, ButtonMask = button == MouseButton.Left ? MouseButtonMask.Left : 0 });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = button, Pressed = false });
        _devQueue = new Queue<string>(_devQueue.Prepend("_clicked=" + what));
    }

    // Where a control is in the game's own pixels, whatever canvas layer it is drawn on, and where that is in the
    // pixels of a picture of the screen, which the game stretches.
    static Rect2 DevCanvasRect(Control control)
    {
        Transform2D place = control.GetGlobalTransformWithCanvas();
        return new Rect2(place.Origin, control.Size * place.Scale);
    }

    Rect2 DevScreenRect(Rect2 canvas) => _ctx.Tree.Root.GetFinalTransform() * canvas;

    // A part of the screen as it is drawn now, saved in the mod's folder.
    void DevCrop(string file, Rect2 canvas)
    {
        Image screen = _ctx.Tree.Root.GetTexture().GetImage();
        Rect2 frame = DevScreenRect(canvas);
        Rect2I part = new Rect2I((int)frame.Position.X, (int)frame.Position.Y, (int)frame.Size.X, (int)frame.Size.Y).Intersection(new Rect2I(0, 0, screen.GetWidth(), screen.GetHeight()));
        if (part.Size.X > 0 && part.Size.Y > 0) screen.GetRegion(part).SavePng(Path.Combine(_ctx.Directory, file));
    }

    // Everything that makes a button look as it does, to set the bank's and the details' side by side.
    static string DevLook(Button b)
    {
        StyleBox? box = b.GetThemeStylebox("normal");
        Font? font = b.GetThemeFont("font");
        return $"'{b.Text}' theme '{b.ThemeTypeVariation}', font {font?.GetType().Name} {font?.ResourcePath} at {b.GetThemeFontSize("font_size")}, colour {b.GetThemeColor("font_color").ToHtml()}, " +
               $"size {b.Size} (least {b.GetCombinedMinimumSize()}, set {b.CustomMinimumSize}), flags {b.SizeFlagsHorizontal}/{b.SizeFlagsVertical}, align {b.Alignment}, cursor {b.MouseDefaultCursorShape}, " +
               $"focus {b.FocusMode}, tooltip '{b.TooltipText}', box {box?.GetType().Name} {(box as StyleBoxTexture)?.Texture?.ResourcePath} margins {box?.ContentMarginLeft},{box?.ContentMarginTop},{box?.ContentMarginRight},{box?.ContentMarginBottom}, " +
               $"drawn at x{b.GetGlobalTransformWithCanvas().Scale.X:0.##}";
    }

    bool DevStripPressed => _stripButton != null && GodotObject.IsInstanceValid(_stripButton) && _stripButton.ButtonPressed;

    string DevName(Item item) => _catalog?.Items.FirstOrDefault(e => ReferenceEquals(e.Item, item))?.Name ?? item.Id;

    // An item whose details are shown, not withheld as ??? by "No spoilers".
    bool DevShown(Entry entry) => _view == null || !_view.DevHidden(entry);

    // A player's click in two steps, as Item Sources makes them (ItemSourcesMod.DevPressAimed): the pointer is put
    // on the control, and the next step presses only when the game says the pointer is on that control, so the
    // click can never land on a button of the game's beside it or under it.
    Control? _devAim;
    string _devAimName = "";
    Vector2 _devAimAt;
    bool _devAimNotAButton;

    // at: a spot in the game's pixels, the control's middle when not given. notAButton: a click meant for a bare
    // part of the control (a window's dimmed screen), never for a button in it.
    void DevAim(Control target, string name, Vector2? at = null, bool notAButton = false)
    {
        _devAim = target;
        _devAimName = name;
        _devAimAt = at ?? DevCanvasRect(target).GetCenter();
        _devAimNotAButton = notAButton;
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devAimAt;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = spot, GlobalPosition = spot });
    }

    bool DevPressAimed()
    {
        Control? target = _devAim;
        _devAim = null;
        Control? over = _ctx.Tree.Root.GuiGetHoveredControl();
        if (target == null || !GodotObject.IsInstanceValid(target) || !target.IsVisibleInTree() || over == null || (over != target && !target.IsAncestorOf(over))
            || (_devAimNotAButton && over is BaseButton))
        {
            _ctx.Log($"Dev: the pointer is on {(over == null ? "nothing" : over.GetType().Name + " " + over.Name)}, not on {_devAimName}: nothing was pressed");
            return false;
        }
        Vector2 spot = _ctx.Tree.Root.GetFinalTransform() * _devAimAt;
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = true, ButtonMask = MouseButtonMask.Left });
        Input.ParseInputEvent(new InputEventMouseButton { Position = spot, GlobalPosition = spot, ButtonIndex = MouseButton.Left, Pressed = false });
        _devQueue = new Queue<string>(_devQueue.Prepend($"_clicked={_devAimName} (on {over.GetType().Name} {over.Name})"));
        return true;
    }

    // One line, and the rest of this step (its own steps, the ones that start with "_") is left out; the next line
    // of the request goes on.
    void DevDrop(string why)
    {
        _ctx.Log($"Dev: {why}");
        while (_devQueue.Count > 0 && _devQueue.Peek().StartsWith('_')) _devQueue.Dequeue();
        _devAim = null;
    }

    // stats=name[,tab][,outside]: the steps of one press of the details' button, up to the window closed again.
    void DevStats(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
        string tag = parts[0];
        if (!DevDetailsOnScreen($"stats {tag}")) return;
        if (_view?.DevStatsButton() == null)
        {
            // An item that cannot be worn has no button: that is what a request for one checks.
            Entry? none = _state.Selected;
            _ctx.Log($"Dev: stats {tag}: no '{BankButton.Word}' button in the details of {none?.Name ?? "nothing"}; the details show {_view?.DevDetailsShown ?? "nothing"} " +
                     $"(kinds {string.Join(",", none?.Types ?? [])}, slot {none?.Slot?.DisplayName.Text ?? "-"}, can be worn {none?.Wearable}, the feature is {(_stats.Gone ? "off" : "on")})");
            return;
        }
        var steps = new List<string> { "_statsaim=" + tag, "_press", "_statsup=" + tag };
        if (parts.Contains("tab")) steps.Add($"_tab={tag}|0");
        // keep: left open, for the next line of the request to close it (key, statsclose).
        if (parts.Contains("keep"))
        {
            _devQueue = new Queue<string>(steps.Concat(_devQueue));
            return;
        }
        steps.Add((parts.Contains("outside") ? "_outside=" : "_closeaim=") + tag);
        steps.Add("_press");
        steps.Add("_statsback=" + tag + (parts.Contains("outside") ? ",outside" : ""));
        _devQueue = new Queue<string>(steps.Concat(_devQueue));
    }

    // A step that pictures or presses something in Details: the window must be on the screen, and it is unfolded
    // first when it is folded (the request puts the fold back as it found it when it ends).
    bool DevDetailsOnScreen(string what)
    {
        if (_window is not { IsAlive: true } || !IsOpen)
        {
            DevDrop($"{what}: the encyclopedia is not on the screen ({(_aside ? "put away for the game's item stats window" : "closed")}): nothing was pictured or pressed");
            return false;
        }
        if (_window.Minimised)
        {
            _window.SetMinimised(false);
            _ctx.Log($"Dev: {what}: the encyclopedia was folded: unfolded first");
        }
        return true;
    }

    // F9, once the game's item stats window has had the time to close: that window closed and the encyclopedia back
    // as it was, and never lying over that window's X while both are on the screen.
    void DevAfterKey(string name)
    {
        string shot = name == "" ? "" : $", shot-key-{name}.png";
        if (name != "") _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-key-{name}.png"));
        bool covers = IsOpen && _stats.IsUp && _stats.DevCloseButton is { } x && x.IsVisibleInTree() && DevCanvasRect(_window!.Root).Intersects(DevCanvasRect(x));
        _ctx.Log($"Dev: after F9{(name == "" ? "" : " (" + name + ")")}{shot}: the game's item stats window is {(_stats.IsUp ? "STILL OPEN" : "closed")}; the encyclopedia is " +
                 $"{(IsOpen ? "on the screen" : _aside ? "put away" : "closed")} (folded {_window?.Minimised}), {(covers ? "and it LIES OVER the X of the game's window" : "nothing of it over the game's window's X")}");
    }

    // Where a button sits in its row, to set the bank's and the details' side by side: at the row's right end and at
    // its least size, or not.
    static string DevPlace(Control b)
    {
        if (b.GetParent() is not Control row) return "in no row";
        float gap = row.Size.X - (b.Position.X + b.Size.X);
        Vector2 over = b.Size - b.GetCombinedMinimumSize();
        return $"at {b.Position} in its {row.GetType().Name} {row.Size} ({row.GetChildCount()} in it), " +
               $"{(Math.Abs(gap) < 0.5f ? "at its right end" : $"{gap:0.#} px from its right end")}, {(Math.Abs(over.X) < 0.5f && Math.Abs(over.Y) < 0.5f ? "at its least size" : "LARGER than its least size")}";
    }

    // The details' button, once scrolled into sight: pictured, and where it is said.
    void DevDetailsButton(string name)
    {
        if (_view?.DevStatsButton() is not { } shown) return;
        Rect2 at = DevCanvasRect(shown), seen = _view.DevDetailsRect;
        DevCrop($"crop-button-{name}.png", at.Grow(24));
        _ctx.Log($"Dev: detailsbutton {name}: crop-button-{name}.png; the button at {DevScreenRect(at)} on the screen, {(seen.Encloses(at) ? "wholly inside" : "NOT wholly inside")} " +
                 $"the part of the details on screen ({DevScreenRect(seen)}); window scale {_window?.Style.Scale}; {DevLook(shown)}; {DevPlace(shown)}; the details show {_view.DevDetailsShown}");
    }

    // TAB in the game's window, as a player presses it, until it compares with the loadout it began with; the worn
    // items are read before and after: TAB changes what the window compares with, never what is worn.
    string _devTabStart = "", _devWornStart = "";
    int _devTabMost;

    void DevTab(string value)
    {
        string[] parts = value.Split('|');
        string tag = parts[0];
        int presses = int.Parse(parts[1]);
        if (!_stats.IsUp) { DevDrop($"stats {tag}: the game's item stats window is not open: TAB was not pressed"); return; }
        int loadouts = 0;
        string comparing = _stats.DevComparison(), worn = _game == null ? "-" : GameStats.DevWorn(_game, out loadouts);
        // Every loadout once, and the one it began with again.
        if (presses == 0) { _devTabStart = comparing; _devWornStart = worn; _devTabMost = loadouts + 1; }
        _ctx.Log($"Dev: stats {tag}: after {presses} TAB the window compares with {comparing}; the character {worn}");
        bool back = presses > 0 && comparing == _devTabStart;
        if (back || presses >= _devTabMost)
        {
            _ctx.Log($"Dev: stats {tag}: TAB {presses} times: the comparison is {(back ? "back where it began" : "NOT back where it began: press TAB by hand until it is")}; " +
                     $"the worn items are {(worn == _devWornStart ? "the same as before" : "NOT the same as before")}");
            return;
        }
        Input.ParseInputEvent(new InputEventKey { Keycode = Key.Tab, PhysicalKeycode = Key.Tab, Pressed = true });
        _devQueue = new Queue<string>(_devQueue.Prepend($"_tab={tag}|{presses + 1}").Prepend("_tabup"));
    }

    // The X of the game's window, however it was opened.
    void DevAimClose(string name)
    {
        if (_stats.IsUp && _stats.DevCloseButton is { } x && x.IsVisibleInTree()) DevAim(x, "the X of the game's item stats window");
        else DevDrop($"stats {name}: the game's item stats window is not open or shows no X to press{(_stats.IsUp ? ": close it by hand" : "")}");
    }

    // A click beside the game's window, on its own dimmed screen, far from its X: only when the game closes it so.
    void DevAimOutside(string name)
    {
        if (!_stats.IsUp || _stats.DevWindow is not { } whole) { DevDrop($"stats {name}: the game's item stats window is not open"); return; }
        if (!_stats.DevClickOutsideCloses)
        {
            _ctx.Log($"Dev: stats {name}: the game does not close its item stats window with a click beside it ({_stats.DevCloses}): closed with its X");
            DevAimClose(name);
            return;
        }
        Rect2 screen = DevCanvasRect(whole);
        DevAim(whole, "beside the game's item stats window", screen.Position + screen.Size * new Vector2(0.03f, 0.5f), notAButton: true);
    }

    // banksel=part of a name | banksel=wearable: an item of the bank's tab on screen, clicked as a player clicks it.
    void DevBankSelect(string value)
    {
        var items = _stats.DevBankItems();
        (Control Button, Item Item, bool Selected) hit;
        if (value == "wearable") hit = items.FirstOrDefault(i => GameStats.DevWearable(i.Item));
        else
        {
            hit = items.FirstOrDefault(i => DevName(i.Item).Equals(value, StringComparison.OrdinalIgnoreCase));
            if (hit.Button == null) hit = items.FirstOrDefault(i => DevName(i.Item).Contains(value, StringComparison.OrdinalIgnoreCase));
        }
        if (hit.Button == null)
        {
            _ctx.Log($"Dev: banksel {value}: not among the {items.Count} items of the bank's tab on screen ({string.Join(", ", items.Take(12).Select(i => DevName(i.Item)))}): select it by hand");
            return;
        }
        if (hit.Selected) _devQueue = new Queue<string>(_devQueue.Prepend("_banksel=" + hit.Item.Id));
        else
        {
            DevAim(hit.Button, $"{DevName(hit.Item)} in the bank");
            _devQueue = new Queue<string>(new[] { "_press", "_banksel=" + hit.Item.Id }.Concat(_devQueue));
        }
    }

    // After the click: selected in the bank? The encyclopedia shows the same item, to set the two buttons side by side.
    void DevBankSelected(string id)
    {
        var now = _stats.DevBankItems().FirstOrDefault(i => i.Item.Id == id);
        Entry? same = _catalog?.Items.FirstOrDefault(e => e.Item.Id == id);
        if (same != null) _view?.Select(same, false);
        Button? theirs = _stats.DevBankButton();
        _ctx.Log($"Dev: banksel: {id} is {(now.Selected ? "selected" : "NOT selected")} in the bank; the bank's '{theirs?.Text}' button is " +
                 $"{(theirs?.IsVisibleInTree() == true ? "shown" : "not shown")}; the encyclopedia shows {_view?.DevDetailsShown}");
    }

    // The bank's own button pictured, with its look beside what was read from the bank's scene and the details' button.
    void DevBankButton(string name)
    {
        if (_stats.DevBankButton() is not { } theirs || !theirs.IsVisibleInTree())
        {
            _ctx.Log($"Dev: bankbutton {name}: the bank shows no View Item Stats: select a piece of equipment in the bank first (banksel)");
            return;
        }
        Rect2 at = DevCanvasRect(theirs);
        DevCrop($"crop-bankbutton-{name}.png", at.Grow(24));
        bool covered = IsOpen && _window!.Root.GetGlobalRect().Intersects(at);
        _ctx.Log($"Dev: bankbutton {name}: crop-bankbutton-{name}.png; the bank's button at {DevScreenRect(at)} on the screen{(covered ? ", UNDER the encyclopedia" : "")}; {DevLook(theirs)}; {DevPlace(theirs)}");
        _ctx.Log($"Dev: bankbutton {name}: read from the bank's scene: {BankButton.DevScene}");
        // Only a button that is laid out on the screen has a size to compare; detailsbutton pictures it.
        if (_view?.DevStatsButton() is { } ours)
            _ctx.Log($"Dev: bankbutton {name}: the details' button now: " + (IsOpen && !_window!.Minimised ? $"{DevLook(ours)}; {DevPlace(ours)} (window scale {_window.Style.Scale})" : "not on the screen (see detailsbutton)"));
    }

    // Every text the game's item stats window shows, in its order.
    List<string> DevStatsTexts()
    {
        var texts = new List<string>();
        if (_stats.DevWindow is not { } shown) return texts;
        foreach (Node node in shown.FindChildren("*", "", true, false))
        {
            string text = node switch
            {
                Label label when label.IsVisibleInTree() => label.Text,
                RichTextLabel rich when rich.IsVisibleInTree() => rich.GetParsedText(),
                _ => "",
            };
            if (text.Trim() != "") texts.Add(text.Trim().Replace("\n", " / "));
        }
        return texts;
    }

    // "Helmet", "Ring,unowned", "Weapon,owned": the first item of that slot that can be worn, in the game's order.
    Entry? DevPickSlot(string how)
    {
        string[] parts = how.Split(',', StringSplitOptions.TrimEntries);
        bool? owned = parts.Length > 1 ? parts[1] == "owned" : null;
        return _catalog?.Items.FirstOrDefault(e => e.Wearable && string.Equals(e.Slot?.DisplayName.Text, parts[0], StringComparison.OrdinalIgnoreCase)
                                                   && (owned == null || (e.Owned > 0) == owned));
    }

    // The game's item stats window after the click: open, and showing the item that was asked for? Every text it
    // shows goes into the log, to compare with the picture. False when it is not open: no picture of nothing.
    bool DevStatsUp(string name)
    {
        Entry? asked = _state.Selected;
        if (_stats.DevWindow is not { } shown || !_stats.IsUp)
        {
            _ctx.Log($"Dev: stats {name}: the game's item stats window did not open for {asked?.Name ?? "nothing"} ({_stats.DevPath}); no picture taken");
            return false;
        }
        List<string> texts = DevStatsTexts();
        bool named = asked != null && texts.Contains(asked.Name);

        _ctx.Tree.Root.GetTexture().GetImage().SavePng(Path.Combine(_ctx.Directory, $"shot-stats-{name}.png"));
        Rect2 at = DevCanvasRect(shown).Grow(10);
        DevCrop($"crop-stats-{name}.png", at);
        Control? focus = _ctx.Tree.Root.GuiGetFocusOwner();

        _ctx.Log($"Dev: stats {name}: the game's item stats window {(named ? "shows" : "is open but does NOT show")} '{asked?.Name}' " +
                 $"(owned {asked?.Owned}, slot {asked?.Slot?.DisplayName.Text ?? "-"}); shot-stats-{name}.png, window at {DevScreenRect(at)}; its canvas layer {_stats.Layer} ({_stats.DevPath}), " +
                 $"the encyclopedia's {_window?.Layer.Layer}, the encyclopedia {(IsOpen ? "SHOWING" : "put away")} (put away {_aside}, folded {_window?.Minimised}); " +
                 $"the game closes it with {_stats.DevCloses}; the keyboard is with {(focus == null ? "nobody" : focus.GetType().Name + " " + focus.Name)}");
        _ctx.Log($"Dev: stats {name}: {texts.Count} texts in the game's window: {string.Join(" | ", texts)}");
        return true;
    }
#endif
}
