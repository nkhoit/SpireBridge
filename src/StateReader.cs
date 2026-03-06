using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Reads game state and serializes it to JSON for WebSocket clients.
/// </summary>
public static class StateReader
{
    public static string GetFullState()
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return CommandHandler.Ok("get_state", new
            {
                screen = "main_menu",
                in_run = false
            });
        }

        var player = LocalContext.GetMe(runState);
        var state = new Dictionary<string, object?>
        {
            ["screen"] = GetCurrentScreen(runState),
            ["in_run"] = true,
            ["act"] = runState.CurrentActIndex + 1,
            ["floor"] = runState.TotalFloor,
            ["act_floor"] = runState.ActFloor,
            ["is_game_over"] = runState.IsGameOver
        };

        // Player info
        if (player != null)
        {
            state["player"] = BuildPlayerInfo(player);
        }

        // Combat state
        if (CombatManager.Instance.IsInProgress)
        {
            state["combat"] = BuildCombatState(player);
        }

        // Map info
        state["map"] = BuildMapInfo(runState);

        return CommandHandler.Ok("get_state", state);
    }

    private static string GetCurrentScreen(RunState runState)
    {
        if (CombatManager.Instance.IsInProgress)
            return "combat";
        if (runState.IsGameOver)
            return "game_over";
        // Rough detection — can be refined
        return "map";
    }

    private static Dictionary<string, object?> BuildPlayerInfo(Player player)
    {
        var creature = player.Creature;
        var info = new Dictionary<string, object?>
        {
            ["character"] = player.Character?.GetType().Name,
            ["hp"] = creature.CurrentHp,
            ["max_hp"] = creature.MaxHp,
            ["gold"] = player.Gold,
            ["max_energy"] = player.MaxEnergy,
            ["deck"] = player.Deck.Cards.Select(SerializeCard).ToList(),
            ["relics"] = player.Relics.Select(r => new Dictionary<string, object?>
            {
                ["id"] = r.Id.Entry,
                ["name"] = r.Id.Entry // Title requires localization context
            }).ToList(),
            ["potions"] = player.PotionSlots.Select((p, i) => p == null ? null : new Dictionary<string, object?>
            {
                ["slot"] = i,
                ["id"] = p.Id.Entry,
                ["can_use"] = CombatManager.Instance.IsInProgress,
                ["target_type"] = p.TargetType.ToString()
            }).ToList()
        };

        // Combat-specific player state
        if (player.PlayerCombatState != null)
        {
            info["energy"] = player.PlayerCombatState.Energy;
            info["block"] = creature.Block;
            info["powers"] = creature.Powers.Select(SerializePower).ToList();
            info["hand"] = player.PlayerCombatState.Hand.Cards.Select(SerializeCard).ToList();
            info["draw_pile_count"] = player.PlayerCombatState.DrawPile.Cards.Count;
            info["discard_pile_count"] = player.PlayerCombatState.DiscardPile.Cards.Count;
            info["exhaust_pile_count"] = player.PlayerCombatState.ExhaustPile.Cards.Count;
            info["orb_slots"] = player.PlayerCombatState.OrbQueue?.ToString();
        }

        return info;
    }

    private static Dictionary<string, object?> BuildCombatState(Player? player)
    {
        var cm = CombatManager.Instance;
        var combat = new Dictionary<string, object?>
        {
            ["is_player_turn"] = cm.IsPlayPhase
        };

        // Enemies
        if (player?.PlayerCombatState != null)
        {
            var combatState = player.Creature.CombatState;
            if (combatState != null)
            {
                combat["enemies"] = combatState.Enemies
                    .Where(e => e.IsAlive)
                    .Select((e, i) => SerializeEnemy(e, i, combatState))
                    .ToList();
            }
        }

        return combat;
    }

    private static Dictionary<string, object?> SerializeEnemy(Creature enemy, int index, CombatState combatState)
    {
        var info = new Dictionary<string, object?>
        {
            ["index"] = index,
            ["id"] = enemy.ModelId.Entry,
            ["name"] = enemy.Name,
            ["hp"] = enemy.CurrentHp,
            ["max_hp"] = enemy.MaxHp,
            ["block"] = enemy.Block,
            ["is_hittable"] = enemy.IsHittable,
            ["powers"] = enemy.Powers.Select(SerializePower).ToList()
        };

        // Intents
        if (enemy.Monster?.NextMove != null)
        {
            info["intents"] = enemy.Monster.NextMove.Intents.Select(intent =>
            {
                var intentInfo = new Dictionary<string, object?>
                {
                    ["type"] = intent.IntentType.ToString()
                };
                if (intent is AttackIntent attack)
                {
                    try
                    {
                        var targets = combatState.Allies;
                        intentInfo["damage"] = attack.GetSingleDamage(targets, enemy);
                        intentInfo["hits"] = attack.Repeats;
                    }
                    catch { /* damage calc may fail outside combat context */ }
                }
                return intentInfo;
            }).ToList();
        }

        return info;
    }

    private static Dictionary<string, object?> BuildMapInfo(RunState runState)
    {
        var mapInfo = new Dictionary<string, object?>
        {
            ["current_coord"] = runState.CurrentMapCoord.HasValue
                ? new { row = runState.CurrentMapCoord.Value.row, col = runState.CurrentMapCoord.Value.col }
                : null,
            ["visited_count"] = runState.VisitedMapCoords.Count
        };

        // Available next nodes
        try
        {
            var map = runState.Map;
            if (runState.VisitedMapCoords.Count == 0)
            {
                // First move — show row 0
                var startPoints = map.GetPointsInRow(0).ToList();
                mapInfo["available_nodes"] = startPoints.Select(SerializeMapPoint).ToList();
            }
            else
            {
                var currentCoord = runState.VisitedMapCoords[^1];
                var currentPoint = map.GetPoint(currentCoord);
                if (currentPoint != null)
                {
                    mapInfo["available_nodes"] = currentPoint.Children.Select(SerializeMapPoint).ToList();
                }
            }
        }
        catch { /* map may not be available */ }

        return mapInfo;
    }

    private static Dictionary<string, object?> SerializeMapPoint(MapPoint point)
    {
        return new Dictionary<string, object?>
        {
            ["row"] = point.coord.row,
            ["col"] = point.coord.col,
            ["type"] = point.PointType.ToString()
        };
    }

    private static Dictionary<string, object?> SerializeCard(CardModel card)
    {
        var info = new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["type"] = card.Type.ToString(),
            ["cost"] = card.EnergyCost.GetResolved(),
            ["target_type"] = card.TargetType.ToString()
        };

        // Only show playability in combat
        try
        {
            info["can_play"] = card.CanPlay();
        }
        catch { info["can_play"] = null; }

        return info;
    }

    private static Dictionary<string, object?> SerializePower(MegaCrit.Sts2.Core.Models.PowerModel power)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = power.Id.Entry,
            ["amount"] = power.Amount
        };
    }
}
