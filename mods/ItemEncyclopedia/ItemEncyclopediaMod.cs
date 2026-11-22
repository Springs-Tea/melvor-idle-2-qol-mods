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

    public void OnLoad(ModContext ctx)
    {
        _ctx = ctx;
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
            }
            if (_game == null) return;

            // The key is looked at on every frame, so a short press is not missed; everything else goes by the clock.
            bool down = Input.IsKeyPressed(OpenKey);
            // The key opens a window that is folded down to its title before it closes one.
            if (down && !_keyWasDown) { if (!IsOpen) Open(); else if (_window!.Minimised) _window.SetMinimised(false); else Close(); }
            _keyWasDown = down;

            TakeIndex();
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
        try { _view?.Release(); _window?.Free(); } catch { }
        if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) _stripButton.QueueFree();
    }

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
        if (_window is not { IsAlive: true }) Build();
        else _view?.OwnedChanged();
        _window!.Visible = true;
        SyncStripButton();
    }

    void Close()
    {
        // Putting the window away is one of the moments that keep what was asked among the recent ones.
        _view?.NoteAsked();
        if (_window is { IsAlive: true }) _window.Visible = false;
        SyncStripButton();
        if (_unsaved) WriteSettings();
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
        _view = new EncyclopediaView(_window, _catalog!, _state, Remember, Rebuild, _ctx.Log);
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
            _window!.Visible = true;
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
            if (_window is { IsAlive: true }) { bool open = IsOpen; Build(); _window!.Visible = open; }
        }
        if (_readIndex is not { } index) return;
        _readIndex = null;
        _catalog.Attach(index, _readParents ?? new Dictionary<object, object>(ReferenceEqualityComparer.Instance));
        _readParents = null;
        if (_window is { IsAlive: true }) { bool open = IsOpen; Build(); _window!.Visible = open; }
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
    //   open | close | reset | filters=open | filters=closed | fold=types,slots (the groups to fold away) | scale=0.8
    //   search=bow | sort=3 | kind=Weapon,Armour | slot=Torso | style=Ranged | weapon=Bow
    //   skill=Cooking | mode=1 | from=Enemy drop | area=part of an area's name | show=owned,found,missing,nospoilers
    //   select=part of an item's name | press=link | press=more | press=back | press=part of a linked item's name
    //   scroll=5 | details=400 | wait | shot=name   (saves shot-name.png, the whole screen, and crop-name.png, the window)
    //   dump   (items-dump.txt and icons-dump.txt, see Dev.cs)
    //   bank | shop   (the game's own screens, to compare with) | screen=name   (shot-name.png, the whole screen)
    //   pick=longest | pick=sources | pick=uses | pick=nosource | pick=plain   (the items that are hardest to show)
    // And through the game's own input, as a player's hand would (the pointer is not moved, the events are made):
    //   key   (F9 down and up) | button   (the round button in the strip) | minimise   (the button beside the X)
    //   click=row5 | click=search | click=Filters
    //   wheel=row5 | type=bow | drag=window,-200,60 | drag=corner,-150,-90
    // When the last line is done, "enc.done" is written.
    Queue<string> _devQueue = new();
    readonly Tempo _devLook = new(300);
    ulong _devNext;

    void DevRequests()
    {
        string request = Path.Combine(_ctx.Directory, "enc.request");
        if (_devQueue.Count == 0)
        {
            if (!_devLook.Due() || !File.Exists(request)) return;
            foreach (string line in File.ReadAllLines(request)) if (line.Trim() != "") _devQueue.Enqueue(line.Trim());
            File.Delete(request);
            _devNext = Time.GetTicksMsec() + 200;
            return;
        }
        if (Time.GetTicksMsec() < _devNext) return;
        string command = _devQueue.Dequeue();
        int cut = command.IndexOf('=');
        string verb = cut < 0 ? command : command[..cut], value = cut < 0 ? "" : command[(cut + 1)..];
        _devNext = Time.GetTicksMsec() + (ulong)(verb is "shot" or "wait" ? 900 : 250);
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
            case "scale": _settings.Scale = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture); break;
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
                    _ => null,
                };
                if (odd != null) _view?.Select(odd, false);
                _ctx.Log(odd == null ? $"Dev: pick {value} -> nothing"
                    : $"Dev: pick {value} -> {odd.Name} ({_catalog?.Index?.For(odd.Item)?.Sources.Count ?? 0} sources, {_catalog?.Index?.For(odd.Item)?.Uses.Count ?? 0} uses)");
                break;
            case "key":
                rebuild = false;
                Input.ParseInputEvent(new InputEventKey { Keycode = OpenKey, PhysicalKeycode = OpenKey, Pressed = true });
                _devQueue = new Queue<string>(_devQueue.Prepend("_keyup"));
                break;
            case "_keyup":
                rebuild = false;
                Input.ParseInputEvent(new InputEventKey { Keycode = OpenKey, PhysicalKeycode = OpenKey, Pressed = false });
                _ctx.Log($"Dev: F9 pressed -> window open = {IsOpen}");
                break;
            case "button":
                rebuild = false;
                if (_stripButton != null && GodotObject.IsInstanceValid(_stripButton)) DevMouse(_stripButton.GetGlobalRect().GetCenter(), MouseButton.Left, "strip button");
                else _ctx.Log("Dev: no strip button");
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
                _ctx.Log($"Dev: {value} -> window open = {IsOpen}, filters open = {_state.FiltersOpen}, selected = {_state.Selected?.Name ?? "-"}, scroll = {_state.Scroll}, {_view?.DevCount} items: {_view?.DevRows(4)}");
                break;
            default: _ctx.Log("Dev: unknown command " + command); rebuild = false; break;
        }
        if (rebuild && _catalog != null)
        {
            _state.Scroll = 0;
            bool open = IsOpen;
            Build();
            _window!.Visible = open;
            _ctx.Log($"Dev: {command} -> {_view?.DevCount} items: {_view?.DevRows(6)}");
        }
        if (_devQueue.Count == 0) File.WriteAllText(Path.Combine(_ctx.Directory, "enc.done"), DateTime.Now.ToString("O"));
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
#endif
}
