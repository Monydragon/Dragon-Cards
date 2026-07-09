namespace DragonCards.Core;

public delegate void EffectHook(EffectContext context);

public sealed record EffectContext(
    DragonDuelEngine Engine,
    MatchState State,
    int OwnerIndex,
    CardInstance Source,
    CardDefinition Card,
    ActivatedAbilityDefinition? Ability);

public interface IEffectHookRegistry
{
    IReadOnlyCollection<string> HookNames { get; }
    bool HasHook(string hookName);
    void Invoke(string hookName, EffectContext context);
}

public sealed class EffectHookRegistry : IEffectHookRegistry
{
    private readonly Dictionary<string, EffectHook> _hooks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<string> HookNames => _hooks.Keys.ToArray();

    public EffectHookRegistry Register(string hookName, EffectHook hook)
    {
        _hooks[hookName] = hook;
        return this;
    }

    public bool HasHook(string hookName) => _hooks.ContainsKey(hookName);

    public void Invoke(string hookName, EffectContext context)
    {
        if (!_hooks.TryGetValue(hookName, out var hook))
        {
            throw new InvalidOperationException($"No effect hook is registered for '{hookName}'.");
        }

        hook(context);
    }
}

public static class DefaultEffectHookRegistry
{
    public static IEffectHookRegistry Create()
    {
        return new EffectHookRegistry()
            .Register("draw_one", context => context.Engine.DrawCards(context.OwnerIndex, 1))
            .Register("draw_two", context => context.Engine.DrawCards(context.OwnerIndex, 2))
            .Register("draw_three_discard_one", context =>
            {
                context.Engine.DrawCards(context.OwnerIndex, 3);
                context.Engine.DiscardCardsFromHand(context.OwnerIndex, 1);
            })
            .Register("draw_two_discard_one", context =>
            {
                context.Engine.DrawCards(context.OwnerIndex, 2);
                context.Engine.DiscardCardsFromHand(context.OwnerIndex, 1);
            })
            .Register("discard_opponent_one", context => context.Engine.DiscardCardsFromHand(1 - context.OwnerIndex, 1))
            .Register("deal_one_damage", context => context.Engine.DealDamageToOpponent(context.OwnerIndex, 1))
            .Register("deal_one_draw_one", context =>
            {
                context.Engine.DealDamageToOpponent(context.OwnerIndex, 1);
                context.Engine.DrawCards(context.OwnerIndex, 1);
            })
            .Register("exhaust_enemy_unit_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ExhaustUnit,
                    TargetScope.EnemyUnit,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an enemy Unit to exhaust.",
                    effectText: PromptText(context));
            })
            .Register("ready_friendly_unit_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReadyUnit,
                    TargetScope.FriendlyUnit,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses a friendly Unit to ready.",
                    effectText: PromptText(context));
            })
            .Register("return_enemy_unit_to_hand_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReturnToHand,
                    TargetScope.EnemyUnit,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an enemy Unit to return to hand.",
                    effectText: PromptText(context));
            })
            .Register("return_friendly_unit_to_hand_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReturnToHand,
                    TargetScope.FriendlyUnit,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses a friendly Unit to return to hand.",
                    effectText: PromptText(context));
            })
            .Register("return_enemy_field_to_hand_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReturnToHand,
                    TargetScope.EnemyField,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an enemy field card to return to hand.",
                    TargetZoneKind.Field,
                    PromptText(context));
            })
            .Register("return_friendly_field_to_hand_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReturnToHand,
                    TargetScope.FriendlyField,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses a friendly field card to return to hand.",
                    TargetZoneKind.Field,
                    PromptText(context));
            })
            .Register("return_any_field_to_hand_choice", context =>
            {
                context.Engine.QueueTargetChoice(
                    context.OwnerIndex,
                    PendingTargetChoiceType.ReturnToHand,
                    TargetScope.AnyField,
                    context.Source,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses a field card to return to hand.",
                    TargetZoneKind.Field,
                    PromptText(context));
            })
            .Register("gain_energy_choice", context =>
            {
                context.Engine.QueueEnergyChoice(
                    context.OwnerIndex,
                    PendingEnergyChoiceType.Gain,
                    1,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an element to gain.",
                    context.Source,
                    PromptText(context));
            })
            .Register("gain_energy_choice_2", context =>
            {
                context.Engine.QueueEnergyChoice(
                    context.OwnerIndex,
                    PendingEnergyChoiceType.Gain,
                    2,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an element to gain 2 energy.",
                    context.Source,
                    PromptText(context));
            })
            .Register("gain_energy_choice_3", context =>
            {
                context.Engine.QueueEnergyChoice(
                    context.OwnerIndex,
                    PendingEnergyChoiceType.Gain,
                    3,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an element to gain 3 energy.",
                    context.Source,
                    PromptText(context));
            })
            .Register("gain_energy_source_element_1", context =>
            {
                var element = context.Card.Elements.FirstOrDefault() ?? context.State.Mode.Elements.First();
                context.Engine.GainEnergy(context.OwnerIndex, element, 1);
            })
            .Register("draw_two_gain_source_energy_1", context =>
            {
                context.Engine.DrawCards(context.OwnerIndex, 2);
                var element = context.Card.Elements.FirstOrDefault() ?? context.State.Mode.Elements.First();
                context.Engine.GainEnergy(context.OwnerIndex, element, 1);
            })
            .Register("draw_one_gain_source_energy_1", context =>
            {
                context.Engine.DrawCards(context.OwnerIndex, 1);
                var element = context.Card.Elements.FirstOrDefault() ?? context.State.Mode.Elements.First();
                context.Engine.GainEnergy(context.OwnerIndex, element, 1);
            })
            .Register("gain_energy_damage_count", context =>
            {
                var player = context.State.Players[context.OwnerIndex];
                var amount = Math.Clamp(player.DamageZone.Count, 1, 3);
                var element = context.Card.Elements.FirstOrDefault() ?? context.State.Mode.Elements.First();
                context.Engine.GainEnergy(context.OwnerIndex, element, amount);
            })
            .Register("reduce_next_card_cost_1", context => context.Engine.ReduceNextCardCost(context.OwnerIndex, 1))
            .Register("reduce_next_card_cost_2", context => context.Engine.ReduceNextCardCost(context.OwnerIndex, 2))
            .Register("reduce_next_card_cost_3", context => context.Engine.ReduceNextCardCost(context.OwnerIndex, 3))
            .Register("convert_one_energy", context =>
            {
                context.Engine.QueueEnergyChoice(
                    context.OwnerIndex,
                    PendingEnergyChoiceType.ConvertTo,
                    1,
                    $"{context.State.Players[context.OwnerIndex].Name} chooses an element to convert energy into.",
                    context.Source,
                    PromptText(context));
            })
            .Register("refund_last_payment_1", context => context.Engine.RefundLastPayment(context.OwnerIndex, 1))
            .Register("refund_last_payment_2", context => context.Engine.RefundLastPayment(context.OwnerIndex, 2))
            .Register("ready_one_energy", context => context.Engine.ReduceNextCardCost(context.OwnerIndex, 1))
            .Register("recover_one_damage", context =>
            {
                var player = context.State.Players[context.OwnerIndex];
                if (player.DamageZone.Count == 0)
                {
                    return;
                }

                var recovered = player.DamageZone[^1];
                player.DamageZone.RemoveAt(player.DamageZone.Count - 1);
                player.DiscardPile.Add(recovered);
                context.State.Log.Add($"{player.Name} recovered one damage.");
            })
            .Register("recover_one_draw_one", context =>
            {
                var player = context.State.Players[context.OwnerIndex];
                if (player.DamageZone.Count > 0)
                {
                    var recovered = player.DamageZone[^1];
                    player.DamageZone.RemoveAt(player.DamageZone.Count - 1);
                    player.DiscardPile.Add(recovered);
                    context.State.Log.Add($"{player.Name} recovered one damage.");
                }

                context.Engine.DrawCards(context.OwnerIndex, 1);
            })
            .Register("recover_one_gain_source_energy_1", context =>
            {
                var player = context.State.Players[context.OwnerIndex];
                if (player.DamageZone.Count > 0)
                {
                    var recovered = player.DamageZone[^1];
                    player.DamageZone.RemoveAt(player.DamageZone.Count - 1);
                    player.DiscardPile.Add(recovered);
                    context.State.Log.Add($"{player.Name} recovered one damage.");
                }

                var element = context.Card.Elements.FirstOrDefault() ?? context.State.Mode.Elements.First();
                context.Engine.GainEnergy(context.OwnerIndex, element, 1);
            });
    }

    private static string PromptText(EffectContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.Ability?.RulesText))
        {
            return context.Ability.RulesText;
        }

        return context.Card.RulesText;
    }
}
