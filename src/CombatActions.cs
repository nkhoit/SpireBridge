using System;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
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
            if (request.TryGetProperty("target_index", out var targetEl))
            {
                int targetIndex = targetEl.GetInt32();
                var combatState = player.Creature.CombatState;
                if (combatState != null)
                {
                    var enemies = combatState.HittableEnemies;
                    if (card.TargetType == TargetType.AnyEnemy && targetIndex >= 0 && targetIndex < enemies.Count)
                    {
                        target = enemies[targetIndex];
                    }
                    else if (card.TargetType == TargetType.AnyAlly)
                    {
                        var allies = combatState.Allies.Where(c => c.IsAlive && c.IsPlayer && c != player.Creature).ToList();
                        if (targetIndex >= 0 && targetIndex < allies.Count)
                            target = allies[targetIndex];
                    }
                }
            }
            else if (card.TargetType == TargetType.AnyEnemy)
            {
                // Default to first hittable enemy
                var combatState = player.Creature.CombatState;
                target = combatState?.HittableEnemies.FirstOrDefault();
            }

            if (target == null && card.TargetType == TargetType.AnyEnemy)
                return CommandHandler.Error("no_target", "No valid target for targeted card");
        }

        // Play the card
        _ = CardCmd.AutoPlay(new BlockingPlayerChoiceContext(), card, target);

        return CommandHandler.Ok("play", new
        {
            card = card.Id.Entry,
            target = target?.Name
        });
    }

    public static string EndTurn()
    {
        if (!CombatManager.Instance.IsInProgress)
            return CommandHandler.Error("not_in_combat", "No combat in progress");

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
}
