using Content.Server.EUI;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.Eui;

namespace Content.Server.CMU14.Survivors.Setup;

public sealed class SurvivorSetupEui(SurvivorSetupSystem system) : BaseEui
{
    public override EuiStateBase GetNewState() => system.BuildState(Player.UserId);

    public override void Opened()
    {
        base.Opened();
        StateDirty(); // CMU14: send the initial state, otherwise the window opens empty
    }

    public override void HandleMessage(EuiMessageBase msg)
    {
        base.HandleMessage(msg);
        switch (msg)
        {
            case SurvivorVoteMsg vote:
                system.Vote(Player.UserId, vote.PartyId);
                break;
            case SurvivorClaimSlotMsg claim:
                system.ClaimSlot(Player.UserId, claim.Slot);
                break;
            case SurvivorPickCharacterMsg pick:
                system.PickCharacter(Player.UserId, pick.Slot);
                break;
        }
    }
}