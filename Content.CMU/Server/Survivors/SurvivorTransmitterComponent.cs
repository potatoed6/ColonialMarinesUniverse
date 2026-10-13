using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.CMU14.Survivors.Transmitter;

/// <summary>
///     A survivor-built transmitter that alerts GOVFOR to the survivors' location.
/// </summary>
[RegisterComponent, AutoGenerateComponentPause]
[Access(typeof(SurvivorTransmitterSystem))]
public sealed partial class SurvivorTransmitterComponent : Component
{
    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromMinutes(2);

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextTransmitAt;

    /// <summary>Marine faction that receives the announcement.</summary>
    [DataField]
    public string Faction = "govfor";

    /// <summary>Cell charge used per call when the grid does not power the transmitter.</summary>
    [DataField]
    public float BatteryCost = 100f;

    [DataField]
    public SoundSpecifier? Sound;
}