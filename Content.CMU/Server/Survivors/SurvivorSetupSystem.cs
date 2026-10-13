using System.Linq;
using Content.Server.Chat.Managers;
using Content.Server.CMU14.Ops.ThirdParty;
using Content.Server.CMU14.Round;
using Content.Server.EUI;
using Content.Server.Preferences.Managers;
using Content.Shared.CMU14.Survivors.Setup;
using Content.Shared.CMU14.Threats;
using Content.Shared.GameTicking;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Robust.Server.Player;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.CMU14.Survivors.Setup;

/// <summary>
///     Holds rolled survivors in the lobby while they vote on a survivor group and pick a role and
///     character, then hands the choices to <see cref="ThirdPartySystem"/> to spawn.
/// </summary>
public sealed class SurvivorSetupSystem : EntitySystem
{
    [Dependency] private AuRoundSystem _auRound = default!;
    [Dependency] private IChatManager _chat = default!;
    [Dependency] private EuiManager _eui = default!;
    [Dependency] private IPlayerManager _player = default!;
    [Dependency] private IServerPreferencesManager _prefs = default!;
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ThirdPartySystem _thirdParty = default!;
    [Dependency] private IGameTiming _timing = default!;

    private static readonly ProtoId<JobPrototype> LeaderJob = "AU14JobThirdPartyLeader";
    private static readonly ProtoId<JobPrototype> MemberJob = "AU14JobThirdPartyMember";
    private static readonly TimeSpan VoteDuration = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan PickDuration = TimeSpan.FromSeconds(20);

    private readonly List<ThirdPartyPrototype> _candidates = new();
    private readonly Dictionary<NetUserId, HeldSurvivor> _held = new();
    private readonly List<RoleSlot> _slots = new();
    private SurvivorSetupPhase? _phase;
    private TimeSpan _phaseEnd;
    private ThirdPartyPrototype? _winner;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<RoundRestartCleanupEvent>(OnRoundRestart);
    }

    private void OnRoundRestart(RoundRestartCleanupEvent ev)
    {
        foreach (var held in _held.Values)
        {
            held.Eui?.Close();
        }

        _held.Clear();
        _candidates.Clear();
        _slots.Clear();
        _phase = null;
        _winner = null;
    }

    public bool IsHeld(NetUserId user) => _held.ContainsKey(user);

    public void Begin(Dictionary<NetUserId, (ProtoId<JobPrototype>?, EntityUid)> assignedJobs)
    {
        if (_phase != null)
            return;

        _candidates.Clear();
        foreach (var party in _auRound.SelectedThirdParties)
        {
            if (party.RoundStart && party.AnnounceAsSurvivors)
                _candidates.Add(party);
        }

        if (_candidates.Count == 0)
            return;

        foreach (var (user, (job, _)) in assignedJobs)
        {
            if (job == LeaderJob || job == MemberJob)
                _held[user] = new HeldSurvivor { Leader = job == LeaderJob };
        }
        Log.Info($"Survivor setup: candidates={_candidates.Count}, held={_held.Count}"); // TEST

        if (_held.Count == 0)
            return;

        _thirdParty.ReserveSurvivorParties();

        if (_candidates.Count == 1)
        {
            _winner = _candidates[0];
            StartPick();
        }
        else
        {
            _phase = SurvivorSetupPhase.Vote;
            _phaseEnd = _timing.CurTime + VoteDuration;
        }

        foreach (var (user, held) in _held)
        {
            if (!_player.TryGetSessionById(user, out var session))
                continue;

            held.Eui = new SurvivorSetupEui(this);
            _eui.OpenEui(held.Eui, session);
        }
    }

    public override void Update(float frameTime)
    {
        if (_phase == null || _timing.CurTime < _phaseEnd)
            return;

        if (_phase == SurvivorSetupPhase.Vote)
        {
            _winner = ResolveWinner();
            StartPick();
            DirtyAll();
            return;
        }

        Finish();
    }

    public SurvivorSetupEuiState BuildState(NetUserId user)
    {
        var state = new SurvivorSetupEuiState
        {
            Phase = _phase ?? SurvivorSetupPhase.Pick,
            PhaseEnd = _phaseEnd,
        };

        _held.TryGetValue(user, out var me);
        state.MyVote = me?.Vote;
        state.MySlot = me?.Slot;
        state.MyCharacter = me?.Character;

        foreach (var party in _candidates)
        {
            var votes = _held.Values.Count(held => held.Vote == party.ID);
            state.Parties.Add(new SurvivorPartyOption(party.ID, party.DisplayName ?? party.ID, votes));
        }

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];
            string? claimedBy = null;
            if (slot.Owner is { } owner && _player.TryGetSessionById(owner, out var ownerSession))
                claimedBy = ownerSession.Name;

            var name = _prototypes.TryIndex<EntityPrototype>(slot.Proto, out var proto) ? proto.Name : slot.Proto;
            state.Slots.Add(new SurvivorRoleSlot(i, name, slot.Leader, claimedBy));
        }

        if (_prefs.GetPreferencesOrNull(user) is { } prefs)
        {
            foreach (var (index, profile) in prefs.Characters)
            {
                if (profile is HumanoidCharacterProfile humanoid)
                    state.Characters.Add(new SurvivorCharacterOption(index, humanoid.Name));
            }
        }

        return state;
    }

    public void Vote(NetUserId user, string partyId)
    {
        if (_phase != SurvivorSetupPhase.Vote
            || !_held.TryGetValue(user, out var held)
            || _candidates.All(party => party.ID != partyId))
        {
            return;
        }

        held.Vote = partyId;
        DirtyAll();
    }

    public void ClaimSlot(NetUserId user, int index)
    {
        if (_phase != SurvivorSetupPhase.Pick
            || !_held.TryGetValue(user, out var held)
            || index < 0
            || index >= _slots.Count
            || _slots[index].Owner is { } owner && owner != user)
        {
            return;
        }

        if (held.Slot is { } previous)
            _slots[previous].Owner = null;

        _slots[index].Owner = user;
        held.Slot = index;
        DirtyAll();
    }

    public void PickCharacter(NetUserId user, int? slot)
    {
        if (_phase != SurvivorSetupPhase.Pick || !_held.TryGetValue(user, out var held))
            return;

        if (slot is { } index && _prefs.GetPreferencesOrNull(user)?.Characters.ContainsKey(index) != true)
            return;

        held.Character = slot;
        held.Eui?.StateDirty();
    }

    private ThirdPartyPrototype ResolveWinner()
    {
        var tally = _candidates.ToDictionary(party => party, party => _held.Values.Count(held => held.Vote == party.ID));
        var best = tally.Values.Max();
        var tied = _candidates.Where(party => tally[party] == best).ToList();

        // Plurality wins; ties (and no votes) fall back to the party weights.
        var total = tied.Sum(party => Math.Max(1, party.weight));
        var roll = _random.Next(total);
        foreach (var party in tied)
        {
            roll -= Math.Max(1, party.weight);
            if (roll < 0)
                return party;
        }

        return tied[^1];
    }

    private void StartPick()
    {
        _phase = SurvivorSetupPhase.Pick;
        _phaseEnd = _timing.CurTime + PickDuration;
        _slots.Clear();

        if (_winner != null && _prototypes.TryIndex(_winner.PartySpawn, out var spawn))
        {
            foreach (var (proto, count) in spawn.LeadersToSpawn)
            {
                for (var i = 0; i < count; i++)
                {
                    _slots.Add(new RoleSlot { Proto = proto, Leader = true });
                }
            }

            foreach (var (proto, count) in spawn.GruntsToSpawn)
            {
                for (var i = 0; i < count; i++)
                {
                    _slots.Add(new RoleSlot { Proto = proto, Leader = false });
                }
            }
        }

        foreach (var (user, held) in _held)
        {
            held.Character = _prefs.GetPreferencesOrNull(user)?.SelectedCharacterIndex;
        }
    }

    private void Finish()
    {
        _phase = null;
        var winner = _winner;

        // Players who never claimed get a free slot, leaders preferring leader slots.
        var unplaced = _held.Where(pair => pair.Value.Slot == null).ToList();
        _random.Shuffle(unplaced);
        foreach (var (user, held) in unplaced)
        {
            var index = _slots.FindIndex(slot => slot.Owner == null && slot.Leader == held.Leader);
            if (index < 0)
                index = _slots.FindIndex(slot => slot.Owner == null);

            if (index < 0)
                continue;

            _slots[index].Owner = user;
            held.Slot = index;
        }

        var chosen = new List<ThirdPartySystem.SurvivorAssignment>();
        foreach (var (user, held) in _held)
        {
            held.Eui?.Close();
            held.Eui = null;

            if (held.Slot is not { } index || !_player.TryGetSessionById(user, out _))
                continue;

            var slot = _slots[index];
            chosen.Add(new ThirdPartySystem.SurvivorAssignment(user, slot.Proto, slot.Leader, ResolveProfile(user, held.Character)));
        }

        var players = _held.Keys.ToList();
        _held.Clear();

        if (winner != null && chosen.Count > 0)
        {
            try
            {
                _thirdParty.SpawnChosenSurvivors(winner, chosen);
            }
            catch (Exception ex)
            {
                Log.Error($"Survivor spawn for {winner.ID} threw: {ex}");
            }
        }

        foreach (var user in players)
        {
            if (!_player.TryGetSessionById(user, out var session) || session.AttachedEntity != null)
                continue;

            _chat.DispatchServerMessage(session, Loc.GetString("cmu-survivor-no-body-return-to-lobby"));
        }
    }

    private HumanoidCharacterProfile ResolveProfile(NetUserId user, int? character)
    {
        if (character is { } index
            && _prefs.GetPreferencesOrNull(user)?.Characters.TryGetValue(index, out var profile) == true
            && profile is HumanoidCharacterProfile humanoid)
        {
            return humanoid;
        }

        return HumanoidCharacterProfile.RandomWithSpecies(HumanoidCharacterProfile.DefaultSpecies);
    }

    private void DirtyAll()
    {
        foreach (var held in _held.Values)
        {
            held.Eui?.StateDirty();
        }
    }

    private sealed class HeldSurvivor
    {
        public bool Leader;
        public string? Vote;
        public int? Slot;
        public int? Character;
        public SurvivorSetupEui? Eui;
    }

    private sealed class RoleSlot
    {
        public string Proto = string.Empty;
        public bool Leader;
        public NetUserId? Owner;
    }
}