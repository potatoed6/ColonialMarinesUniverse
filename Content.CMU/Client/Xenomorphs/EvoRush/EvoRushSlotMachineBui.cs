using Content.Shared.CMU14.Xenonids.EvoRush;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.CMU14.Xenonids.EvoRush;

[UsedImplicitly]
public sealed class EvoRushSlotMachineBui(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private EvoRushSlotMachineWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<EvoRushSlotMachineWindow>();
        _window.OnDecision += accept => SendMessage(new EvoRushSlotMachineDecisionMsg(accept));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is EvoRushSlotMachineBuiState cast)
            _window?.SetState(cast);
    }
}