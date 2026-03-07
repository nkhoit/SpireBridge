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
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;

namespace SpireBridge;

/// <summary>
/// Handles non-combat screen actions: proceed, skip, choose_card, choose_option, choose_reward.
/// </summary>
public static class ScreenActions
{
    // Track reward buttons that have already been clicked (like AutoSlayer's attemptedButtons)
    // Reset when entering a new rewards screen
    private static readonly HashSet<ulong> _attemptedRewardIds = new();
    private static int _lastRewardsSeq = -1;

    /// <summary>Reset attempted rewards tracking (called when new rewards screen opens).</summary>
    public static void ResetRewardTracking()
    {
        _attemptedRewardIds.Clear();
    }

    /// <summary>Check if a reward button has already been attempted.</summary>
    public static bool IsRewardAttempted(Godot.Node btn)
    {
        return _attemptedRewardIds.Contains(((GodotObject)btn).GetInstanceId());
    }

    /// <summary>Click any visible confirm button on the current overlay.</summary>
    public static string Confirm()
    {
        var overlay = NOverlayStack.Instance?.Peek();
        if (overlay == null)
            return CommandHandler.Error("no_overlay", "No overlay screen open");

        var node = (Node)overlay;
        // Try common confirm button paths
        string[] paths = { "%PreviewConfirm", "%Confirm", "ConfirmButton" };
        foreach (var path in paths)
        {
            var btn = node.GetNodeOrNull<NConfirmButton>(path);
            if (btn != null && btn.IsVisibleInTree() && btn.IsEnabled)
            {
                btn.ForceClick();
                SpireBridgeMod.Log($"confirm: clicked {path}");
                return CommandHandler.Ok("confirm", new { button = path });
            }
        }

        // Fallback: find any NConfirmButton
        var buttons = FindAll<NConfirmButton>(node).Where(b => b.IsVisibleInTree() && b.IsEnabled).ToList();
        if (buttons.Count > 0)
        {
            buttons[0].ForceClick();
            SpireBridgeMod.Log($"confirm: clicked fallback NConfirmButton");
            return CommandHandler.Ok("confirm", new { button = "fallback" });
        }

        return CommandHandler.Error("no_confirm", "No enabled confirm button found");
    }

    /// <summary>Choose a rest site option by index (rest, smith, etc.).</summary>
    public static string ChooseRestOption(JsonElement request)
    {
        if (!request.TryGetProperty("index", out var indexEl))
            return CommandHandler.Error("missing_param", "choose_rest_option requires 'index'");

        int index = indexEl.GetInt32();

        try
        {
            var restRoom = NRun.Instance?.RestSiteRoom;
            if (restRoom == null)
                return CommandHandler.Error("no_rest_site", "Not at a rest site");

            var choicesContainer = ((Node)restRoom).GetNodeOrNull<Control>("%ChoicesContainer");
            if (choicesContainer == null)
                return CommandHandler.Error("no_choices", "Rest site choices container not found");

            var buttons = choicesContainer.GetChildren().OfType<NRestSiteButton>().ToList();
            if (index < 0 || index >= buttons.Count)
                return CommandHandler.Error("invalid_index", $"Rest option index {index} out of range (available: {buttons.Count})");

            var btn = buttons[index];
            var optionId = btn.Option.OptionId;
            SpireBridgeMod.Log($"choose_rest_option: clicking {optionId} (index {index}), _isUnclickable={btn.Get("_isUnclickable")}");
            btn.ForceClick();
            SpireBridgeMod.Log($"choose_rest_option: ForceClick completed for {optionId}");

            return CommandHandler.Ok("choose_rest_option", new { index, option_id = optionId });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("error", $"Rest option error: {ex.Message}");
        }
    }

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
            
            // Fallback: check room proceed buttons directly (shops, treasure, etc.)
            if (enabledButton == null)
            {
                try
                {
                    var nRun = NRun.Instance;
                    if (nRun != null)
                    {
                        IRoomWithProceedButton? room = null;
                        if (nRun.MerchantRoom != null && ((Control)nRun.MerchantRoom).IsVisibleInTree())
                            room = nRun.MerchantRoom;
                        else if (nRun.TreasureRoom != null && ((Control)nRun.TreasureRoom).IsVisibleInTree())
                            room = nRun.TreasureRoom;
                        else if (nRun.RestSiteRoom != null && ((Control)nRun.RestSiteRoom).IsVisibleInTree())
                            room = nRun.RestSiteRoom;
                        
                        if (room?.ProceedButton != null)
                        {
                            var btn = room.ProceedButton;
                            SpireBridgeMod.Log($"proceed: room button IsEnabled={btn.IsEnabled} IsVisible={btn.IsVisibleInTree()}");
                            if (btn.IsVisibleInTree())
                            {
                                enabledButton = btn;
                            }
                        }
                    }
                }
                catch (Exception ex2) { SpireBridgeMod.Log($"proceed: room fallback error: {ex2.Message}"); }
            }
            
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
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay is not Node overlayNode || !overlayNode.IsInsideTree())
                return CommandHandler.Error("no_overlay", "No overlay screen open to skip");

            // For card reward screens, use the known RewardAlternatives container
            if (overlay is NCardRewardSelectionScreen cardRewardScreen)
            {
                try
                {
                    var altContainer = overlayNode.GetNode<Control>("UI/RewardAlternatives");
                    if (altContainer != null)
                    {
                        foreach (var child in altContainer.GetChildren())
                        {
                            if (child is NCardRewardAlternativeButton altBtn && altBtn.IsVisibleInTree() && altBtn.IsEnabled)
                            {
                                altBtn.ForceClick();
                                return CommandHandler.Ok("skip", new { clicked = "alternative_button" });
                            }
                        }
                    }
                }
                catch { /* fall through */ }
            }

            // Generic fallback: walk overlay children safely for NButton with skip/close/back names
            try
            {
                foreach (var btn in SafeFindButtons(overlayNode))
                {
                    var name = btn.Name.ToString();
                    if (name.Contains("Skip", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Bowl", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Close", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("Back", StringComparison.OrdinalIgnoreCase))
                    {
                        btn.ForceClick();
                        return CommandHandler.Ok("skip", new { clicked = name });
                    }
                }
            }
            catch { /* tree traversal failed */ }

            return CommandHandler.Error("no_button", "No skip or close button found on overlay");
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
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay == null || !(overlay is Node overlayNode && overlayNode.IsInsideTree()))
                return CommandHandler.Error("wrong_screen", "No card selection screen open");

            var overlayType = overlay.GetType().Name;
            if (!overlayType.Contains("Card") && !overlayType.Contains("Reward") && !overlayType.Contains("Deck") && !overlayType.Contains("Select"))
                return CommandHandler.Error("wrong_screen", $"Not on a card selection screen (current: {overlayType})");

            Node searchRoot = overlayNode;
            SpireBridgeMod.Log($"choose_card: overlay type = {overlayType}");

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
                // Emit Pressed signal directly (NCardHolder.Pressed is what screens listen for)
                // ForceClick on hitbox emits Released which doesn't trigger card selection
                holder.EmitSignal(NCardHolder.SignalName.Pressed, holder);
                SpireBridgeMod.Log("choose_card: emitted Pressed signal");

                // For NCardGridSelectionScreen (card removal, transform), auto-confirm after selection
                if (overlay is NCardGridSelectionScreen gridScreen)
                {
                    SpireBridgeMod.Log("choose_card: scheduling auto-confirm");
                    // Schedule confirm after a short delay to let preview animation play
                    SpireBridgeMod.ScheduleAction(0.8f, () =>
                    {
                        try
                        {
                            // Find any visible+enabled confirm button in the overlay
                            var allConfirm = FindAll<NConfirmButton>((Node)gridScreen);
                            SpireBridgeMod.Log($"choose_card: found {allConfirm.Count} confirm buttons");
                            foreach (var btn in allConfirm)
                            {
                                SpireBridgeMod.Log($"  confirm btn: {btn.Name} visible={btn.IsVisibleInTree()} enabled={btn.IsEnabled}");
                                if (btn.IsVisibleInTree() && btn.IsEnabled)
                                {
                                    btn.ForceClick();
                                    SpireBridgeMod.Log($"choose_card: auto-confirmed via {btn.Name}");
                                    break;
                                }
                            }
                        }
                        catch (Exception ex) { SpireBridgeMod.Log($"choose_card: auto-confirm failed: {ex.Message}"); }
                    });
                }

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

            // Check we're on a rewards screen
            var overlay = NOverlayStack.Instance?.Peek();
            if (overlay == null || overlay.GetType().Name != "NRewardsScreen")
                return CommandHandler.Error("wrong_screen", "Not on rewards screen");

            var rewardButtons = FindAll<NRewardButton>((Node)overlay)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled)
                .ToList();

            if (index < 0 || index >= rewardButtons.Count)
                return CommandHandler.Error("invalid_index", $"Reward index {index} out of range (available: {rewardButtons.Count})");

            var btn = rewardButtons[index];
            var btnId = btn.GetInstanceId();

            if (_attemptedRewardIds.Contains(btnId))
                return CommandHandler.Error("already_attempted", $"Reward at index {index} already attempted (skip or proceed instead)");

            _attemptedRewardIds.Add(btnId);
            btn.ForceClick();
            var rewardType = btn.Reward?.GetType().Name ?? "unknown";
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
            if (eventRoom == null)
                return CommandHandler.Error("wrong_screen", "Not on an event screen");

            var eventButtons = FindAll<NEventOptionButton>(eventRoom)
                .Where(b => b.IsVisibleInTree() && b.IsEnabled && !b.Option.IsLocked)
                .ToList();

            if (eventButtons.Count == 0)
                return CommandHandler.Error("no_options", "No event options available");

            if (index < 0 || index >= eventButtons.Count)
                return CommandHandler.Error("invalid_index", $"Option index {index} out of range (available: {eventButtons.Count})");

            var btn = eventButtons[index];
            btn.ForceClick();
            var isProceed = btn.Option?.IsProceed ?? false;
            return CommandHandler.Ok("choose_option", new { index, is_proceed = isProceed, event_id = btn.Event?.Id?.Entry });
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

    public static string OpenChest()
    {
        try
        {
            var treasureRoom = NRun.Instance?.TreasureRoom;
            if (treasureRoom == null)
                return CommandHandler.Error("not_in_treasure", "Not in a treasure room");

            var chestBtn = ((Node)treasureRoom).GetNodeOrNull<NButton>("%Chest");
            if (chestBtn == null)
                return CommandHandler.Error("no_chest", "Chest button not found");

            if (!chestBtn.IsEnabled)
                return CommandHandler.Error("chest_opened", "Chest already opened");

            SpireBridgeMod.Log("open_chest: clicking chest button");
            chestBtn.ForceClick();
            return CommandHandler.Ok("open_chest", new { opened = true });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("error", $"Open chest error: {ex.Message}");
        }
    }

    /// <summary>Safely find NButton descendants, catching BadImageFormatException from problematic nodes.</summary>
        public static string ShopBuy(JsonElement request)
    {
        try
        {
            if (!request.TryGetProperty("index", out var indexEl))
                return CommandHandler.Error("missing_param", "shop_buy requires 'index'");

            int index = indexEl.GetInt32();

            var nMerchantRoom = NRun.Instance?.MerchantRoom;
            if (nMerchantRoom == null)
                return CommandHandler.Error("not_in_shop", "Not in a shop");

            var inventory = nMerchantRoom.Room?.Inventory;
            if (inventory == null)
                return CommandHandler.Error("no_inventory", "Shop inventory not found");

            var allEntries = inventory.AllEntries.ToList();
            if (index < 0 || index >= allEntries.Count)
                return CommandHandler.Error("invalid_index", $"Shop index {index} out of range (available: {allEntries.Count})");

            var entry = allEntries[index];
            if (!entry.IsStocked)
                return CommandHandler.Error("out_of_stock", $"Item at index {index} is sold out");

            if (!entry.EnoughGold)
                return CommandHandler.Error("not_enough_gold", $"Not enough gold ({inventory.Player?.Gold ?? 0} < {entry.Cost})");

            SpireBridgeMod.Log($"shop_buy: purchasing index {index}, cost {entry.Cost}");

            // Card removal has its own wrapper that opens a card select screen
            if (entry is MegaCrit.Sts2.Core.Entities.Merchant.MerchantCardRemovalEntry removalEntry)
            {
                MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(removalEntry.OnTryPurchaseWrapper(inventory));
                return CommandHandler.Ok("shop_buy", new { index, type = "card_removal", cost = entry.Cost });
            }

            MegaCrit.Sts2.Core.Helpers.TaskHelper.RunSafely(entry.OnTryPurchaseWrapper(inventory));
            return CommandHandler.Ok("shop_buy", new { index, cost = entry.Cost });
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("error", $"Shop buy error: {ex.Message}");
        }
    }

private static List<NButton> SafeFindButtons(Node root)
    {
        var results = new List<NButton>();
        SafeFindButtonsRecursive(root, results);
        return results;
    }

    private static void SafeFindButtonsRecursive(Node node, List<NButton> results)
    {
        try
        {
            if (node is NButton btn && btn.IsVisibleInTree() && btn.IsEnabled)
                results.Add(btn);
            foreach (var child in node.GetChildren())
                SafeFindButtonsRecursive(child, results);
        }
        catch (BadImageFormatException) { /* skip problematic subtree */ }
        catch (TypeLoadException) { /* skip problematic subtree */ }
    }
}
