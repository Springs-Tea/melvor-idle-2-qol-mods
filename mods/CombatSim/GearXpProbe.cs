#if DEVTOOLS
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GamesByMalcs.Melvor2.Components.Equipment;
using GamesByMalcs.Melvor2.Data.Items;
using GamesByMalcs.Melvor2.Entities.Combat;
using Godot;
using MelvorMods.Shared;

namespace CombatSim;

public partial class CombatSimMod
{
    SimEngine? _devXpRun;

    // Run as one command in DevRequests. The probe owns its picks, copies and result queue.
    void DevXpCheck(string request)
    {
        try
        {
            if (_engine.Running || _state.Running || _run != null || _upgradeRun != null)
                throw new InvalidOperationException("A scan is running.");
            string[] parts = request.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length is < 2 or > 3 || !int.TryParse(parts[1], out int duration) ||
                duration < 0 || duration >= SimState.Durations.Length ||
                (_devSession is { EquipmentOnly: true } probeSession ? probeSession.ProbeSure : _state.Sure) != 0)
                throw new InvalidOperationException("Use sure=0 and gearcheck=<check>|<duration index> with an optional |<enemy name>.");
            string which = parts[0];
            if (which is not ("sword" or "bow" or "shown" or "choice")) throw new InvalidOperationException("Expected sword, bow, shown or choice.");
            if (_game == null || _combat == null) throw new InvalidOperationException("No combat character.");
            var probe = new CombatSimMod { _ctx = _ctx, _game = _game, _combat = _combat, _bank = _bank };
            double hours = SimState.Durations[duration].Hours;
            probe._state.Duration = duration;
            probe._state.Unlimited = true;
            probe._state.Sure = 0;
            probe.BuildCatalog();
            SimTarget target = probe.DevXpEnemy(parts.Length == 3 ? parts[2] : "");
            var jobs = new List<SimJob>();
            foreach (var skill in _game.LeveledEntities.Where(s => s.Id is "MelvorBase:Strength" or "MelvorBase:Ranged"))
                _ctx.Log($"Dev: gear XP: {skill.DisplayName.Text} level {skill.Experience.Level}/{skill.Experience.LevelCap}.");
            void Add(string label, SimOptions options)
            {
                options.Hours = hours;
                options.UnlimitedSupplies = true;
                options.Sure = 0;
                options.SetupLabel = label + " / " + SimText.WithStance(options.SetupLabel, options.StanceName);
                jobs.Add(new SimJob(target, options));
                if (options.StanceId is { } id && StanceOf(_game, id) is { } stance)
                    _ctx.Log($"Dev: gear XP: {stance.DisplayName.Text}, XP per damage from the stance: " +
                        string.Join(", ", stance.ExperienceGain.Select(g => $"{g.Component.Entity.Id} {g.ExperiencePerDamage}")));
                _ctx.Log($"Dev: gear XP: Simulating {target.Name} with {options.SetupLabel}: {options.DevGear}; " +
                    $"{TimeSpan.FromHours(options.Hours).TotalMinutes} minutes, Unlimited, sure={options.Sure}.");
            }
            if (which == "choice")
            {
                Add("Use worn equipment", probe.RunOptions());
                probe.DevPreviousEquipment();
                Add("Keep my choices (shown side)", probe.RunOptions());
            }
            else if (which == "shown")
            {
                if (_window is not { IsAlive: true, Visible: true, Minimised: false } || _view == null || _chooseGear ||
                    _state.BestMode || _state.Running || _state.Compare || _cannotReadWorn || _lastWorn != WornSetup())
                    throw new InvalidOperationException("Show a settled Fights setup with Compare off and resolve any worn equipment choice.");
                Add("Shown on the left", RunOptions());
            }
            else
            {
                // The same controls and signals as the real left side. This request-owned window exists only during
                // this main-thread callback. Its picker never grabs focus and no settings callbacks are attached.
                var window = new GameWindow(_ctx.Tree, WindowTitle, _settings.Scale, null);
                try
                {
                    window.Visible = true;
                    probe._view = probe.DevProbeView(window);
                    if (which == "sword") probe.DevSwordJobs(Add);
                    else probe.DevBowJobs(Add);
                }
                finally { window.Visible = false; window.Free(); }
            }
            var engine = new SimEngine(_ctx.Log);
            try
            {
                if (!engine.Start(_game, jobs, 1, out string error)) throw new InvalidOperationException(error);
                _devXpRun = engine;
            }
            finally
            {
                // The two-frame capture starts after the snapshot. It can still show the question if the snapshot failed.
                if (which == "choice") DevShowChoice(probe);
            }
        }
        catch (Exception e)
        {
            // A snapshot failure does not cancel an already scheduled picture of the question.
            if (_devChoice is { } choice && choice.Probe._devShots.Count == 0) DevEndChoice();
            _ctx.Log("Dev: equipment XP check stopped: " + e.Message);
        }
    }

    SimTarget DevXpEnemy(string name)
    {
        var enemies = _game!.Data.GetDataRegistryOutput<Enemy>();
        var hiddenAreas = _combat!.Areas.ActiveAreas.Values.Where(a => a.DisplayRequirements is { AreMet: false })
            .Select(a => a.Area.Id).ToHashSet();
        SimTarget? lowest = null;
        foreach (SimTarget target in _state.Catalog)
        {
            if (target.EnemyId is not { } id || hiddenAreas.Contains(target.AreaId) || !enemies.TryGetObjectById(id, out Enemy? enemy) ||
                enemy.Requirements is { AreMet: false } || enemy.DisplayRequirements is { AreMet: false }) continue;
            if (lowest == null || target.CombatLevel < lowest.CombatLevel) lowest = target;
            if (name.Length != 0 && target.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                _ctx.Log($"Dev: gear XP: {target.Name} (level {target.CombatLevel}) in {target.AreaName} is the requested unlocked enemy.");
                return target;
            }
        }
        string reason = name.Length == 0 ? "No enemy was named" : $"'{name}' was not available in an unlocked area";
        if (lowest == null) throw new InvalidOperationException(reason + " and there are no unlocked enemies to try.");
        _ctx.Log($"Dev: gear XP: {reason}. Using {lowest.Name} in {lowest.AreaName}, the lowest-level unlocked enemy " +
            $"(combat level {lowest.CombatLevel}, first in catalogue order at that level).");
        return lowest;
    }

    // Only the request's remembered equipment and picker model change. FollowWorn reads the
    // character normally, so it produces the same pending question and slot differences as a real change.
    void DevPreviousEquipment()
    {
        if (_lastWorn is not { } worn) throw new InvalidOperationException("Could not read the worn baseline.");
        GearSlot weapon = DevSlot("Weapon");
        string offhand = DevSlot("Offhand").SlotId;
        var choices = DevWearable("Weapon").Where(c => c.ItemId != weapon.ItemId);
        var items = _game!.Data.GetDataRegistryOutput<Item>();
        GearChoice? pick = choices.OrderByDescending(c => items.TryGetObjectById(c.ItemId, out Item? item) &&
            SimGear.FillsToo(item, weapon.SlotId).Contains(offhand)).FirstOrDefault();
        if (pick == null && weapon.ItemId == null)
            throw new InvalidOperationException("No different wearable weapon in the picker.");
        ((ISimLookup)this).Pick(weapon, pick?.ItemId ?? "");
        BuildGear();
        if (DevSlot("Weapon").ItemId == weapon.ItemId) throw new InvalidOperationException("The probe's weapon pick did not change.");
        _lastWorn = worn with { Equipment = string.Join("; ", _gearWorn!.Slots.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => p.Key + "=" + p.Value.Item.Id)) };
        _ctx.Log($"Dev: worn gear choice: faked the probe's previous worn equipment and simulator pick with " +
            $"{pick?.Name ?? "an empty Weapon slot"}; current equipment is read from the character. No character action was called.");
        if (!CheckWorn() || !_chooseGear || _keepDescription == "")
            throw new InvalidOperationException("The equipment change did not produce a question with slot differences.");
    }

    (CombatSimMod Probe, Action Restore)? _devChoice;

    SimView DevProbeView(GameWindow window) => new(window, _state, this, () => { }, () => { }, () => { }, BuildGear,
        () => { }, _ => { }, () => { }, _chooseGear ? _ => { } : null, _wornDescription, _keepDescription)
        { DevGearProbe = true, DevLog = _ctx.Log };

    void DevEndChoice()
    {
        if (_devChoice is not { } choice) return;
        _devChoice = null;
        // A character can be closed between the request and the second drawn frame.
        foreach (var shot in choice.Probe._devShots) RenderingServer.FramePostDraw -= shot.Capture;
        choice.Probe._devShots.Clear();
        choice.Restore();
    }

    // Only the player's window has callbacks that write settings when it is dragged, folded or resized.
    // Put it aside for the picture, including any open picker, and let the probe own the photographed frame.
    void DevShowChoice(CombatSimMod probe)
    {
        GameWindow? before = _window;
        bool visible = before is { IsAlive: true, Visible: true };
        Control? focus = _ctx.Tree.Root.GuiGetFocusOwner();
        GameWindow window = probe._window = CreateWindow(WindowTitle, false);
        void Restore()
        {
            window.Visible = false;
            window.Free();
            if (visible && _window == before && before!.IsAlive) before.Visible = true;
            if (GodotObject.IsInstanceValid(focus) && focus!.IsVisibleInTree() && !focus.HasFocus()) focus.GrabFocus();
            SyncStripButton();
            _ctx.Log("Dev: worn gear choice: discarded the simulated change and restored the previous window.");
        }
        _devChoice = (probe, Restore);
        if (visible) before!.Visible = false;
        probe._settings.Scale = window.Style.Scale;
        probe._state.History = _state.History.ToList();
        probe._state.Columns = _state.Columns.ToList();
        probe._view = probe.DevProbeView(window);
        window.Visible = true;
        probe.DevShot("worn-gear-choice", probe.DevReadChoice);
    }

    void DevReadChoice(Image image, Transform2D screen)
    {
        var buttons = _view?.DevGearChoiceButtons();
        if (_view?.DevWornNotice is not { } note || note.Text != _wornDescription ||
            _view.DevChoiceList is not { } list || list.Text != _keepDescription ||
            buttons == null || !buttons.Select(b => b.Text).SequenceEqual(new[] { "Use worn equipment", "Keep my choices" }))
            throw new InvalidOperationException("The worn setup and both choices are not on screen.");
        void Read(string words, Control control)
        {
            string pixels = DevDrawn(image, screen, words, control.GetGlobalRect());
            _ctx.Log("Dev: worn gear choice: " + pixels);
        }
        Read(note.Text, note);
        Read(list.Text, list);
        foreach (Button button in buttons) Read(button.Text, button);
        _view.DevMeasureChoiceLines();
        _view.DevMeasureCuts("worn gear choice");
    }

    GearSlot DevSlot(string name) => _state.Gear.FirstOrDefault(g => g.SlotName.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException($"The setup has no {name} slot.");

    IEnumerable<GearChoice> DevWearable(string slot) => ((ISimLookup)this).ChoicesFor(DevSlot(slot))
        .Where(c => c.CanWear).OrderByDescending(c => c.Owned > 0).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

    void DevXpPick(string name, GearChoice? choice)
    {
        var slot = DevSlot(name);
        if (choice == null && slot.ItemId == null) return;
        _view!.DevOpenItems(slot);
        string row = choice?.Name ?? "Nothing (leave it empty)";
        if (!_view.DevPressRow(row) || DevSlot(name).ItemId != choice?.ItemId)
            throw new InvalidOperationException($"The {name} picker did not put '{row}' on the left.");
        string owned = choice == null ? "" : $" (owned: {choice.Owned})";
        _ctx.Log($"Dev: gear XP pick: {name} = {row}{owned}; probe setup: {DevGearText()}.");
    }

    void DevSwordJobs(Action<string, SimOptions> add)
    {
        var items = _game!.Data.GetDataRegistryOutput<Item>();
        GearSlot hand = DevSlot("Weapon"), offhand = DevSlot("Offhand");
        GearChoice sword = DevWearable("Weapon").FirstOrDefault(c => c.Name.Contains("adamant", StringComparison.OrdinalIgnoreCase) &&
            c.Name.Contains("sword", StringComparison.OrdinalIgnoreCase) && items.TryGetObjectById(c.ItemId, out Item? item) &&
            SimGear.FillsToo(item, hand.SlotId).Contains(offhand.SlotId))
            ?? throw new InvalidOperationException("No wearable Adamantite Greatsword or Adamant 2H Sword in the Weapon picker.");
        GearChoice? shield = DevWearable("Offhand").FirstOrDefault(c => c.Name.Contains("shield", StringComparison.OrdinalIgnoreCase));
        if (DevSlot("Offhand").FilledFrom != "") DevXpPick("Weapon", null);
        DevXpPick("Offhand", null);
        foreach (bool withShield in new[] { false, true })
        {
            if (withShield)
            {
                if (shield == null)
                {
                    _ctx.Log("Dev: gear XP: no wearable shield in the Offhand picker. Skipped the shield cases only.");
                    return;
                }
                DevXpPick("Offhand", shield);
            }
            string before = DevSlot("Offhand").ItemName;
            DevXpPick("Weapon", sword);
            if (DevSlot("Offhand").FilledFrom != DevSlot("Weapon").SlotId || _state.Stances.Count == 0)
                throw new InvalidOperationException("The sword did not fill both hands or no stances were read.");
            foreach (var stance in _state.Stances)
            {
                if (!_view!.DevChooseStance(stance.Name) || _state.ChosenStance?.Id != stance.Id)
                    throw new InvalidOperationException($"The Stance box did not select {stance.Name}.");
                add($"{sword.Name}, Offhand previously {before.Or("empty")}", RunOptions());
            }
        }
    }

    void DevBowJobs(Action<string, SimOptions> add)
    {
        add("Worn before the picks", RunOptions());
        var items = _game!.Data.GetDataRegistryOutput<Item>();
        // WeaponCapability.RequiredAmmoType and EquipmentCapability.AmmoType are the game's compatibility IDs.
        var arrows = DevWearable("Ammo").Where(c => c.Name.Contains("arrow", StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var bow in DevWearable("Weapon").Where(c => c.Name.Contains("bow", StringComparison.OrdinalIgnoreCase)))
        {
            if (!items.TryGetObjectById(bow.ItemId, out Item? item) || !item.Capabilities.TryGet(out WeaponCapability? weapon) ||
                weapon.RequiredAmmoType is not { } needed) continue;
            GearChoice? arrow = arrows.FirstOrDefault(c => items.TryGetObjectById(c.ItemId, out Item? ammo) &&
                ammo.Capabilities.TryGet(out EquipmentCapability? equip) && equip.AmmoType?.Id == needed.Id);
            if (arrow == null) continue;
            DevXpPick("Weapon", bow);
            DevXpPick("Ammo", arrow);
            // AttackStance.ExperienceGain names the skills the stance trains. A single stance needs no selection.
            var trains = weapon.Type.Stances.FirstOrDefault(s => s.ExperienceGain.Any(g =>
                g.Component.Entity.Id == "MelvorBase:Ranged" && g.ExperiencePerDamage > 0));
            if (trains != null && _state.Stances.Count > 1 &&
                (!_view!.DevChooseStance(trains.DisplayName.Text) || _state.ChosenStance?.Id != trains.Id))
                throw new InvalidOperationException("The Stance box did not select the bow's Ranged stance.");
            add($"{bow.Name} with {arrow.Name}", RunOptions());
            return;
        }
        throw new InvalidOperationException("No wearable bow and matching arrows were found in the pickers.");
    }

    bool DevXpFinishing()
    {
        if (_devXpRun is not { } engine) return false;
        if (engine.Running) return true;
        _devXpRun = null;
        int count = 0;
        while (engine.Finished.TryDequeue(out SimResult? r))
        {
            count++;
            if (r.Failed || r.Hours <= 0)
            {
                _ctx.Log($"Dev: gear XP result: {r.Target.Name}, {r.SetupLabel}: no measured rates. {r.Note}");
                continue;
            }
            if (r.Kills == 0)
            {
                var items = _game!.Data.GetDataRegistryOutput<Item>();
                var ammunition = r.Used.Where(a => !a.IsCurrency && items.TryGetObjectById(a.Id, out Item? item) &&
                    item.Capabilities.TryGet(out EquipmentCapability? equip) && equip.AmmoType != null);
                string names = string.Join(", ", ammunition.Select(a => a.Name));
                string total = r.AmmoUsed?.ToString("N0") ?? "unknown";
                _ctx.Log($"Dev: gear XP result: {r.Target.Name}, {r.SetupLabel}: 0 kills in {r.Hours:0.####} h. " +
                    $"Result stance '{r.StanceShown.Or("unknown")}', ammo used {total}{(names == "" ? "" : $" ({names})")}. " +
                    "No kill-based comparison for this setup.");
                continue;
            }
            double Xp(string skill) => r.PerHour(r.Xp.GetValueOrDefault("MelvorBase:" + skill));
            string ammo = r.AmmoUsed is { } used ? r.PerHour(used).ToString("N1") : "unknown";
            string supplies = string.Join(", ", r.Used.Select(a => $"{a.Name} {r.PerHour(a.Quantity):N1}/h"));
            _ctx.Log($"Dev: gear XP result: {r.Target.Name}, {r.SetupLabel}; result stance '{r.StanceShown}', " +
                $"Strength {Xp("Strength"):N1} XP/h, Ranged {Xp("Ranged"):N1} XP/h, ammo {ammo}/h, " +
                $"{r.Kills} kills in {r.Hours:0.####} h ({r.KillsPerHour:N1} kills/h); " +
                $"supplies {supplies.Or("none")}; copy {r.SetupText ?? "unavailable"}; {r.Note}");
        }
        _ctx.Log($"Dev: read {count} of {engine.Total} gear XP results. {engine.Problem} " +
            "Probe results discarded.");
        return true;
    }
}

sealed partial class SimView
{
    public bool DevGearProbe;
    VBoxContainer? _devGearChoice;

    public Label? DevWornNotice { get; private set; }
    public Label? DevChoiceList { get; private set; }

    // A wrapping label can fit all its words and still have a line below the scroll viewport.
    // Read Godot's character bounds on the captured frame (as CutProbe.DevDrawnLines does).
    public void DevMeasureChoiceLines()
    {
        foreach (var label in DevWordsUnder(_devGearChoice!).OfType<Label>())
        {
            DevLog?.Invoke("Dev: worn gear choice: " + DevFit(label).Said);
            Rect2 visible = label.GetViewportRect().Intersection(label.GetGlobalRect());
            for (Node parent = label.GetParent(); parent is Control c; parent = c.GetParent())
                if (c.ClipContents || c is ScrollContainer) visible = visible.Intersection(c.GetGlobalRect());
            int index = 0;
            foreach (string line in label.Text.Split('\n'))
            {
                bool cut = false, unread = false;
                foreach (var letter in line.EnumerateRunes())
                {
                    Rect2 box = label.GetCharacterBounds(index++);
                    if (Rune.IsWhiteSpace(letter)) continue;
                    if (!box.HasArea()) unread = true;
                    else if (!visible.Grow(0.5f).Encloses(label.GetGlobalTransform() * box)) cut = true;
                }
                index++; // The newline between differences.
                string verdict = cut ? "CUT or outside the visible area" : unread ? "could not measure every character" : "whole on screen";
                DevLog?.Invoke($"Dev: worn gear choice: '{line}': {verdict}.");
            }
        }
    }

    public List<Button> DevGearChoiceButtons() => _devGearChoice is { } body && body.IsVisibleInTree()
        ? body.GetChildren().OfType<Button>().Where(b => b.IsVisibleInTree()).ToList() : new();
}
#endif
