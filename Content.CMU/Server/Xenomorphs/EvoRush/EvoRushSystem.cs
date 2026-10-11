using Content.Server.CMU14.Round;
using Content.Server.GameTicking;
using Content.Shared._RMC14.Rules;
using Content.Shared._RMC14.Xenonids.Announce;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared.GameTicking;
using Robust.Shared.Timing;
using Content.Server._RMC14.Xenonids.JoinXeno;
using Content.Shared._RMC14.Xenonids;
using Content.Shared.Ghost.Components;
using Content.Shared.Mobs;
using Robust.Shared.Network;
using Robust.Shared.Player;

namespace Content.Server.CMU14.Xenonids.EvoRush;

/// <summary>
/// Distress Signal evolution rush: listed hives can evolve freely until the timer or the marine landing
/// (plus a short grace period) ends it
/// </summary>
public sealed class EvoRushSystem : EntitySystem
{
    [Dependency] private AuRoundSystem _auRound = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private SharedXenoAnnounceSystem _xenoAnnounce = default!;
    [Dependency] private LarvaQueueSystem _larvaQueue = default!;
    [Dependency] private SharedXenoHiveSystem _xenoHive = default!;

    private const string DistressSignalPresetId = "DistressSignal";
    private static readonly HashSet<string> RushHives = new() { "CMXenoHive", "CMUPathogenHive" };
    private static readonly TimeSpan Duration = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan LandingGrace = TimeSpan.FromMinutes(2);

    private static readonly TimeSpan ReviveGhostWindow = TimeSpan.FromSeconds(45);
    private readonly Dictionary<NetUserId, (EntityUid Hive, TimeSpan ExpiresAt)> _pendingRevives = new();
    private readonly List<NetUserId> _expiredRevives = new();

    private TimeSpan _endAt;
    private TimeSpan _nextCheck;
    private bool _started;
    private bool _warned;
    private bool _finished;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
        SubscribeLocalEvent<MarinesLandedChangedEvent>(OnMarinesLanded);
        SubscribeLocalEvent<XenoComponent, MobStateChangedEvent>(OnXenoMobStateChanged);
        SubscribeLocalEvent<PlayerAttachedEvent>(OnPlayerAttached);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        _started = false;
        _warned = false;
        _finished = false;
        _nextCheck = TimeSpan.Zero;
    }

    public override void Update(float frameTime)
    {
        UpdatePendingRevives();
        if (_finished || _ticker.RunLevel != GameRunLevel.InRound)
            return;

        var now = _timing.CurTime;
        if (now < _nextCheck)
            return;

        _nextCheck = now + TimeSpan.FromSeconds(1);

        if (!_started)
        {
            TryStart(now);
            return;
        }

        if (now >= _endAt)
            End();
    }

    private void TryStart(TimeSpan now)
    {
        var presetId = _auRound.SelectedPreset?.ID;
        if (presetId == null)
            return;

        if (!string.Equals(presetId, DistressSignalPresetId, StringComparison.OrdinalIgnoreCase))
        {
            _finished = true;
            return;
        }

        var started = false;
        var query = EntityQueryEnumerator<HiveComponent>();
        while (query.MoveNext(out var uid, out var hive))
        {
            if (!RushHives.Contains(Prototype(uid)?.ID ?? string.Empty))
                continue;

            hive.EvoRush = true;
            Dirty(uid, hive);
            _xenoAnnounce.AnnounceToHive(default, (uid, hive),
                Loc.GetString("cmu-evo-rush-start", ("minutes", (int) Duration.TotalMinutes)));
            started = true;
        }

        // hives are created during round start, so keep retrying until they exist
        if (!started)
            return;

        _started = true;
        _endAt = now + Duration;
    }

    private void OnMarinesLanded(ref MarinesLandedChangedEvent ev)
    {
        if (!_started || _finished || _warned || !ev.Landed)
            return;

        _warned = true;
        _endAt = TimeSpan.FromTicks(Math.Min(_endAt.Ticks, (_timing.CurTime + LandingGrace).Ticks));

        var query = EntityQueryEnumerator<HiveComponent>();
        while (query.MoveNext(out var uid, out var hive))
        {
            if (hive.EvoRush)
                _xenoAnnounce.AnnounceToHive(default, (uid, hive), Loc.GetString("cmu-evo-rush-landing-warning"));
        }
    }

    private void End()
    {
        _finished = true;

        var query = EntityQueryEnumerator<HiveComponent>();
        while (query.MoveNext(out var uid, out var hive))
        {
            if (!hive.EvoRush)
                continue;

            hive.EvoRush = false;
            Dirty(uid, hive);
            _xenoAnnounce.AnnounceToHive(default, (uid, hive), Loc.GetString("cmu-evo-rush-end"));

            var ended = new EvoRushEndedEvent(uid);
            RaiseLocalEvent(ref ended);
        }
    }

    // CMU14 method: a player xeno dying in a rushing hive gets priority on a burrowed larva
    private void OnXenoMobStateChanged(Entity<XenoComponent> ent, ref MobStateChangedEvent args)
    {
        if (args.NewMobState != MobState.Dead
            || !TryComp(ent, out ActorComponent? actor)
            || _xenoHive.GetHive(ent.Owner) is not { } hive
            || !hive.Comp.EvoRush)
        {
            return;
        }

        _pendingRevives[actor.PlayerSession.UserId] = (hive.Owner, _timing.CurTime + ReviveGhostWindow);
    }

    private void OnPlayerAttached(PlayerAttachedEvent ev)
    {
        if (_pendingRevives.Count == 0
            || !HasComp<GhostComponent>(ev.Entity)
            || !_pendingRevives.Remove(ev.Player.UserId, out var pending))
        {
            return;
        }

        GrantRevive(pending.Hive, ev.Player.UserId);
    }

    private void UpdatePendingRevives()
    {
        if (_pendingRevives.Count == 0)
            return;

        var now = _timing.CurTime;
        _expiredRevives.Clear();
        foreach (var (user, pending) in _pendingRevives)
        {
            if (pending.ExpiresAt <= now)
                _expiredRevives.Add(user);
        }

        foreach (var user in _expiredRevives)
        {
            if (_pendingRevives.Remove(user, out var pending))
                GrantRevive(pending.Hive, null);
        }
    }

    // Queue priority first: the larva added below is then offered to the front of the queue.
    private void GrantRevive(EntityUid hiveUid, NetUserId? user)
    {
        if (TerminatingOrDeleted(hiveUid) || !TryComp(hiveUid, out HiveComponent? hive))
            return;

        if (user is { } userId)
            _larvaQueue.AddToLarvaQueueFront((hiveUid, hive), userId);

        _xenoHive.ChangeBurrowedLarva((hiveUid, hive), 1);
    }
}

// CMU14 event: raised per hive when its rush ends (the slot machine hooks here later)
[ByRefEvent]
public readonly record struct EvoRushEndedEvent(EntityUid Hive);