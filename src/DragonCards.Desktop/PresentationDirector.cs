using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using DragonCards.Core;

[assembly: InternalsVisibleTo("DragonCards.Tests")]

namespace DragonCards.Desktop;

internal enum PresentationPriority
{
    Secondary,
    Standard,
    Primary
}

internal enum PresentationMotion
{
    None,
    Fade,
    StaticHighlight,
    ResourcePulse,
    CardTravel,
    SourceReveal,
    TargetHighlight,
    CombatLunge,
    Impact
}

internal sealed record AnimationRecipe(
    float AnticipationSeconds,
    float MotionSeconds,
    float SettleSeconds,
    float MinimumCaptionSeconds,
    PresentationPriority Priority,
    bool BlocksInput,
    PresentationMotion Motion,
    string Caption,
    bool AddToTimeline,
    IReadOnlyList<string> SoundCues)
{
    public float AnimationDurationSeconds => AnticipationSeconds + MotionSeconds + SettleSeconds;
    public float DurationSeconds => Math.Max(AnimationDurationSeconds, MinimumCaptionSeconds);

    public AnimationRecipe ForReducedMotion() => this with
    {
        AnticipationSeconds = 0.04f,
        MotionSeconds = 0f,
        SettleSeconds = PresentationDirector.ReducedMotionHighlightSeconds,
        BlocksInput = false,
        Motion = PresentationMotion.StaticHighlight
    };

    public AnimationRecipe WithAnimationSpeed(float speedMultiplier)
    {
        var speed = Math.Clamp(speedMultiplier, 0.6f, 1.6f);
        return this with
        {
            AnticipationSeconds = AnticipationSeconds / speed,
            MotionSeconds = MotionSeconds / speed,
            SettleSeconds = SettleSeconds / speed
        };
    }
}

internal static class AnimationRecipes
{
    private static readonly IReadOnlyDictionary<MatchEventKind, AnimationRecipe> Recipes =
        new ReadOnlyDictionary<MatchEventKind, AnimationRecipe>(
            new Dictionary<MatchEventKind, AnimationRecipe>
            {
                [MatchEventKind.PhaseChanged] = Recipe(0.72f, 1.40f, PresentationPriority.Standard, false, PresentationMotion.Fade, "Phase", false, SoundKeys.Phase),
                [MatchEventKind.CardDrawn] = Recipe(0.72f, 1.45f, PresentationPriority.Primary, true, PresentationMotion.CardTravel, "Draw", true, SoundKeys.CardDraw),
                [MatchEventKind.CardPlayed] = Recipe(0.82f, 1.60f, PresentationPriority.Primary, true, PresentationMotion.CardTravel, "Play", true, SoundKeys.CardPlay),
                [MatchEventKind.CardDiscarded] = Recipe(0.68f, 1.45f, PresentationPriority.Primary, true, PresentationMotion.CardTravel, "Discard", true, SoundKeys.CardDiscard),
                [MatchEventKind.CardSacrificed] = Recipe(0.90f, 1.65f, PresentationPriority.Primary, true, PresentationMotion.CardTravel, "Sacrifice", true, SoundKeys.Sacrifice),
                [MatchEventKind.EnergySpent] = Recipe(0.48f, 1.15f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Energy spent", false, SoundKeys.EnergySpend),
                [MatchEventKind.EnergyGained] = Recipe(0.48f, 1.15f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Energy gained", false, SoundKeys.EnergyGain),
                [MatchEventKind.EnergySourceCreated] = Recipe(0.62f, 1.35f, PresentationPriority.Standard, false, PresentationMotion.CardTravel, "Energy source", true, SoundKeys.EnergyGain),
                [MatchEventKind.EnergyRefreshed] = Recipe(0.44f, 1.10f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Energy refresh", false, SoundKeys.CardReady),
                [MatchEventKind.EnergyConverted] = Recipe(0.52f, 1.20f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Energy converted", false, SoundKeys.EnergyGain),
                [MatchEventKind.EnergyRefunded] = Recipe(0.48f, 1.15f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Energy refunded", false, SoundKeys.EnergyGain),
                [MatchEventKind.CostReduced] = Recipe(0.46f, 1.15f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Cost reduced", false, SoundKeys.CostReduce),
                [MatchEventKind.AbilityActivated] = Recipe(0.76f, 1.65f, PresentationPriority.Primary, true, PresentationMotion.SourceReveal, "Ability", true, SoundKeys.Ability),
                [MatchEventKind.TargetChoiceQueued] = Recipe(0.36f, 1.50f, PresentationPriority.Standard, false, PresentationMotion.StaticHighlight, "Choose target", false, SoundKeys.TargetPrompt),
                [MatchEventKind.TargetResolved] = Recipe(0.64f, 1.45f, PresentationPriority.Primary, true, PresentationMotion.TargetHighlight, "Target", true, SoundKeys.TargetResolve),
                [MatchEventKind.AttackDeclared] = Recipe(1.00f, 1.75f, PresentationPriority.Primary, true, PresentationMotion.CombatLunge, "Attack", true, SoundKeys.Attack),
                [MatchEventKind.BlockDeclared] = Recipe(0.86f, 1.60f, PresentationPriority.Primary, true, PresentationMotion.CombatLunge, "Block", true, SoundKeys.Block),
                [MatchEventKind.CombatActionQueued] = Recipe(0.52f, 1.35f, PresentationPriority.Standard, false, PresentationMotion.SourceReveal, "Combat action", false, SoundKeys.CombatWindow),
                [MatchEventKind.CombatActionPassed] = Recipe(0.46f, 1.20f, PresentationPriority.Standard, false, PresentationMotion.Fade, "Pass", true, SoundKeys.CombatWindow),
                [MatchEventKind.CombatResolved] = Recipe(1.04f, 1.80f, PresentationPriority.Primary, true, PresentationMotion.Impact, "Resolve", true, SoundKeys.CombatResolve),
                [MatchEventKind.DamageTaken] = Recipe(0.86f, 1.65f, PresentationPriority.Primary, true, PresentationMotion.Impact, "Damage", true, SoundKeys.Damage),
                [MatchEventKind.CardReadied] = Recipe(0.44f, 1.10f, PresentationPriority.Secondary, false, PresentationMotion.ResourcePulse, "Ready", false, SoundKeys.CardReady),
                [MatchEventKind.CardReturnedToHand] = Recipe(0.78f, 1.55f, PresentationPriority.Primary, true, PresentationMotion.CardTravel, "Return", true, SoundKeys.CardReturn)
            });

    public static IReadOnlyDictionary<MatchEventKind, AnimationRecipe> All => Recipes;

    public static AnimationRecipe For(MatchEventKind kind) => Recipes.TryGetValue(kind, out var recipe)
        ? recipe
        : throw new ArgumentOutOfRangeException(nameof(kind), kind, "No animation recipe is registered for this match event kind.");

    private static AnimationRecipe Recipe(
        float animationSeconds,
        float minimumCaptionSeconds,
        PresentationPriority priority,
        bool blocksInput,
        PresentationMotion motion,
        string caption,
        bool addToTimeline,
        params string[] soundCues)
    {
        var anticipationRatio = motion is PresentationMotion.CardTravel or PresentationMotion.CombatLunge or PresentationMotion.SourceReveal ? 0.18f : 0.12f;
        var settleRatio = motion is PresentationMotion.Impact or PresentationMotion.CombatLunge ? 0.30f : 0.24f;
        var anticipation = animationSeconds * anticipationRatio;
        var settle = animationSeconds * settleRatio;
        return new AnimationRecipe(
            anticipation,
            Math.Max(0f, animationSeconds - anticipation - settle),
            settle,
            minimumCaptionSeconds,
            priority,
            blocksInput,
            motion,
            caption,
            addToTimeline,
            Array.AsReadOnly(soundCues));
    }
}

internal static class PresentationPacing
{
    public static int MigrateLegacyAnimationSpeed(int legacyPercent) => legacyPercent switch
    {
        <= 55 => 75,
        <= 70 => 100,
        <= 100 => 125,
        _ => 150
    };

    public static int MigrateLegacyMessageDuration(int legacyPercent) => legacyPercent switch
    {
        <= 55 => 140,
        <= 70 => 100,
        <= 100 => 75,
        _ => 55
    };
}

internal sealed class PresentationBeat
{
    internal PresentationBeat(
        int actionId,
        MatchEvent matchEvent,
        IReadOnlyList<MatchEvent> sourceEvents,
        AnimationRecipe recipe,
        float captionDuration)
    {
        ActionId = actionId;
        Event = matchEvent;
        SourceEvents = sourceEvents;
        Recipe = recipe;
        CaptionDuration = Math.Max(0.45f, captionDuration);
    }

    public int ActionId { get; }
    public MatchEvent Event { get; }
    public IReadOnlyList<MatchEvent> SourceEvents { get; }
    public AnimationRecipe Recipe { get; }
    public float CaptionDuration { get; }
    public float AnimationDuration => Recipe.AnimationDurationSeconds;
    public float Duration => Math.Max(AnimationDuration, CaptionDuration);
    public bool Blocking => Recipe.BlocksInput;
    public float Elapsed { get; internal set; }
    public float Progress => Duration <= 0f ? 1f : Math.Clamp(Elapsed / Duration, 0f, 1f);
    public float AnimationProgress => AnimationDuration <= 0f ? 1f : Math.Clamp(Elapsed / AnimationDuration, 0f, 1f);
    public float MotionProgress => Recipe.MotionSeconds <= 0f
        ? 1f
        : Math.Clamp((Elapsed - Recipe.AnticipationSeconds) / Recipe.MotionSeconds, 0f, 1f);
    public float SettleProgress => Recipe.SettleSeconds <= 0f
        ? 1f
        : Math.Clamp((Elapsed - Recipe.AnticipationSeconds - Recipe.MotionSeconds) / Recipe.SettleSeconds, 0f, 1f);
    public bool IsBlocking => Recipe.BlocksInput && Elapsed < AnimationDuration;
    public float Opacity => FadeEnvelope(Elapsed, Duration, 0.16f, 0.22f);
    public float CaptionOpacity => FadeEnvelope(Elapsed, CaptionDuration, 0.16f, 0.22f);
    public bool IsComplete => Progress >= 1f;

    private static float FadeEnvelope(float elapsed, float duration, float fadeIn, float fadeOut)
    {
        if (duration <= 0f)
        {
            return 0f;
        }

        var enter = Math.Clamp(elapsed / Math.Min(fadeIn, duration * 0.5f), 0f, 1f);
        var exit = Math.Clamp((duration - elapsed) / Math.Min(fadeOut, duration * 0.5f), 0f, 1f);
        return Math.Min(enter, exit);
    }
}

internal readonly record struct PresentationActivation(
    int ActionId,
    MatchEvent Event,
    AnimationRecipe Recipe,
    int SourceEventCount);

internal static class PresentationVisibility
{
    public static bool SuppressesDestination(
        IEnumerable<PresentationBeat> beats,
        ZoneRef zone,
        string instanceId,
        bool reducedMotion)
    {
        if (reducedMotion)
        {
            return false;
        }

        return beats.Any(beat =>
            beat.Recipe.Motion == PresentationMotion.CardTravel &&
            beat.MotionProgress < 0.94f &&
            beat.SourceEvents.Any(source =>
                source.To is { } destination &&
                destination.PlayerIndex == zone.PlayerIndex &&
                destination.Zone.Equals(zone.Zone, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(source.InstanceId)
                    ? destination.Index == zone.Index
                    : source.InstanceId.Equals(instanceId, StringComparison.OrdinalIgnoreCase))));
    }
}

/// <summary>
/// Converts each engine action's event batch into a short presentation group. Primary beats run in
/// order, resource/readiness beats run in parallel, and activation cues are exposed exactly when a
/// beat starts so audio can stay synchronized with the visual.
/// </summary>
internal sealed class PresentationDirector
{
    public const float MinimumSkipDelaySeconds = 0.35f;
    public const float ReducedMotionHighlightSeconds = 0.24f;

    private readonly Queue<ActionGroup> _pendingGroups = [];
    private readonly Queue<PresentationActivation> _activations = [];
    private ActionGroup? _activeGroup;
    private int _nextActionId = 1;

    public bool ReducedMotion { get; set; }
    /// <summary>
    /// Scales motion independently from narration. Values below one slow movement; values above
    /// one speed it up.
    /// </summary>
    public float AnimationSpeedMultiplier { get; set; } = 1f;
    /// <summary>Scales how long action narration remains visible without changing movement.</summary>
    public float MessageDurationMultiplier { get; set; } = 1f;
    public PresentationBeat? Active => _activeGroup?.SequenceBeat ?? _activeGroup?.ParallelBeats.FirstOrDefault();
    public bool IsBlocking => _activeGroup?.CurrentBeats.Any(beat => beat.IsBlocking) == true;
    public bool CanSkip => _activeGroup is not null && _activeGroup.Elapsed >= MinimumSkipDelaySeconds;
    public int PendingActionCount => _pendingGroups.Count + (_activeGroup is null ? 0 : 1);
    public bool HasPendingActivations => _activations.Count > 0;
    public IReadOnlyList<PresentationBeat> ActiveBeats => _activeGroup?.CurrentBeats ?? Array.Empty<PresentationBeat>();

    public void Enqueue(IEnumerable<MatchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        var snapshots = events.Select(Snapshot).ToArray();
        if (snapshots.Length == 0)
        {
            return;
        }

        var actionId = _nextActionId++;
        var beats = CreateBeats(actionId, snapshots);
        if (beats.Count == 0)
        {
            return;
        }

        _pendingGroups.Enqueue(new ActionGroup(actionId, beats));
        EnsureActiveGroup();
    }

    public void Update(float elapsedSeconds)
    {
        var remaining = Math.Max(0f, elapsedSeconds);
        EnsureActiveGroup();

        while (_activeGroup is not null && remaining > 0f)
        {
            var group = _activeGroup;
            var untilTransition = group.SecondsUntilTransition;
            var step = Math.Min(remaining, untilTransition);

            if (step > 0f)
            {
                group.Advance(step);
                remaining -= step;
            }

            var transitioned = group.CompleteFinishedBeats(Activate);
            if (group.IsComplete)
            {
                _activeGroup = null;
                EnsureActiveGroup();
                continue;
            }

            if (!transitioned || step <= 0f)
            {
                break;
            }
        }
    }

    public bool SkipActive()
    {
        if (!CanSkip)
        {
            return false;
        }

        _activeGroup = null;
        EnsureActiveGroup();
        return true;
    }

    public void Clear()
    {
        _pendingGroups.Clear();
        _activations.Clear();
        _activeGroup = null;
    }

    public IReadOnlyList<PresentationActivation> DrainActivations()
    {
        if (_activations.Count == 0)
        {
            return Array.Empty<PresentationActivation>();
        }

        var result = _activations.ToArray();
        _activations.Clear();
        return result;
    }

    public void PrimeForCapture(MatchEvent matchEvent, float progress = 0.55f)
    {
        ArgumentNullException.ThrowIfNull(matchEvent);
        Clear();

        var actionId = _nextActionId++;
        var snapshot = Snapshot(matchEvent);
        var recipe = EffectiveRecipe(snapshot.Kind);
        var beat = new PresentationBeat(actionId, snapshot, Array.AsReadOnly([snapshot]), recipe, CaptionDuration(snapshot, recipe))
        {
            Elapsed = 0f
        };
        beat.Elapsed = beat.Duration * Math.Clamp(progress, 0f, 1f);

        _activeGroup = ActionGroup.ForCapture(actionId, beat);
    }

    private void EnsureActiveGroup()
    {
        while (_activeGroup is null && _pendingGroups.Count > 0)
        {
            _activeGroup = _pendingGroups.Dequeue();
            _activeGroup.Start(Activate);
            if (_activeGroup.IsComplete)
            {
                _activeGroup = null;
            }
        }
    }

    private void Activate(PresentationBeat beat) => _activations.Enqueue(new PresentationActivation(
        beat.ActionId,
        beat.Event,
        beat.Recipe,
        beat.SourceEvents.Count));

    private IReadOnlyList<PresentationBeat> CreateBeats(int actionId, IReadOnlyList<MatchEvent> events)
    {
        var buckets = Coalesce(RemoveRedundantMovementEvents(events));
        return buckets
            .Select(bucket =>
            {
                var merged = Merge(bucket);
                var recipe = EffectiveRecipe(merged.Kind);
                return new PresentationBeat(
                    actionId,
                    merged,
                    Array.AsReadOnly(bucket.ToArray()),
                    recipe,
                    CaptionDuration(merged, recipe));
            })
            .ToArray();
    }

    private static IReadOnlyList<MatchEvent> RemoveRedundantMovementEvents(IReadOnlyList<MatchEvent> events)
    {
        var result = new List<MatchEvent>(events.Count);
        var playedDestinations = new Dictionary<string, ZoneRef?>(StringComparer.OrdinalIgnoreCase);

        foreach (var matchEvent in events)
        {
            if (matchEvent.Kind == MatchEventKind.CardPlayed && !string.IsNullOrWhiteSpace(matchEvent.InstanceId))
            {
                playedDestinations[matchEvent.InstanceId] = matchEvent.To;
            }

            var isResolvedSpellMarker = matchEvent.Kind == MatchEventKind.CardDiscarded &&
                matchEvent.From is null &&
                !string.IsNullOrWhiteSpace(matchEvent.InstanceId) &&
                playedDestinations.TryGetValue(matchEvent.InstanceId, out var playDestination) &&
                playDestination is not null &&
                playDestination == matchEvent.To;
            if (!isResolvedSpellMarker)
            {
                result.Add(matchEvent);
            }
        }

        return result;
    }

    private AnimationRecipe EffectiveRecipe(MatchEventKind kind)
    {
        var recipe = AnimationRecipes.For(kind);
        return (ReducedMotion ? recipe.ForReducedMotion() : recipe)
            .WithAnimationSpeed(AnimationSpeedMultiplier);
    }

    private float CaptionDuration(MatchEvent matchEvent, AnimationRecipe recipe)
    {
        var text = string.IsNullOrWhiteSpace(matchEvent.Message) ? recipe.Caption : matchEvent.Message;
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var naturalReadingTime = Math.Clamp(0.9f + words / 6f, recipe.MinimumCaptionSeconds, 3.2f);
        return naturalReadingTime * Math.Clamp(MessageDurationMultiplier, 0.55f, 1.5f);
    }

    private static IReadOnlyList<List<MatchEvent>> Coalesce(IReadOnlyList<MatchEvent> events)
    {
        var buckets = new List<List<MatchEvent>>(events.Count);
        var coalescedIndexes = new Dictionary<CoalesceKey, int>();

        foreach (var matchEvent in events)
        {
            if (!CanCoalesce(matchEvent.Kind))
            {
                buckets.Add([matchEvent]);
                continue;
            }

            var key = new CoalesceKey(matchEvent.Kind, matchEvent.PlayerIndex, matchEvent.Element);
            if (coalescedIndexes.TryGetValue(key, out var index))
            {
                buckets[index].Add(matchEvent);
            }
            else
            {
                coalescedIndexes[key] = buckets.Count;
                buckets.Add([matchEvent]);
            }
        }

        return buckets;
    }

    private static bool CanCoalesce(MatchEventKind kind) => kind is
        MatchEventKind.CardDrawn or
        MatchEventKind.EnergySpent or
        MatchEventKind.EnergyGained or
        MatchEventKind.EnergyConverted or
        MatchEventKind.EnergyRefunded or
        MatchEventKind.DamageTaken;

    private static MatchEvent Merge(IReadOnlyList<MatchEvent> events)
    {
        var first = events[0];
        if (events.Count == 1)
        {
            return first;
        }

        var amount = events.Sum(matchEvent => matchEvent.Amount > 0 ? matchEvent.Amount : 1);
        var last = events[^1];
        return first with
        {
            CardId = "",
            InstanceId = "",
            Amount = amount,
            To = last.To ?? first.To,
            Message = CoalescedMessage(first, events.Count, amount)
        };
    }

    private static string CoalescedMessage(MatchEvent first, int eventCount, int amount) => first.Kind switch
    {
        MatchEventKind.CardDrawn => $"Drew {eventCount} cards.",
        MatchEventKind.DamageTaken => $"Took {amount} damage.",
        MatchEventKind.EnergySpent => $"{amount} {first.Element} energy spent.",
        MatchEventKind.EnergyGained => $"{amount} {first.Element} energy gained.",
        MatchEventKind.EnergyConverted => $"{amount} energy converted to {first.Element}.",
        MatchEventKind.EnergyRefunded => $"{amount} {first.Element} energy refunded.",
        _ => first.Message
    };

    private static MatchEvent Snapshot(MatchEvent matchEvent) => matchEvent with { };

    private readonly record struct CoalesceKey(MatchEventKind Kind, int PlayerIndex, string Element);

    private sealed class ActionGroup
    {
        private readonly Queue<PresentationBeat> _sequence;

        public ActionGroup(int actionId, IEnumerable<PresentationBeat> beats)
        {
            ActionId = actionId;
            var materialized = beats.ToArray();
            ParallelBeats = materialized
                .Where(beat => beat.Recipe.Priority == PresentationPriority.Secondary)
                .ToList();
            _sequence = new Queue<PresentationBeat>(materialized
                .Where(beat => beat.Recipe.Priority != PresentationPriority.Secondary));
        }

        public int ActionId { get; }
        public float Elapsed { get; private set; }
        public PresentationBeat? SequenceBeat { get; private set; }
        public List<PresentationBeat> ParallelBeats { get; }
        public IReadOnlyList<PresentationBeat> CurrentBeats
        {
            get
            {
                if (SequenceBeat is null)
                {
                    return ParallelBeats;
                }

                if (ParallelBeats.Count == 0)
                {
                    return [SequenceBeat];
                }

                var beats = new List<PresentationBeat>(ParallelBeats.Count + 1) { SequenceBeat };
                beats.AddRange(ParallelBeats);
                return beats;
            }
        }

        public bool IsComplete => SequenceBeat is null && _sequence.Count == 0 && ParallelBeats.Count == 0;
        public float SecondsUntilTransition => CurrentBeats.Count == 0
            ? 0f
            : CurrentBeats.Min(beat => Math.Max(0f, beat.Duration - beat.Elapsed));

        public static ActionGroup ForCapture(int actionId, PresentationBeat beat)
        {
            var group = new ActionGroup(actionId, [beat]);
            if (beat.Recipe.Priority == PresentationPriority.Secondary)
            {
                return group;
            }

            group.SequenceBeat = group._sequence.Dequeue();
            return group;
        }

        public void Start(Action<PresentationBeat> onActivated)
        {
            foreach (var beat in ParallelBeats)
            {
                onActivated(beat);
            }

            ActivateNextSequence(onActivated);
        }

        public void Advance(float elapsedSeconds)
        {
            Elapsed += elapsedSeconds;
            if (SequenceBeat is not null)
            {
                SequenceBeat.Elapsed += elapsedSeconds;
            }

            foreach (var beat in ParallelBeats)
            {
                beat.Elapsed += elapsedSeconds;
            }
        }

        public bool CompleteFinishedBeats(Action<PresentationBeat> onActivated)
        {
            var transitioned = ParallelBeats.RemoveAll(beat => beat.IsComplete) > 0;
            if (SequenceBeat?.IsComplete == true)
            {
                SequenceBeat = null;
                ActivateNextSequence(onActivated);
                transitioned = true;
            }

            return transitioned;
        }

        private void ActivateNextSequence(Action<PresentationBeat> onActivated)
        {
            if (SequenceBeat is not null || _sequence.Count == 0)
            {
                return;
            }

            SequenceBeat = _sequence.Dequeue();
            onActivated(SequenceBeat);
        }
    }
}
