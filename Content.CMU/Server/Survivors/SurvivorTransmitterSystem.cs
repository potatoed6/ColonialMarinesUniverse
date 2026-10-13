using Content.Server.Power.EntitySystems;
using Content.Server._RMC14.Marines;
using Content.Shared._RMC14.Areas;
using Content.Shared._RMC14.Power;
using Content.Shared._RMC14.TacticalMap;
using Content.Shared._RMC14.Xenonids;
using Content.Shared.Administration.Logs;
using Content.Shared.Database;
using Content.Shared.Interaction;
using Content.Shared.Popups;
using Content.Shared.PowerCell;
using Robust.Shared.Timing;

namespace Content.Server.CMU14.Survivors.Transmitter;

public sealed class SurvivorTransmitterSystem : EntitySystem
{
    [Dependency] private ISharedAdminLogManager _adminLog = default!;
    [Dependency] private AreaSystem _area = default!;
    [Dependency] private BatterySystem _battery = default!;
    [Dependency] private PowerCellSystem _cell = default!;
    [Dependency] private MarineAnnounceSystem _marineAnnounce = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private SharedRMCPowerSystem _rmcPower = default!;
    [Dependency] private SharedTacticalMapSystem _tacticalMap = default!;
    [Dependency] private IGameTiming _timing = default!;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<SurvivorTransmitterComponent, ActivateInWorldEvent>(OnActivate);
    }

    private void OnActivate(Entity<SurvivorTransmitterComponent> ent, ref ActivateInWorldEvent args)
    {
        if (args.Handled || !args.Complex || HasComp<XenoComponent>(args.User))
            return;

        args.Handled = true;
        var user = args.User;

        if (!Transform(ent).Anchored)
        {
            _popup.PopupEntity(Loc.GetString("cmu-survivor-transmitter-unanchored"), ent, user, PopupType.SmallCaution);
            return;
        }

        var now = _timing.CurTime;
        if (now < ent.Comp.NextTransmitAt)
        {
            var seconds = (int) Math.Ceiling((ent.Comp.NextTransmitAt - now).TotalSeconds);
            _popup.PopupEntity(Loc.GetString("cmu-survivor-transmitter-cooldown", ("seconds", seconds)),
                ent, user, PopupType.SmallCaution);
            return;
        }

        if (!TryPayPower(ent))
        {
            _popup.PopupEntity(Loc.GetString("cmu-survivor-transmitter-no-power"), ent, user, PopupType.SmallCaution);
            return;
        }

        ent.Comp.NextTransmitAt = now + ent.Comp.Cooldown;

        var location = _area.TryGetArea(ent, out _, out var area)
            ? area.Name
            : Loc.GetString("cmu-survivor-transmitter-unknown-location");

        _marineAnnounce.AnnounceToMarines(
            Loc.GetString("cmu-survivor-transmitter-announcement", ("location", location)),
            ent.Comp.Sound,
            faction: ent.Comp.Faction);

        AddTacMapMarker(ent);

        _popup.PopupEntity(Loc.GetString("cmu-survivor-transmitter-sent"), ent, user, PopupType.Medium);
        _adminLog.Add(LogType.Action, LogImpact.Low,
            $"{ToPrettyString(user):user} sent a survivor distress call from {ToPrettyString(ent):transmitter} at {location}");
    }

    /// <summary>Area power is free; otherwise the inserted cell pays for the call.</summary>
    private bool TryPayPower(Entity<SurvivorTransmitterComponent> ent)
    {
        if (_rmcPower.IsPowered(ent))
            return true;

        if (!_cell.TryGetBatteryFromSlot(ent.Owner, out var battery))
            return false;

        var cell = battery.Value.AsNullable();
        if (_battery.GetCharge(cell) < ent.Comp.BatteryCost)
            return false;

        _battery.UseCharge(cell, ent.Comp.BatteryCost);
        return true;
    }

    /// <summary>
    ///     Puts the transmitter on the GOVFOR tactical map; TacticalMapSystem places it in the GOVFOR bucket
    ///     on its next tick, and TacticalMapAlwaysVisible shows it without a map republish.
    /// </summary>
    private void AddTacMapMarker(Entity<SurvivorTransmitterComponent> ent)
    {
        if (HasComp<TacticalMapTrackedComponent>(ent))
            return;

        // The faction bucket must exist before tracking starts.
        EnsureComp<GovforMapTrackedComponent>(ent);
        EnsureComp<TacticalMapTrackedComponent>(ent);
        _tacticalMap.RefreshTracked(ent);
    }
}