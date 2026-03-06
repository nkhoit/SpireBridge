using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;

namespace SpireBridge;

/// <summary>
/// Handles non-combat screen actions: proceed, skip, choose_card, choose_option, choose_reward.
/// </summary>
public static class ScreenActions
{
    public static string Proceed()
    {
        try
        {
            // Scope to current overlay if available
            Node searchRoot;
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay is Node overlayNode && overlayNode.IsInsideTree())
            {
                searchRoot = overlayNode;
                SpireBridgeMod.Log($"proceed: scoped to overlay {overlay.GetType().Name}");
            }
            else
            {
                searchRoot = ((SceneTree)Engine.GetMainLoop()).Root;
                SpireBridgeMod.Log("proceed: using root (no overlay)");
            }

            var proceedButtons = FindAll<NProceedButton>(searchRoot);
            SpireBridgeMod.Log($"proceed: found {proceedButtons.Count} buttons");
            var enabledButton = proceedButtons.FirstOrDefault(b => b.IsVisibleInTree() && b.IsEnabled);
            if (enabledButton != null)
            {
                SpireBridgeMod.Log($"proceed: clicking {enabledButton.Name}");
                enabledButton.ForceClick();
                return CommandHandler.Ok("proceed", new { clicked = "proceed_button" });
            }
            return CommandHandler.Error("no_button", "No enabled proceed button found");
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("proceed_error", ex.Message);
        }
    }

    public static string Skip()
    {
        try
        {
            var root = ((SceneTree)Engine.GetMainLoop()).Root;
            // Look for skip/bowl button on card reward screen
            var skipButtons = FindAll<NButton>(root)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled &&
                    (b.Name.ToString().Contains("Skip", StringComparison.OrdinalIgnoreCase) ||
                     b.Name.ToString().Contains("Bowl", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (skipButtons.Count > 0)
            {
                skipButtons[0].ForceClick();
                return CommandHandler.Ok("skip", new { clicked = skipButtons[0].Name.ToString() });
            }

            // Fallback: try the card reward alternative button (singing bowl / skip)
            var altButtons = FindAll<NCardRewardAlternativeButton>(root)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled)
                .ToList();
            if (altButtons.Count > 0)
            {
                altButtons[0].ForceClick();
                return CommandHandler.Ok("skip", new { clicked = "alternative_button" });
            }

            return CommandHandler.Error("no_button", "No skip button found");
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("skip_error", ex.Message);
        }
    }

    public static string ChooseCard(JsonElement request)
    {
        try
        {
            if (!request.TryGetProperty("index", out var idxEl))
                return CommandHandler.Error("missing_param", "choose_card requires 'index'");

            int index = idxEl.GetInt32();

            // Scope to current overlay screen
            Node searchRoot;
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay is Node overlayNode && overlayNode.IsInsideTree())
            {
                searchRoot = overlayNode;
                SpireBridgeMod.Log($"choose_card: overlay type = {overlay.GetType().Name}");
            }
            else
            {
                searchRoot = ((SceneTree)Engine.GetMainLoop()).Root;
                SpireBridgeMod.Log("choose_card: no overlay, using root");
            }

            // Try NGridCardHolder specifically (used in selection screens)
            var gridHolders = FindAll<NGridCardHolder>(searchRoot)
                .Where(h => h.IsVisibleInTree())
                .ToList();

            SpireBridgeMod.Log($"choose_card: found {gridHolders.Count} grid holders");

            if (gridHolders.Count > 0)
            {
                if (index < 0 || index >= gridHolders.Count)
                    return CommandHandler.Error("invalid_index", $"Card index {index} out of range (available: {gridHolders.Count})");

                var holder = gridHolders[index];
                SpireBridgeMod.Log($"choose_card: clicking grid holder {index}, card={holder.CardModel?.Id}");
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                return CommandHandler.Ok("choose_card", new { index, count = gridHolders.Count, card = holder.CardModel?.Id?.ToString() });
            }

            // Fallback: any NCardHolder
            var cardHolders = FindAll<NCardHolder>(searchRoot)
                .Where(h => h.IsVisibleInTree())
                .ToList();

            SpireBridgeMod.Log($"choose_card: found {cardHolders.Count} card holders");

            if (index < 0 || index >= cardHolders.Count)
                return CommandHandler.Error("invalid_index", $"Card index {index} out of range (available: {cardHolders.Count})");

            var h2 = cardHolders[index];
            h2.EmitSignal(NCardHolder.SignalName.Pressed, h2);
            return CommandHandler.Ok("choose_card", new { index, count = cardHolders.Count });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("choose_card_error", ex.Message);
        }
    }

    public static string ChooseReward(JsonElement request)
    {
        try
        {
            if (!request.TryGetProperty("index", out var idxEl))
                return CommandHandler.Error("missing_param", "choose_reward requires 'index'");

            int index = idxEl.GetInt32();
            var root = ((SceneTree)Engine.GetMainLoop()).Root;

            // Find reward buttons
            var rewardButtons = FindAll<NRewardButton>(root)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled)
                .ToList();

            if (index < 0 || index >= rewardButtons.Count)
                return CommandHandler.Error("invalid_index", $"Reward index {index} out of range (available: {rewardButtons.Count})");

            rewardButtons[index].ForceClick();
            var rewardType = rewardButtons[index].Reward?.GetType().Name ?? "unknown";
            return CommandHandler.Ok("choose_reward", new { index, reward_type = rewardType });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("choose_reward_error", ex.Message);
        }
    }

    public static string ChooseOption(JsonElement request)
    {
        try
        {
            if (!request.TryGetProperty("index", out var idxEl))
                return CommandHandler.Error("missing_param", "choose_option requires 'index'");

            int index = idxEl.GetInt32();
            var root = ((SceneTree)Engine.GetMainLoop()).Root;

            // Events use NEventOptionButton in the EventRoom
            var eventRoom = root.GetNodeOrNull("/root/Game/RootSceneContainer/Run/RoomContainer/EventRoom");
            if (eventRoom != null)
            {
                var eventButtons = FindAll<NEventOptionButton>(eventRoom)
                    .Where(b => b.IsVisibleInTree() && b.IsEnabled && !b.Option.IsLocked)
                    .ToList();

                if (eventButtons.Count > 0)
                {
                    if (index < 0 || index >= eventButtons.Count)
                        return CommandHandler.Error("invalid_index", $"Option index {index} out of range (available: {eventButtons.Count})");

                    var btn = eventButtons[index];
                    btn.ForceClick();
                    var isProceed = btn.Option?.IsProceed ?? false;
                    return CommandHandler.Ok("choose_option", new { index, is_proceed = isProceed, event_id = btn.Event?.Id?.Entry });
                }
            }

            // Fallback: generic button search
            var buttons = FindAll<NButton>(root)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled &&
                    (b.Name.ToString().Contains("Option") || b.Name.ToString().Contains("Choice")))
                .ToList();

            if (index < 0 || index >= buttons.Count)
                return CommandHandler.Error("invalid_index", $"Option index {index} out of range (available: {buttons.Count})");

            buttons[index].ForceClick();
            return CommandHandler.Ok("choose_option", new { index, button = buttons[index].Name.ToString() });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("choose_option_error", ex.Message);
        }
    }

    /// <summary>Recursively find all nodes of type T.</summary>
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
}
