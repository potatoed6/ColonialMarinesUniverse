using System.Linq;
using Content.Shared._RMC14.Xenonids;
using Content.Shared._RMC14.Xenonids.Evolution;
using Content.Shared._RMC14.Xenonids.Hive;
using Content.Shared.CMU14.Xenonids.EvoRush;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Systems;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Shared.Prototypes;

namespace Content.Server.CMU14.Xenonids.EvoRush;

public sealed partial class EvoRushSystem
{
    [Dependency] private IComponentFactory _compFactory = default!;
    [Dependency] private XenoEvolutionSystem _evolution = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private SharedUserInterfaceSystem _ui = default!;

    // First caste of each rushing hive's tree; declined or unplaceable players return here
    private static readonly Dictionary<string, EntProtoId> HiveRoots = new()
    {
        ["CMXenoHive"] = "CMXenoLarva",
        ["CMUPathogenHive"] = "CMU14XenoBloodburster",
    };

    private static readonly FixedPoint2 DeclinePoints = 800;
    private static readonly TimeSpan SpinDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan DecideDuration = TimeSpan.FromSeconds(10);
    private const int MaxPicks = 3;
    private const int ReelLength = 12;

    private readonly Dictionary<EntityUid, List<EntProtoId>> _hiveCastes = new();
    private readonly Dictionary<NetUserId, List<EntProtoId>> _priorities = new();
    private readonly Dictionary<EntityUid, PendingRoll> _pendingRolls = new();
    private readonly List<EntityUid> _rollsDue = new();

    private void InitializeRoll()
    {
        Subs.BuiEvents<XenoComponent>(EvoRushUiKey.CastePriority,
            subs => subs.Event<EvoRushCastePriorityMsg>(OnCastePriorityMsg));
        Subs.BuiEvents<XenoComponent>(EvoRushUiKey.SlotMachine,
            subs => subs.Event<EvoRushSlotMachineDecisionMsg>(OnSlotMachineDecision));
    }

    private void ResetRoll()
    {
        _hiveCastes.Clear();
        _priorities.Clear();
        _pendingRolls.Clear();
    }

    private EntProtoId? GetRoot(EntityUid hive)
        => HiveRoots.TryGetValue(Prototype(hive)?.ID ?? string.Empty, out var root) ? root : (EntProtoId?) null;

    /// <summary>Castes reachable from the hive's root, excluding the queen, capped and hidden castes.</summary>
    private List<EntProtoId> GetHiveCastes(EntityUid hive)
    {
        if (_hiveCastes.TryGetValue(hive, out var cached))
            return cached;

        var castes = new List<EntProtoId>();
        if (GetRoot(hive) is { } root)
        {
            var seen = new HashSet<string> { root.Id };
            var open = new Queue<EntProtoId>();
            open.Enqueue(root);
            while (open.TryDequeue(out var id))
            {
                if (!_prototypes.TryIndex(id, out var proto))
                    continue;

                if (id != root && IsRollableCaste(proto))
                    castes.Add(id);

                if (!proto.TryComp(out XenoEvolutionComponent? evolution, _compFactory))
                    continue;

                foreach (var next in evolution.EvolvesTo.Concat(evolution.EarlyEvolvesTo))
                {
                    if (seen.Add(next.Id))
                        open.Enqueue(next);
                }
            }
        }

        _hiveCastes[hive] = castes;
        return castes;
    }

    private bool IsRollableCaste(EntityPrototype proto)
        => proto.HasComponent<XenoComponent>(_compFactory)
            && !proto.HasComponent<XenoEvolutionGranterComponent>(_compFactory)
            && !proto.HasComponent<XenoEvolutionCappedComponent>(_compFactory)
            && !proto.HasComponent<XenoHiddenComponent>(_compFactory);

    private void TryPromptPriorities(EntityUid xeno, ICommonSession session)
    {
        if (_finished
            || _priorities.ContainsKey(session.UserId)
            || _xenoHive.GetHive(xeno) is not { } hive
            || !hive.Comp.EvoRush
            || !_ui.HasUi(xeno, EvoRushUiKey.CastePriority))
        {
            return;
        }

        var castes = GetHiveCastes(hive.Owner);
        if (castes.Count == 0)
            return;

        _ui.OpenUi(xeno, EvoRushUiKey.CastePriority, xeno);
        _ui.SetUiState(xeno, EvoRushUiKey.CastePriority, new EvoRushCastePriorityBuiState(castes, new()));
    }

    private void OnCastePriorityMsg(Entity<XenoComponent> ent, ref EvoRushCastePriorityMsg args)
    {
        if (!TryComp(args.Actor, out ActorComponent? actor) || _xenoHive.GetHive(ent.Owner) is not { } hive)
            return;

        var castes = GetHiveCastes(hive.Owner);
        var order = new List<EntProtoId>(MaxPicks);
        foreach (var id in args.Order)
        {
            if (order.Count >= MaxPicks)
                break;

            if (castes.Contains(id) && !order.Contains(id))
                order.Add(id);
        }

        if (order.Count == 0)
            return;

        _priorities[actor.PlayerSession.UserId] = order;
        _ui.CloseUi(ent.Owner, EvoRushUiKey.CastePriority, args.Actor);
    }

    private void RollHive(Entity<HiveComponent> hive)
    {
        var castes = GetHiveCastes(hive.Owner);
        if (castes.Count == 0 || GetRoot(hive.Owner) is not { } root)
            return;

        // Rolled xenos are replaced; everyone else keeps occupying their tier.
        var rolled = new List<(EntityUid Xeno, NetUserId User)>();
        var placed = new List<(int Tier, string Role)>();
        var query = EntityQueryEnumerator<XenoComponent, HiveMemberComponent>();
        while (query.MoveNext(out var uid, out var xeno, out var member))
        {
            if (member.Hive != hive.Owner || _mobState.IsDead(uid))
                continue;

            var protoId = Prototype(uid)?.ID;
            if (protoId != null
                && TryComp(uid, out ActorComponent? actor)
                && (protoId == root.Id || castes.Contains(protoId)))
            {
                rolled.Add((uid, actor.PlayerSession.UserId));
                continue;
            }

            if (xeno.CountedInSlots)
                placed.Add((xeno.Tier, xeno.Role.Id));
        }

        // Mirrors the tier-slot check in XenoEvolutionSystem.CanEvolvePopup.
        var total = Math.Min(Math.Sqrt(hive.Comp.BurrowedLarva * hive.Comp.BurrowedLarvaSlotFactor),
            hive.Comp.BurrowedLarva) + placed.Count + rolled.Count;

        bool TryPlace(EntProtoId caste)
        {
            if (!_prototypes.TryIndex(caste, out var proto) || !proto.TryComp(out XenoComponent? comp, _compFactory))
                return false;

            if (comp.UnlockAt > _ticker.RoundDuration())
                return false;

            if (!comp.BypassTierCount && _xenoHive.TryGetTierLimit((hive.Owner, hive.Comp), comp.Tier, out var limit))
            {
                var slotCount = hive.Comp.FreeSlots.ToDictionary();
                var existing = 0;
                foreach (var (tier, role) in placed)
                {
                    if (tier < comp.Tier)
                        continue;

                    if (slotCount.TryGetValue(role, out var free) && free > 0)
                        slotCount[role] = free - 1;
                    else
                        existing++;
                }

                if (total != 0
                    && existing / (float) total >= limit
                    && (!slotCount.TryGetValue(caste, out var casteSlots) || casteSlots <= 0))
                {
                    return false;
                }
            }

            placed.Add((comp.Tier, caste.Id));
            return true;
        }

        _random.Shuffle(rolled);
        foreach (var (xeno, user) in rolled)
        {
            EntProtoId? won = null;
            if (_priorities.TryGetValue(user, out var picks))
            {
                foreach (var pick in picks)
                {
                    if (TryPlace(pick))
                    {
                        won = pick;
                        break;
                    }
                }
            }

            if (won == null)
            {
                var shuffled = castes.ToList();
                _random.Shuffle(shuffled);
                foreach (var caste in shuffled)
                {
                    if (TryPlace(caste))
                    {
                        won = caste;
                        break;
                    }
                }
            }

            OpenSlotMachine(xeno, won ?? root, castes);
        }
    }

    private void OpenSlotMachine(EntityUid xeno, EntProtoId result, List<EntProtoId> castes)
    {
        var now = _timing.CurTime;
        var pending = new PendingRoll(result, now + SpinDuration, now + SpinDuration + DecideDuration);
        _pendingRolls[xeno] = pending;

        _ui.CloseUi(xeno, EvoRushUiKey.CastePriority);
        if (!_ui.HasUi(xeno, EvoRushUiKey.SlotMachine))
            return;

        var reel = new List<EntProtoId>(ReelLength);
        for (var i = 0; i < ReelLength; i++)
        {
            reel.Add(_random.Pick(castes));
        }

        _ui.OpenUi(xeno, EvoRushUiKey.SlotMachine, xeno);
        _ui.SetUiState(xeno, EvoRushUiKey.SlotMachine,
            new EvoRushSlotMachineBuiState(reel, result, pending.SpinEnd, pending.DecideEnd));
    }

    private void OnSlotMachineDecision(Entity<XenoComponent> ent, ref EvoRushSlotMachineDecisionMsg args)
    {
        if (!_pendingRolls.TryGetValue(ent.Owner, out var pending) || _timing.CurTime < pending.SpinEnd)
            return;

        _pendingRolls.Remove(ent.Owner);
        _ui.CloseUi(ent.Owner, EvoRushUiKey.SlotMachine, args.Actor);
        ApplyRoll(ent.Owner, args.Accept ? pending.Result : (EntProtoId?) null);
    }

    private void UpdatePendingRolls()
    {
        if (_pendingRolls.Count == 0)
            return;

        var now = _timing.CurTime;
        _rollsDue.Clear();
        foreach (var (xeno, pending) in _pendingRolls)
        {
            if (pending.DecideEnd <= now)
                _rollsDue.Add(xeno);
        }

        foreach (var xeno in _rollsDue)
        {
            if (!_pendingRolls.Remove(xeno, out var pending))
                continue;

            _ui.CloseUi(xeno, EvoRushUiKey.SlotMachine);
            ApplyRoll(xeno, pending.Result);
        }
    }

    /// <summary>Evolves into the rolled caste regardless of the evolution tree; null means declined.</summary>
    private void ApplyRoll(EntityUid xeno, EntProtoId? caste)
    {
        if (TerminatingOrDeleted(xeno)
            || _mobState.IsDead(xeno)
            || !HasComp<ActorComponent>(xeno)
            || _xenoHive.GetHive(xeno) is not { } hive
            || GetRoot(hive.Owner) is not { } root)
        {
            return;
        }

        var target = caste ?? root;
        var current = Prototype(xeno)?.ID;
        var result = current == target.Id ? xeno : _evolution.EvolveTo(xeno, target);

        if (target == root && TryComp(result, out XenoEvolutionComponent? evolution))
            _evolution.SetPoints((result, evolution), DeclinePoints);
    }

    private readonly record struct PendingRoll(EntProtoId Result, TimeSpan SpinEnd, TimeSpan DecideEnd);
}