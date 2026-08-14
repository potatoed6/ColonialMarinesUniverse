using Robust.Shared.Prototypes;
using Content.Shared._RMC14.Marines.Roles.Ranks;

namespace Content.Shared._CMU14.Marines.Roles.Ranks;

[ByRefEvent]
public readonly record struct MarineRankChangedEvent(ProtoId<RankPrototype> BaseRank, RankPrototype ResolvedRank);