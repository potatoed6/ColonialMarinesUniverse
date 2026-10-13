using Content.Shared.CMU14.Xenonids.EvoRush;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client.CMU14.Xenonids.EvoRush;

[UsedImplicitly]
public sealed class EvoRushCastePriorityBui(EntityUid owner, Enum uiKey) : BoundUserInterface(owner, uiKey)
{
    private EvoRushCastePriorityWindow? _window;

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<EvoRushCastePriorityWindow>();
        _window.OnSubmit += order => SendMessage(new EvoRushCastePriorityMsg(order));
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);
        if (state is EvoRushCastePriorityBuiState cast)
            _window?.Update(cast);
    }
}