using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.CMU14.Xenonids.EvoRush;

[Serializable, NetSerializable]
public enum EvoRushUiKey : byte
{
    CastePriority,
    SlotMachine,
}

[Serializable, NetSerializable]
public sealed class EvoRushCastePriorityBuiState(List<EntProtoId> castes, List<EntProtoId> current) : BoundUserInterfaceState
{
    public readonly List<EntProtoId> Castes = castes;
    public readonly List<EntProtoId> Current = current;
}

[Serializable, NetSerializable]
public sealed class EvoRushCastePriorityMsg(List<EntProtoId> order) : BoundUserInterfaceMessage
{
    public readonly List<EntProtoId> Order = order;
}

[Serializable, NetSerializable]
public sealed class EvoRushSlotMachineBuiState(
    List<EntProtoId> reel,
    EntProtoId result,
    TimeSpan spinEnd,
    TimeSpan decideEnd) : BoundUserInterfaceState
{
    public readonly List<EntProtoId> Reel = reel;
    public readonly EntProtoId Result = result;
    public readonly TimeSpan SpinEnd = spinEnd;
    public readonly TimeSpan DecideEnd = decideEnd;
}

[Serializable, NetSerializable]
public sealed class EvoRushSlotMachineDecisionMsg(bool accept) : BoundUserInterfaceMessage
{
    public readonly bool Accept = accept;
}