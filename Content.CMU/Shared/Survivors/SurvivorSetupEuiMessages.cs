using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared.CMU14.Survivors.Setup;

[Serializable, NetSerializable]
public enum SurvivorSetupPhase : byte
{
    Vote,
    Pick,
}

[Serializable, NetSerializable]
public sealed record SurvivorPartyOption(string Id, string Name, int Votes);

[Serializable, NetSerializable]
public sealed record SurvivorRoleSlot(int Index, string Name, bool Leader, string? ClaimedBy);

[Serializable, NetSerializable]
public sealed record SurvivorCharacterOption(int Slot, string Name);

[Serializable, NetSerializable]
public sealed class SurvivorSetupEuiState : EuiStateBase
{
    public SurvivorSetupPhase Phase;
    public TimeSpan PhaseEnd;
    public List<SurvivorPartyOption> Parties = new();
    public string? MyVote;
    public List<SurvivorRoleSlot> Slots = new();
    public int? MySlot;
    public List<SurvivorCharacterOption> Characters = new();

    /// <summary>Saved character slot; null means a random character.</summary>
    public int? MyCharacter;
}

[Serializable, NetSerializable]
public sealed class SurvivorVoteMsg(string partyId) : EuiMessageBase
{
    public readonly string PartyId = partyId;
}

[Serializable, NetSerializable]
public sealed class SurvivorClaimSlotMsg(int slot) : EuiMessageBase
{
    public readonly int Slot = slot;
}

[Serializable, NetSerializable]
public sealed class SurvivorPickCharacterMsg(int? slot) : EuiMessageBase
{
    public readonly int? Slot = slot;
}