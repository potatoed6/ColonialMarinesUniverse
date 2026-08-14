using Content.Server._RMC14.Marines.Roles.Ranks;
using Content.Shared._AU14.Marines.Roles.Ranks;
using Content.Shared._RMC14.Marines;
using Content.Shared._RMC14.Marines.Roles.Ranks;
using Content.Shared._RMC14.UniformAccessories;
using Content.Shared.Access.Components;
using Content.Shared.Hands;
using Content.Shared.Inventory;
using Content.Shared.Inventory.Events;
using Content.Shared.Roles;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared._CMU14.Marines.Roles.Ranks;

namespace Content.Server._AU14.Marines.Roles.Ranks;

public sealed partial class RankChangerSystem : EntitySystem
{
    [Dependency] private RankSystem _rank = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedMarineSystem _marine = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RankChangerComponent, GotEquippedEvent>(OnEquipped);
        SubscribeLocalEvent<RankChangerComponent, GotUnequippedEvent>(OnUnequipped);
        SubscribeLocalEvent<RankChangerComponent, ComponentShutdown>(OnShutdown);
        SubscribeLocalEvent<RankChangerComponent, GotEquippedHandEvent>(OnEquippedHand);
        SubscribeLocalEvent<RankChangerComponent, GotUnequippedHandEvent>(OnUnequippedHand);
    }

    private void OnEquipped(Entity<RankChangerComponent> ent, ref GotEquippedEvent args)
    {
        ApplyRank(args.Equipee, ent.Comp);
    }

    private void OnUnequipped(Entity<RankChangerComponent> ent, ref GotUnequippedEvent args)
    {
        RevertRank(args.Equipee, ent.Comp);
    }

    private void OnShutdown(Entity<RankChangerComponent> ent, ref ComponentShutdown args)
    {
        if (_containers.TryGetContainingContainer(ent.Owner, out var container))
            RevertRank(container.Owner, ent.Comp);
    }

    public void ApplyRank(EntityUid wearer, RankChangerComponent comp)
    {
        if (IsAnyChevronActive(wearer, comp))
            return;

        if (!TryResolveAndApply(wearer, comp.Rank, out var resolvedProto))
            return;

        comp.Applied = true;

        var jobId = _rank.GetJobId(wearer);
        if (jobId != null
            && _prototypes.TryIndex<JobPrototype>(jobId.Value, out var jobProto)
            && jobProto.HasIcon
            && _prototypes.TryIndex(jobProto.Icon, out var iconProto))
            _marine.SetMarineIcon(wearer, iconProto.Icon);
    }

    public void RevertRank(EntityUid wearer, RankChangerComponent comp)
    {
        if (!comp.Applied)
            return;

        comp.Applied = false;

        if (!TryComp<RankComponent>(wearer, out var rankComp) || rankComp.Rank != comp.Rank)
            return;

        if (!HasComp<InventoryComponent>(wearer))
        {
            _rank.ReapplyJobRank(wearer);
            return;
        }

        var invSystem = EntityManager.System<InventorySystem>();
        foreach (var item in invSystem.GetHandOrInventoryEntities(wearer))
        {
            // Check item itself
            if (TryComp<RankChangerComponent>(item, out var changer) && changer != comp)
            {
                if (TryResolveAndApply(wearer, changer.Rank, out _))
                {
                    changer.Applied = true;
                    return;
                }
            }

            // Check accessory slot on item
            if (TryComp<UniformAccessoryHolderComponent>(item, out var holder)
                && _containers.TryGetContainer(item, holder.ContainerId, out var container))
            {
                foreach (var accessory in container.ContainedEntities)
                {
                    if (TryComp<RankChangerComponent>(accessory, out var accessoryChanger)
                        && accessoryChanger != comp
                        && TryResolveAndApply(wearer, accessoryChanger.Rank, out _))
                    {
                        accessoryChanger.Applied = true;
                        return;
                    }
                }
            }
        }

        // No other chevron found — restore job rank
        _rank.ReapplyJobRank(wearer);
        _marine.ClearMarineIcon(wearer);
    }

    private bool TryResolveAndApply(EntityUid wearer, ProtoId<RankPrototype> baseRank, out RankPrototype resolvedProto)
    {
        resolvedProto = default!;

        if (!_prototypes.TryIndex(baseRank, out var proto))
            return false;

        resolvedProto = proto;
        _rank.SetRank(wearer, proto);

        var changedEv = new MarineRankChangedEvent(baseRank, proto);
        RaiseLocalEvent(wearer, ref changedEv);
        return true;
    }

    private bool IsAnyChevronActive(EntityUid wearer, RankChangerComponent excluded)
    {
        if (!TryComp<InventoryComponent>(wearer, out var inventory))
            return false;

        var invSystem = EntityManager.System<InventorySystem>();
        foreach (var item in invSystem.GetHandOrInventoryEntities(wearer))
        {
            if (TryComp<RankChangerComponent>(item, out var changer)
                && changer != excluded
                && changer.Applied)
                return true;

            if (TryComp<UniformAccessoryHolderComponent>(item, out var holder)
                && _containers.TryGetContainer(item, holder.ContainerId, out var container))
            {
                foreach (var accessory in container.ContainedEntities)
                {
                    if (TryComp<RankChangerComponent>(accessory, out var accessoryChanger)
                        && accessoryChanger != excluded
                        && accessoryChanger.Applied)
                        return true;
                }
            }
        }

        return false;
    }

    private void OnEquippedHand(Entity<RankChangerComponent> ent, ref GotEquippedHandEvent args)
    {
        ApplyRank(args.User, ent.Comp);
    }

    private void OnUnequippedHand(Entity<RankChangerComponent> ent, ref GotUnequippedHandEvent args)
    {
        RevertRank(args.User, ent.Comp);
    }
}