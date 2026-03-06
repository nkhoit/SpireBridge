using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Subscribes to game events and pushes state updates to all connected WebSocket clients.
/// Eliminates polling — agents get notified when things actually change.
/// </summary>
public static class GameEventBridge
{
    private static bool _subscribed;
    private static ulong _seq;

    public static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // Run lifecycle
        RunManager.Instance.RunStarted += _ => Push("run_started");
        RunManager.Instance.RoomEntered += () => Push("room_entered");
        RunManager.Instance.RoomExited += () => Push("room_exited");
        RunManager.Instance.ActEntered += () => Push("act_entered");

        // Combat
        CombatManager.Instance.CombatSetUp += _ => Push("combat_start");
        CombatManager.Instance.CombatWon += _ => Push("combat_won");
        CombatManager.Instance.CombatEnded += _ => Push("combat_ended");
        CombatManager.Instance.TurnStarted += _ => Push("turn_started");
        CombatManager.Instance.TurnEnded += _ => Push("turn_ended");

        // Overlay changes (rewards, events, card selection, etc.)
        if (NOverlayStack.Instance != null)
        {
            NOverlayStack.Instance.Changed += () =>
            {
                // Reset reward tracking when a new rewards screen opens
                var overlay = NOverlayStack.Instance?.Peek();
                if (overlay?.GetType().Name == "NRewardsScreen")
                    ScreenActions.ResetRewardTracking();
                Push("screen_changed");
            };
        }

        SpireBridgeMod.Log("GameEventBridge subscribed to game events");
    }

    /// <summary>
    /// Push a state update to all connected clients.
    /// Includes the full game state + event name + sequence number.
    /// </summary>
    private static void Push(string eventName)
    {
        _seq++;
        try
        {
            var stateJson = StateReader.GetFullState();
            // Wrap state in an event envelope
            var envelope = $"{{\"type\":\"state_update\",\"event\":\"{eventName}\",\"seq\":{_seq},\"state\":{ExtractData(stateJson)}}}";
            SpireBridgeMod.BroadcastToClients(envelope);
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"GameEventBridge.Push({eventName}) error: {ex.Message}");
        }
    }

    /// <summary>
    /// Extract just the "data" field from a CommandHandler.Ok response,
    /// or return the whole thing if parsing fails.
    /// </summary>
    private static string ExtractData(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data))
                return data.GetRawText();
        }
        catch { }
        return json;
    }

    public static ulong CurrentSeq => _seq;
}
