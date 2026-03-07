using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Rooms;

namespace SpireBridge;

/// <summary>
/// Reads game state and serializes it to JSON for WebSocket clients.
/// </summary>
public static class StateReader
{
    private static readonly Regex BbCodeRegex = new(@"\[/?[a-zA-Z_][^\]]*\]", RegexOptions.Compiled);
    
    public static string StripBBCode(string text)
    {
        return BbCodeRegex.Replace(text, "");
    }

    private static string SerializeCardDescription(CardModel card)
    {
        try
        {
            var desc = card.Description;
            if (desc == null) return "";
            card.DynamicVars.AddTo(desc);
            return StripBBCode(desc.GetFormattedText());
        }
        catch { return ""; }
    }

    public static string GetFullState()
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
        {
            return CommandHandler.Ok("get_state", new
            {
                screen = "main_menu",
                in_run = false,
                available_actions = new List<Dictionary<string, object?>>
                {
                    new() { ["action"] = "start_run", ["description"] = "Start a new run" }
                }
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

        // Event info
        if (state["screen"]?.ToString() == "event")
        {
            state["event"] = BuildEventInfo();
        }

        // Rewards info
        if (state["screen"]?.ToString() == "rewards")
        {
            state["rewards"] = BuildRewardsInfo();
        }

        // Rest site info
        if (state["screen"]?.ToString() == "rest_site")
        {
            state["rest_site"] = BuildRestSiteInfo();
        }

        // Shop info
        if (state["screen"]?.ToString() == "shop")
        {
            state["shop"] = BuildShopState();
        }

        // Card reward choices
        var screenStr = state["screen"]?.ToString() ?? "";
        if (screenStr == "card_reward" || screenStr == "card_select" || screenStr.Contains("select") || screenStr.Contains("transform"))
        {
            state["card_choices"] = BuildCardChoices();
        }

        // Sequence number for staleness detection
        state["seq"] = GameEventBridge.CurrentSeq;

        // Available actions for the agent
        state["available_actions"] = BuildAvailableActions(state);

        return CommandHandler.Ok("get_state", state);
    }

    private static string GetCurrentScreen(RunState runState)
    {
        if (runState.IsGameOver)
            return "game_over";
        if (CombatManager.Instance.IsInProgress)
            return "combat";

        // Check room types BEFORE map (rooms are visible while map reports IsOpen)
        try
        {
            var nRun = NRun.Instance;
            if (nRun != null)
            {
                var restRoom = nRun.RestSiteRoom;
                if (restRoom != null && ((Control)restRoom).IsVisibleInTree())
                {
                    // Always check for card select overlay first (Smith opens it async)
                    var restOverlay = NOverlayStack.Instance?.Peek();
                    if (restOverlay != null && restOverlay is CanvasItem restOv && restOv.IsInsideTree() && restOv.IsVisibleInTree())
                    {
                        var restOvType = restOverlay.GetType().Name;
                        if (restOvType.Contains("Select") || restOvType.Contains("Upgrade") || restOvType.Contains("Card"))
                        {
                            // Fall through to overlay check below (card_select)
                        }
                        else
                        {
                            // Non-card overlay on rest site — still rest_site
                            var proceedBtn2 = restRoom.ProceedButton;
                            if (proceedBtn2 != null && proceedBtn2.IsEnabled)
                            {
                                if (NMapScreen.Instance?.IsOpen != true)
                                    return "map";
                            }
                            else
                            {
                                return "rest_site";
                            }
                        }
                    }
                    else
                    {
                        // No overlay — check proceed button
                        var proceedBtn = restRoom.ProceedButton;
                        if (proceedBtn == null || !proceedBtn.IsEnabled)
                        {
                            return "rest_site";
                        }
                        // Proceed enabled, rest complete
                        if (NMapScreen.Instance?.IsOpen != true)
                            return "map";
                    }
                }
                if (nRun.MerchantRoom != null && ((Control)nRun.MerchantRoom).IsVisibleInTree())
                {
                    if (NMapScreen.Instance?.IsOpen != true)
                    {
                        // Check if an overlay (card removal select) is on top
                        var shopOverlay = NOverlayStack.Instance?.Peek();
                        if (shopOverlay != null && shopOverlay is CanvasItem shopOv && shopOv.IsInsideTree() && shopOv.IsVisibleInTree())
                        {
                            var shopOvType = shopOverlay.GetType().Name;
                            if (shopOvType.Contains("Select") || shopOvType.Contains("Card"))
                            {
                                // Fall through to overlay check below
                            }
                            else
                            {
                                return "shop";
                            }
                        }
                        else
                        {
                            return "shop";
                        }
                    }
                }
                if (nRun.TreasureRoom != null && ((Control)nRun.TreasureRoom).IsVisibleInTree())
                {
                    if (NMapScreen.Instance?.IsOpen != true)
                        return "treasure";
                }

                // Event room — NRun.EventRoom is non-null only when _roomContainer.CurrentScene is NEventRoom
                if (nRun.EventRoom != null && ((Control)nRun.EventRoom).IsVisibleInTree())
                {
                    if (NMapScreen.Instance?.IsOpen != true)
                    {
                        // Check if a card select overlay is on top (e.g. transform/remove from event)
                        try
                        {
                            var evOverlay = NOverlayStack.Instance?.Peek();
                            if (evOverlay != null && evOverlay is CanvasItem evOv && evOv.IsInsideTree() && evOv.IsVisibleInTree())
                            {
                                var evOvType = evOverlay.GetType().Name;
                                if (evOvType.Contains("Select") || evOvType.Contains("Upgrade") || evOvType.Contains("Card") || evOvType.Contains("Transform"))
                                {
                                    // Fall through to overlay detection below
                                }
                                else
                                {
                                    return "event";
                                }
                            }
                            else
                            {
                                return "event";
                            }
                        }
                        catch { return "event"; }
                    }
                }
            }
        }
        catch { }

        // Check if map screen is open FIRST — it takes priority even over overlays
        // (rewards overlay stays in tree during close animation, but map is already open)
        try
        {
            if (NMapScreen.Instance?.IsOpen == true)
            {
                // Check if there's a meaningful overlay on top (card selection from reward)
                try
                {
                    var overlay = NOverlayStack.Instance?.Peek();
                    if (overlay != null && overlay is CanvasItem ov && ov.IsInsideTree() && ov.IsVisibleInTree())
                    {
                        var typeName = overlay.GetType().Name;
                        // Only card reward/selection overlays take priority over map
                        if (typeName.Contains("CardSelection") || typeName.Contains("CardSelect") || typeName.Contains("SelectScreen") || typeName.Contains("Enchant") || typeName.Contains("Transform"))
                            return "card_select";
                        if (typeName.Contains("Reward") && typeName.Contains("Card"))
                            return "card_reward";
                    }
                }
                catch { }
                return "map";
            }
        }
        catch { }

        // Check overlay stack (overlays sit on top of events/map)
        try
        {
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay != null && overlay is CanvasItem overlayNode && overlayNode.IsInsideTree() && overlayNode.IsVisibleInTree())
            {
                var typeName = overlay.GetType().Name;
                if (typeName.Contains("Reward") && typeName.Contains("Card"))
                    return "card_reward";
                if (typeName.Contains("Reward"))
                    return "rewards";
                if (typeName.Contains("Shop"))
                    return "shop";
                if (typeName.Contains("Event"))
                    return "event";
                if (typeName.Contains("RestSite") || typeName.Contains("Campfire"))
                    return "rest_site";
                if (typeName.Contains("CardSelection") || typeName.Contains("CardSelect") || typeName.Contains("SelectScreen") || typeName.Contains("Enchant") || typeName.Contains("Transform"))
                    return "card_select";
                if (typeName.Contains("GameOver"))
                    return "game_over";
                // Log unknown overlay for debugging
                SpireBridgeMod.Log($"Unknown overlay: {typeName}");
                return typeName.ToLowerInvariant();
            }
        }
        catch { /* overlay stack may not exist */ }

        // Nothing matched — check if we're in a transition
        // If a run is in progress and we're between screens, return "map" as safe default
        try
        {
            if (NRun.Instance != null && RunManager.Instance?.DebugOnlyGetState() != null)
                return "map";
        }
        catch { }
        
        return "unknown";
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
            ["relics"] = player.Relics.Select(r => {
                var info = new Dictionary<string, object?> { ["id"] = r.Id.Entry };
                try { info["name"] = StripBBCode(r.Title?.GetFormattedText() ?? r.Id.Entry); } catch { info["name"] = r.Id.Entry; }
                try { info["description"] = StripBBCode(r.DynamicDescription?.GetFormattedText() ?? ""); } catch { info["description"] = ""; }
                return info;
            }).ToList(),
            ["potions"] = player.PotionSlots.Select((p, i) => p == null ? null : new Dictionary<string, object?>
            {
                ["slot"] = i,
                ["id"] = p.Id.Entry,
                ["name"] = StripBBCode(p.Title?.GetFormattedText() ?? p.Id.Entry),
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
            info["draw_pile"] = player.PlayerCombatState.DrawPile.Cards.Select(SerializeCard).ToList();
            info["discard_pile_count"] = player.PlayerCombatState.DiscardPile.Cards.Count;
            info["discard_pile"] = player.PlayerCombatState.DiscardPile.Cards.Select(SerializeCard).ToList();
            info["exhaust_pile_count"] = player.PlayerCombatState.ExhaustPile.Cards.Count;
            info["exhaust_pile"] = player.PlayerCombatState.ExhaustPile.Cards.Select(SerializeCard).ToList();
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

        // Enemies — try multiple access paths
        try
        {
            IEnumerable<Creature>? enemies = null;
            CombatState? combatState = null;

            // Path 1: player.Creature.CombatState.Enemies
            if (player?.PlayerCombatState != null)
            {
                combatState = player.Creature.CombatState;
                if (combatState != null)
                {
                    enemies = combatState.Enemies;
                    SpireBridgeMod.Log($"BuildCombatState: Path 1 (player.Creature.CombatState) — {enemies?.Count() ?? 0} enemies");
                }
            }

            // Path 2: CombatManager enemies via combat state
            if (enemies == null || !enemies.Any())
            {
                try
                {
                    var cmState = cm.GetType().GetProperty("CombatState")?.GetValue(cm) as CombatState;
                    if (cmState != null)
                    {
                        combatState = cmState;
                        enemies = cmState.Enemies;
                        SpireBridgeMod.Log($"BuildCombatState: Path 2 (CombatManager.CombatState) — {enemies?.Count() ?? 0} enemies");
                    }
                }
                catch (Exception ex2) { SpireBridgeMod.Log($"BuildCombatState: Path 2 failed: {ex2.Message}"); }
            }

            // Path 3: Try Allies property to confirm CombatState access works
            if (enemies == null || !enemies.Any())
            {
                try
                {
                    if (combatState != null)
                    {
                        // Log what properties CombatState has for debugging
                        var props = combatState.GetType().GetProperties().Select(p => p.Name).ToList();
                        SpireBridgeMod.Log($"BuildCombatState: CombatState properties: {string.Join(", ", props)}");
                    }
                }
                catch (Exception ex3) { SpireBridgeMod.Log($"BuildCombatState: Path 3 failed: {ex3.Message}"); }
            }

            if (enemies != null && combatState != null)
            {
                combat["enemies"] = enemies
                    .Where(e => e.IsAlive)
                    .Select((e, i) => SerializeEnemy(e, i, combatState))
                    .ToList();
            }
            else
            {
                SpireBridgeMod.Log("BuildCombatState: No enemies found via any path");
                combat["enemies"] = new List<Dictionary<string, object?>>();
            }
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"BuildCombatState error: {ex.Message}\n{ex.StackTrace}");
            combat["enemies"] = new List<Dictionary<string, object?>>();
            combat["error"] = ex.Message;
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

    private static List<Dictionary<string, object?>> BuildRewardsInfo()
    {
        var rewards = new List<Dictionary<string, object?>>();
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            var buttons = FindAll<MegaCrit.Sts2.Core.Nodes.Rewards.NRewardButton>(root)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled)
                .ToList();
            foreach (var btn in buttons)
            {
                var r = new Dictionary<string, object?>
                {
                    ["index"] = rewards.Count,
                    ["type"] = btn.Reward?.GetType().Name ?? "unknown",
                    ["attempted"] = ScreenActions.IsRewardAttempted(btn),
                };
                // Try to get details
                try
                {
                    if (btn.Reward is MegaCrit.Sts2.Core.Rewards.GoldReward gold)
                        r["gold"] = gold.Amount;
                    else if (btn.Reward is MegaCrit.Sts2.Core.Rewards.PotionReward potion && potion.Potion != null)
                    {
                        r["potion_id"] = potion.Potion.Id.Entry;
                        r["name"] = StripBBCode(potion.Potion.Title?.GetFormattedText() ?? potion.Potion.Id.Entry);
                    }
                    else if (btn.Reward is MegaCrit.Sts2.Core.Rewards.RelicReward relicReward)
                    {
                        try { r["name"] = StripBBCode(relicReward.Description?.GetFormattedText() ?? "Relic"); } catch { }
                    }
                }
                catch { }
                rewards.Add(r);
            }
        }
        catch { }
        return rewards;
    }

    private static Dictionary<string, object?> BuildEventInfo()
    {
        var info = new Dictionary<string, object?>();
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var eventRoom = tree.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
            if (eventRoom == null) return info;

            var buttons = FindAll<MegaCrit.Sts2.Core.Nodes.Events.NEventOptionButton>(eventRoom)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled)
                .ToList();

            var options = new List<Dictionary<string, object?>>();
            foreach (var btn in buttons)
            {
                var opt = new Dictionary<string, object?>
                {
                    ["index"] = options.Count,
                    ["locked"] = btn.Option?.IsLocked ?? false,
                    ["is_proceed"] = btn.Option?.IsProceed ?? false,
                };
                // Try to get label text
                try { opt["text"] = StripBBCode(btn.Option?.Description?.GetFormattedText() ?? btn.Option?.TextKey ?? ""); } catch { opt["text"] = ""; }
                try { opt["event_id"] = btn.Event?.Id?.Entry; } catch { }
                options.Add(opt);
            }
            info["options"] = options;
            info["option_count"] = options.Count;
        }
        catch (Exception ex)
        {
            info["error"] = ex.Message;
        }
        return info;
    }

    private static List<T> FindAll<T>(Node root) where T : Node
    {
        var results = new List<T>();
        FindAllRecursive(root, results);
        return results;
    }

    private static void FindAllRecursive<T>(Node node, List<T> results) where T : Node
    {
        if (node is T t) results.Add(t);
        foreach (var child in node.GetChildren())
            FindAllRecursive(child, results);
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

    private static Dictionary<string, object?> BuildRestSiteInfo()
    {
        var info = new Dictionary<string, object?>();
        var options = new List<Dictionary<string, object?>>();

        try
        {
            var restRoom = NRun.Instance?.RestSiteRoom;
            if (restRoom != null)
            {
                var choicesContainer = ((Node)restRoom).GetNodeOrNull<Control>("%ChoicesContainer");
                if (choicesContainer != null)
                {
                    int idx = 0;
                    foreach (var child in choicesContainer.GetChildren())
                    {
                        if (child is NRestSiteButton btn)
                        {
                            options.Add(new Dictionary<string, object?>
                            {
                                ["index"] = idx,
                                ["id"] = btn.Option.OptionId,
                                ["name"] = StripBBCode(btn.Option.Title.GetFormattedText()),
                                ["description"] = StripBBCode(btn.Option.Description.GetFormattedText())
                            });
                            idx++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) { SpireBridgeMod.Log($"BuildRestSiteInfo error: {ex.Message}"); }

        info["options"] = options;
        return info;
    }

    private static Dictionary<string, object?> BuildShopState()
    {
        var shop = new Dictionary<string, object?>();
        try
        {
            var nMerchantRoom = NRun.Instance?.MerchantRoom;
            if (nMerchantRoom == null) return shop;

            var inventory = nMerchantRoom.Room?.Inventory;
            if (inventory == null) return shop;

            var items = new List<Dictionary<string, object?>>();
            int idx = 0;

            // Character cards
            foreach (var entry in inventory.CharacterCardEntries)
            {
                if (!entry.IsStocked) { idx++; continue; }
                var card = entry.CreationResult?.Card;
                if (card == null) { idx++; continue; }
                items.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["type"] = "card",
                    ["category"] = "character",
                    ["id"] = card.Id.Entry,
                    ["name"] = StripBBCode(card.Title),
                    ["description"] = SerializeCardDescription(card),
                    ["cost"] = entry.Cost,
                    ["affordable"] = entry.EnoughGold,
                    ["rarity"] = card.Rarity.ToString(),
                    ["card_type"] = card.Type.ToString(),
                });
                idx++;
            }

            // Colorless cards
            foreach (var entry in inventory.ColorlessCardEntries)
            {
                if (!entry.IsStocked) { idx++; continue; }
                var card = entry.CreationResult?.Card;
                if (card == null) { idx++; continue; }
                items.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["type"] = "card",
                    ["category"] = "colorless",
                    ["id"] = card.Id.Entry,
                    ["name"] = StripBBCode(card.Title),
                    ["cost"] = entry.Cost,
                    ["affordable"] = entry.EnoughGold,
                    ["rarity"] = card.Rarity.ToString(),
                });
                idx++;
            }

            // Relics
            foreach (var entry in inventory.RelicEntries)
            {
                if (!entry.IsStocked) { idx++; continue; }
                var relic = entry.Model;
                if (relic == null) { idx++; continue; }
                items.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["type"] = "relic",
                    ["id"] = relic.Id.Entry,
                    ["name"] = StripBBCode(relic.Title?.GetFormattedText() ?? relic.Id.Entry),
                    ["description"] = StripBBCode(relic.DynamicDescription?.GetFormattedText() ?? ""),
                    ["cost"] = entry.Cost,
                    ["affordable"] = entry.EnoughGold,
                });
                idx++;
            }

            // Potions
            foreach (var entry in inventory.PotionEntries)
            {
                if (!entry.IsStocked) { idx++; continue; }
                var potion = entry.Model;
                if (potion == null) { idx++; continue; }
                items.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["type"] = "potion",
                    ["id"] = potion.Id.Entry,
                    ["name"] = StripBBCode(potion.Title?.GetFormattedText() ?? potion.Id.Entry),
                    ["cost"] = entry.Cost,
                    ["affordable"] = entry.EnoughGold,
                });
                idx++;
            }

            // Card removal
            if (inventory.CardRemovalEntry != null)
            {
                items.Add(new Dictionary<string, object?>
                {
                    ["index"] = idx,
                    ["type"] = "card_removal",
                    ["id"] = "CARD_REMOVAL",
                    ["name"] = "Card Removal",
                    ["description"] = "Remove a card from your deck",
                    ["cost"] = inventory.CardRemovalEntry.Cost,
                    ["affordable"] = inventory.CardRemovalEntry.EnoughGold,
                });
            }

            shop["items"] = items;
            shop["gold"] = inventory.Player?.Gold ?? 0;
        }
        catch (Exception ex) { SpireBridgeMod.Log($"BuildShopState error: {ex.Message}"); }
        return shop;
    }

    private static List<Dictionary<string, object?>> BuildCardChoices()
    {
        var choices = new List<Dictionary<string, object?>>();
        try
        {
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay == null) return choices;

            var holders = UiHelper.FindAll<NGridCardHolder>((Node)overlay);
            int idx = 0;
            foreach (var holder in holders)
            {
                if (holder.CardModel != null)
                {
                    var card = SerializeCard(holder.CardModel);
                    card["index"] = idx++;
                    choices.Add(card);
                }
            }
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"Error building card choices: {ex.Message}");
        }
        return choices;
    }

    private static Dictionary<string, object?> SerializeCard(CardModel card)
    {
        var info = new Dictionary<string, object?>
        {
            ["id"] = card.Id.Entry,
            ["name"] = card.Title,
            ["type"] = card.Type.ToString(),
            ["cost"] = card.EnergyCost.GetResolved(),
            ["is_x_cost"] = card.EnergyCost.CostsX,
            ["target_type"] = card.TargetType.ToString()
        };

        // Only show playability in combat
        try { info["can_play"] = card.CanPlay(); }
        catch { info["can_play"] = null; }

        // Description (resolve dynamic vars like damage/block values)
        try 
        { 
            var desc = card.Description;
            card.DynamicVars.AddTo(desc);
            info["description"] = StripBBCode(desc.GetFormattedText()); 
        }
        catch { info["description"] = null; }

        // Damage
        try { info["damage"] = card.DynamicVars.ContainsKey("Damage") ? (int)card.DynamicVars.Damage.BaseValue : null; }
        catch { info["damage"] = null; }

        // Block
        try { info["block"] = card.DynamicVars.ContainsKey("Block") ? (int)card.DynamicVars.Block.BaseValue : null; }
        catch { info["block"] = null; }

        // Rarity
        try { info["rarity"] = card.Rarity.ToString(); }
        catch { info["rarity"] = null; }

        // Upgraded
        try { info["upgraded"] = card.IsUpgraded; }
        catch { info["upgraded"] = null; }

        // Keywords
        try
        {
            var kw = card.Keywords.Where(k => k != MegaCrit.Sts2.Core.Entities.Cards.CardKeyword.None).Select(k => k.ToString()).ToList();
            if (kw.Count > 0) info["keywords"] = kw;
        }
        catch { }

        return info;
    }

    private static Dictionary<string, object?> SerializePower(MegaCrit.Sts2.Core.Models.PowerModel power)
    {
        var info = new Dictionary<string, object?>
        {
            ["id"] = power.Id.Entry,
            ["amount"] = power.Amount
        };

        try { info["name"] = StripBBCode(power.Title.GetFormattedText()); }
        catch { info["name"] = null; }

        try { info["type"] = power.Type.ToString(); }
        catch { info["type"] = null; }

        return info;
    }

    private static List<Dictionary<string, object?>> BuildAvailableActions(Dictionary<string, object?> state)
    {
        var actions = new List<Dictionary<string, object?>>();
        var screen = state["screen"]?.ToString() ?? "";

        try
        {
            switch (screen)
            {
                case "combat":
                    BuildCombatActions(state, actions);
                    break;
                case "map":
                    BuildMapActions(state, actions);
                    break;
                case "rewards":
                    BuildRewardActions(state, actions);
                    break;
                case "card_reward":
                    BuildCardRewardActions(state, actions);
                    break;
                case "card_select":
                    BuildCardSelectActions(state, actions);
                    break;
                case "event":
                    BuildEventActions(state, actions);
                    break;
                case "rest_site":
                    BuildRestSiteActions(state, actions);
                    break;
                case "game_over":
                    actions.Add(new Dictionary<string, object?> { ["action"] = "proceed", ["description"] = "Advance past game over / timeline screens" });
                    actions.Add(new Dictionary<string, object?> { ["action"] = "start_run", ["description"] = "Start a new run (auto-dismisses post-run screens)" });
                    break;
                case "main_menu":
                    actions.Add(new Dictionary<string, object?> { ["action"] = "start_run", ["description"] = "Start a new run" });
                    break;
                case "shop":
                    try
                    {
                        var shopRoom = NRun.Instance?.MerchantRoom;
                        var shopInventory = shopRoom?.Room?.Inventory;
                        if (shopInventory != null)
                        {
                            int shopIdx = 0;
                            foreach (var entry in shopInventory.AllEntries)
                            {
                                if (entry.IsStocked)
                                {
                                    string itemName = "item";
                                    if (entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardEntry ce && ce.CreationResult?.Card != null)
                                        itemName = ce.CreationResult.Card.Title;
                                    else if (entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantRelicEntry re && re.Model != null)
                                        try { itemName = StripBBCode(re.Model.Title?.GetFormattedText() ?? re.Model.Id.Entry); } catch { itemName = re.Model.Id.Entry; }
                                    else if (entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantPotionEntry pe && pe.Model != null)
                                        try { itemName = StripBBCode(pe.Model.Title?.GetFormattedText() ?? pe.Model.Id.Entry); } catch { itemName = pe.Model.Id.Entry; }
                                    else if (entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry)
                                        itemName = "Card Removal";

                                    actions.Add(new Dictionary<string, object?>
                                    {
                                        ["action"] = "shop_buy",
                                        ["index"] = shopIdx,
                                        ["cost"] = entry.Cost,
                                        ["affordable"] = entry.EnoughGold,
                                        ["description"] = $"Buy {itemName} for {entry.Cost} gold"
                                    });
                                }
                                shopIdx++;
                            }
                        }
                    }
                    catch { }
                    actions.Add(new Dictionary<string, object?> { ["action"] = "proceed", ["description"] = "Leave shop" });
                    break;
                case "treasure":
                    try {
                        var tRoom = NRun.Instance?.TreasureRoom;
                        if (tRoom != null)
                        {
                            var cBtn = ((Node)tRoom).GetNodeOrNull<NButton>("%Chest");
                            if (cBtn != null && cBtn.IsEnabled)
                                actions.Add(new Dictionary<string, object?> { ["action"] = "open_chest", ["description"] = "Open the treasure chest" });
                        }
                    } catch { }
                    actions.Add(new Dictionary<string, object?> { ["action"] = "proceed", ["description"] = "Leave treasure room" });
                    break;
                case "unknown":
                    // Include debug info so agents/users can report what screen they're on
                    try
                    {
                        var debugInfo = new List<string>();
                        var tree3 = (SceneTree)Engine.GetMainLoop();
                        var roomContainer = tree3.Root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer");
                        if (roomContainer != null)
                        {
                            foreach (var child in roomContainer.GetChildren())
                            {
                                if (child is CanvasItem ci && ci.IsVisibleInTree())
                                    debugInfo.Add($"room:{child.GetType().Name}");
                            }
                        }
                        var ov = NOverlayStack.Instance?.Peek();
                        if (ov != null)
                            debugInfo.Add($"overlay:{ov.GetType().Name}");
                        if (debugInfo.Count > 0)
                            actions.Add(new Dictionary<string, object?> { ["action"] = "debug", ["visible_nodes"] = debugInfo });
                    }
                    catch { }
                    actions.Add(new Dictionary<string, object?> { ["action"] = "get_state", ["description"] = "Re-check state (screen may be transitioning)" });
                    break;
            }
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"BuildAvailableActions error: {ex.Message}");
        }

        // Discard potion available on any screen when potions exist
        try
        {
            var player2 = state["player"] as Dictionary<string, object?>;
            if (player2?["potions"] is IEnumerable<object> allPotions)
                foreach (var p in allPotions)
                    if (p is Dictionary<string, object?> pot)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "discard_potion",
                            ["potion_index"] = pot["slot"],
                            ["description"] = $"Discard {pot.GetValueOrDefault("name", pot["id"])}"
                        });
        }
        catch { }

        return actions;
    }

    private static void BuildCombatActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            var combat = state["combat"] as Dictionary<string, object?>;
            if (combat == null) return;
            var isPlayerTurn = combat["is_player_turn"] as bool? ?? false;
            if (!isPlayerTurn) return;

            var enemyIndices = new List<int>();
            try
            {
                if (combat["enemies"] is IEnumerable<object> enemyList)
                    foreach (var e in enemyList)
                        if (e is Dictionary<string, object?> ed)
                            try { 
                                var hittable = ed["is_hittable"] as bool? ?? true;
                                if (hittable)
                                    enemyIndices.Add((int)(ed["index"] ?? 0)); 
                            } catch { }
            }
            catch { }

            // Hand cards
            try
            {
                var player = state["player"] as Dictionary<string, object?>;
                if (player?["hand"] is IEnumerable<object> hand)
                {
                    int idx = 0;
                    foreach (var c in hand)
                    {
                        if (c is Dictionary<string, object?> card)
                        {
                            var canPlay = card["can_play"] as bool? ?? false;
                            if (canPlay)
                            {
                                var cardId = card["id"]?.ToString() ?? "?";
                                var targetType = card["target_type"]?.ToString() ?? "";
                                var desc = $"Play {cardId}";
                                try
                                {
                                    if (card["damage"] != null) desc += $" ({card["damage"]} dmg)";
                                    if (card["block"] != null) desc += $" ({card["block"]} block)";
                                }
                                catch { }

                                // Skip targeted attacks when no enemies are alive/hittable
                                if (targetType == "AnyEnemy" && enemyIndices.Count == 0)
                                {
                                    idx++;
                                    continue;
                                }

                                var action = new Dictionary<string, object?>
                                {
                                    ["action"] = "play",
                                    ["card"] = idx,
                                    ["description"] = desc
                                };
                                if (targetType == "AnyEnemy")
                                    action["targets"] = enemyIndices;
                                actions.Add(action);
                            }
                        }
                        idx++;
                    }
                }
            }
            catch { }

            actions.Add(new Dictionary<string, object?> { ["action"] = "end_turn", ["description"] = "End your turn" });

            // Potions
            try
            {
                var player = state["player"] as Dictionary<string, object?>;
                if (player?["potions"] is IEnumerable<object> potions)
                {
                    foreach (var p in potions)
                    {
                        if (p is Dictionary<string, object?> potion)
                        {
                            var canUse = potion["can_use"] as bool? ?? false;
                            if (canUse)
                            {
                                var action = new Dictionary<string, object?>
                                {
                                    ["action"] = "use_potion",
                                    ["potion_index"] = potion["slot"],
                                    ["description"] = $"Use potion {potion["id"]}"
                                };
                                if (potion["target_type"]?.ToString() == "AnyEnemy")
                                    action["targets"] = enemyIndices;
                                    // Agent should pass target_index when using
                                actions.Add(action);
                            }
                        }
                    }
                }
            }
            catch { }
        }
        catch { }
    }

    private static void BuildMapActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            var map = state["map"] as Dictionary<string, object?>;
            if (map?["available_nodes"] is IEnumerable<object> nodes)
                foreach (var n in nodes)
                    if (n is Dictionary<string, object?> node)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_node",
                            ["row"] = node["row"],
                            ["col"] = node["col"],
                            ["type"] = node["type"],
                            ["description"] = $"Go to {node["type"]} at row {node["row"]}, col {node["col"]}"
                        });
        }
        catch { }
    }

    private static void BuildRewardActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            if (state["rewards"] is IEnumerable<object> rewards)
                foreach (var r in rewards)
                    if (r is Dictionary<string, object?> reward)
                    {
                        var type = reward["type"]?.ToString() ?? "unknown";
                        string desc = type;
                        if (type == "GoldReward" && reward.ContainsKey("gold"))
                            desc = $"Take {reward["gold"]} Gold";
                        else if (type == "PotionReward" && reward.ContainsKey("name"))
                            desc = $"Take {reward["name"]} potion";
                        else if (type == "RelicReward" && reward.ContainsKey("name"))
                            desc = $"Take {reward["name"]} relic";
                        else if (type == "CardReward")
                            desc = "Choose a card reward";
                        
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_reward",
                            ["index"] = reward["index"],
                            ["type"] = reward["type"],
                            ["description"] = desc
                        });
                    }
            actions.Add(new Dictionary<string, object?> { ["action"] = "proceed", ["description"] = "Skip remaining rewards and proceed" });
        }
        catch { }
    }

    private static void BuildCardRewardActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            if (state["card_choices"] is IEnumerable<object> cards)
                foreach (var c in cards)
                    if (c is Dictionary<string, object?> card)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_card",
                            ["index"] = card["index"],
                            ["card_id"] = card["id"],
                            ["description"] = $"Add {card["id"]} to deck"
                        });
            actions.Add(new Dictionary<string, object?> { ["action"] = "skip", ["description"] = "Skip card reward" });
        }
        catch { }
    }

    private static void BuildCardSelectActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            if (state["card_choices"] is IEnumerable<object> cards)
                foreach (var c in cards)
                    if (c is Dictionary<string, object?> card)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_card",
                            ["index"] = card["index"],
                            ["card_id"] = card["id"],
                            ["description"] = $"Select {card.GetValueOrDefault("name", card["id"])}"
                        });
        }
        catch { }
    }

    private static void BuildEventActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            if (state["event"] is Dictionary<string, object?> evt && evt["options"] is IEnumerable<object> options)
                foreach (var o in options)
                    if (o is Dictionary<string, object?> opt)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_option",
                            ["index"] = opt["index"],
                            ["text"] = opt["text"],
                            ["description"] = $"Choose: {opt["text"]}"
                        });
        }
        catch { }
    }

    private static void BuildRestSiteActions(Dictionary<string, object?> state, List<Dictionary<string, object?>> actions)
    {
        try
        {
            if (state["rest_site"] is Dictionary<string, object?> rest && rest["options"] is IEnumerable<object> options)
                foreach (var o in options)
                    if (o is Dictionary<string, object?> opt)
                        actions.Add(new Dictionary<string, object?>
                        {
                            ["action"] = "choose_rest_option",
                            ["index"] = opt["index"],
                            ["id"] = opt["id"],
                            ["description"] = $"{opt["name"]} - {opt["description"]}"
                        });
        }
        catch { }
    }
}
