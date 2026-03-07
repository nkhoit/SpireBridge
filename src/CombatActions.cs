using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Helpers;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Handles combat actions: play card, end turn, use potion.
/// All methods run on the main Godot thread.
/// </summary>
public static class CombatActions
{
    public static string PlayCard(JsonElement request)
    {
        if (!CombatManager.Instance.IsInProgress)
            return CommandHandler.Error("not_in_combat", "No combat in progress");
        if (!CombatManager.Instance.IsPlayPhase)
            return CommandHandler.Error("not_play_phase", "Not the player's turn");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return CommandHandler.Error("no_run", "No run in progress");

        var player = LocalContext.GetMe(runState);
        if (player?.PlayerCombatState == null)
            return CommandHandler.Error("no_player", "Player not found");

        // Get card index from request (accept both "card" and "card_index")
        if (!request.TryGetProperty("card_index", out var cardIdxEl) && !request.TryGetProperty("card", out cardIdxEl))
            return CommandHandler.Error("missing_param", "play requires 'card' or 'card_index'");

        int cardIndex = cardIdxEl.GetInt32();
        var hand = player.PlayerCombatState.Hand.Cards;
        if (cardIndex < 0 || cardIndex >= hand.Count)
            return CommandHandler.Error("invalid_index", $"card_index {cardIndex} out of range (hand size: {hand.Count})");

        var card = hand[cardIndex];

        if (!card.CanPlay())
            return CommandHandler.Error("unplayable", $"Card '{card.Id.Entry}' cannot be played");

        // Resolve target
        Creature? target = null;
        if (card.TargetType == TargetType.AnyEnemy || card.TargetType == TargetType.AnyAlly)
        {
            // Accept both "target" and "target_index"
            if (!request.TryGetProperty("target", out var targetEl))
                request.TryGetProperty("target_index", out targetEl);

            var combatState = player.Creature.CombatState;
            if (targetEl.ValueKind == JsonValueKind.Number)
            {
                int targetIndex = targetEl.GetInt32();
                if (combatState != null)
                {
                    if (card.TargetType == TargetType.AnyEnemy)
                    {
                        var enemies = combatState.HittableEnemies;
                        if (targetIndex < 0 || targetIndex >= enemies.Count)
                            return CommandHandler.Error("invalid_index", $"target {targetIndex} out of range (enemies: {enemies.Count})");
                        target = enemies[targetIndex];
                    }
                    else if (card.TargetType == TargetType.AnyAlly)
                    {
                        var allies = combatState.Allies.Where(c => c.IsAlive && c.IsPlayer && c != player.Creature).ToList();
                        if (targetIndex < 0 || targetIndex >= allies.Count)
                            return CommandHandler.Error("invalid_index", $"target {targetIndex} out of range (allies: {allies.Count})");
                        target = allies[targetIndex];
                    }
                }
            }
            else if (card.TargetType == TargetType.AnyEnemy)
            {
                // Default to first hittable enemy
                target = combatState?.HittableEnemies.FirstOrDefault();
            }

            if (target == null && card.TargetType == TargetType.AnyEnemy)
                return CommandHandler.Error("no_target", "No valid target for targeted card");
        }

        // Spend energy/resources first (AutoPlay skips this — it's designed for AutoSlayer which uses cheats)
        // Then play the card via TaskHelper.RunSafely
        TaskHelper.RunSafely(PlayCardWithResources(card, target));

        return CommandHandler.Ok("play", new
        {
            card = card.Id.Entry,
            target = target?.Name
        });
    }

    private static async Task PlayCardWithResources(CardModel card, Creature? target)
    {
        var (energySpent, starsSpent) = await card.SpendResources();
        var resources = new ResourceInfo
        {
            EnergySpent = energySpent,
            EnergyValue = energySpent,
            StarsSpent = starsSpent,
            StarValue = starsSpent
        };
        var choiceContext = new BlockingPlayerChoiceContext();
        await card.OnPlayWrapper(choiceContext, target, isAutoPlay: false, resources);
    }

    public static string EndTurn()
    {
        if (!CombatManager.Instance.IsInProgress)
            return CommandHandler.Error("not_in_combat", "No combat in progress");
        if (!CombatManager.Instance.IsPlayPhase)
            return CommandHandler.Error("not_play_phase", "Not the player's turn");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return CommandHandler.Error("no_run", "No run in progress");

        var player = LocalContext.GetMe(runState);
        if (player == null)
            return CommandHandler.Error("no_player", "Player not found");

        PlayerCmd.EndTurn(player, canBackOut: false);
        return CommandHandler.Ok("end_turn");
    }

    public static string UsePotion(JsonElement request)
    {
        if (!CombatManager.Instance.IsInProgress)
            return CommandHandler.Error("not_in_combat", "No combat in progress");

        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return CommandHandler.Error("no_run", "No run in progress");

        var player = LocalContext.GetMe(runState);
        if (player == null)
            return CommandHandler.Error("no_player", "Player not found");

        if (!request.TryGetProperty("potion_index", out var potionIdxEl) && !request.TryGetProperty("potion", out potionIdxEl))
            return CommandHandler.Error("missing_param", "use_potion requires 'potion' or 'potion_index'");

        int potionIndex = potionIdxEl.GetInt32();
        var slots = player.PotionSlots;
        if (potionIndex < 0 || potionIndex >= slots.Count)
            return CommandHandler.Error("invalid_index", $"potion_index {potionIndex} out of range");

        var potion = slots[potionIndex];
        if (potion == null)
            return CommandHandler.Error("empty_slot", $"No potion in slot {potionIndex}");

        // Resolve target for targeted potions
        Creature? target = null;
        if (potion.TargetType == TargetType.AnyEnemy)
        {
            var combatState = player.Creature.CombatState;
            if (request.TryGetProperty("target_index", out var targetEl) && combatState != null)
            {
                int targetIndex = targetEl.GetInt32();
                var enemies = combatState.HittableEnemies;
                if (targetIndex >= 0 && targetIndex < enemies.Count)
                    target = enemies[targetIndex];
            }
            else
            {
                target = combatState?.HittableEnemies.FirstOrDefault();
            }
        }

        potion.EnqueueManualUse(target);
        return CommandHandler.Ok("use_potion", new { potion = potion.Id.Entry });
    }

    public static string DiscardPotion(JsonElement request)
    {
        var runState = RunManager.Instance?.DebugOnlyGetState();
        if (runState == null)
            return CommandHandler.Error("no_run", "No active run");

        var player = LocalContext.GetMe(runState);
        if (player == null)
            return CommandHandler.Error("no_player", "No active player");

        if (!request.TryGetProperty("potion_index", out var potionIdxEl))
            return CommandHandler.Error("missing_param", "discard_potion requires 'potion_index'");

        int potionIndex = potionIdxEl.GetInt32();
        if (potionIndex < 0 || potionIndex >= player.PotionSlots.Count)
            return CommandHandler.Error("invalid_index", $"potion_index {potionIndex} out of range");

        var potion = player.PotionSlots[potionIndex];
        if (potion == null)
            return CommandHandler.Error("empty_slot", $"Potion slot {potionIndex} is empty");

        player.DiscardPotionInternal(potion);
        return CommandHandler.Ok("discard_potion", new { potion_index = potionIndex, potion = potion.Id.Entry });
    }
}
