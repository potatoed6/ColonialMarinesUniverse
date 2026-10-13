using Content.Client.Eui;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.Eui;
using JetBrains.Annotations;

namespace Content.Client.CMU14.Survivors;

[UsedImplicitly]
public sealed class SurvivorSetupEui : BaseEui
{
    private readonly SurvivorSetupWindow _window;

    public SurvivorSetupEui()
    {
        _window = new SurvivorSetupWindow();
        _window.OnVote += party => SendMessage(new SurvivorVoteMsg(party));
        _window.OnClaim += slot => SendMessage(new SurvivorClaimSlotMsg(slot));
        _window.OnCharacter += slot => SendMessage(new SurvivorPickCharacterMsg(slot));
    }

    public override void Opened()
    {
        base.Opened();
        _window.OpenCentered();
    }

    public override void Closed()
    {
        base.Closed();
        _window.Close();
    }

    public override void HandleState(EuiStateBase state)
    {
        if (state is SurvivorSetupEuiState cast)
            _window.UpdateState(cast);
    }
}