using System;
using System.Collections.Generic;
using Godot;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Subscribes to game events and pushes state updates to all connected WebSocket clients.
/// Uses debouncing: state only pushes after no changes for DebounceMs.
/// </summary>
public static class GameEventBridge
{
    private static bool _subscribed;
    private static ulong _seq;
    private static string? _pendingEvent;
    private static ulong _debounceId;

    /// <summary>Debounce window in seconds. State pushes after this much silence.</summary>
    private const float DebounceSec = 0.5f;
    /// <summary>Max delay for command-triggered pushes — debounce but don't exceed this.</summary>
    private const float MaxDelaySec = 2.0f;
    private static ulong _deadlineId;

    public static void Subscribe()
    {
        if (_subscribed) return;
        _subscribed = true;

        // Run lifecycle
        RunManager.Instance.RunStarted += _ => DebouncePush("run_started");
        RunManager.Instance.RoomEntered += () => DebouncePush("room_entered");
        RunManager.Instance.RoomExited += () => DebouncePush("room_exited");
        RunManager.Instance.ActEntered += () => DebouncePush("act_entered");

        // Combat — skip turn_started/turn_ended (fire before cards are dealt/cleared)
        CombatManager.Instance.CombatSetUp += _ => DebouncePush("combat_start");
        CombatManager.Instance.CombatWon += _ => DebouncePush("combat_won");
        CombatManager.Instance.CombatEnded += _ => DebouncePush("combat_ended");

        // Overlay changes (rewards, events, card selection, etc.)
        if (NOverlayStack.Instance != null)
        {
            NOverlayStack.Instance.Changed += () =>
            {
                var overlay = NOverlayStack.Instance?.Peek();
                if (overlay?.GetType().Name == "NRewardsScreen")
                    ScreenActions.ResetRewardTracking();
                DebouncePush("screen_changed");
            };
        }

        SpireBridgeMod.Log("GameEventBridge subscribed (debounce={DebounceSec}s)");
    }

    /// <summary>
    /// Schedule a debounced state push. Resets the timer on each call.
    /// The event name used is the latest one (most recent wins).
    /// </summary>
    public static void DebouncePush(string eventName)
    {
        _pendingEvent = eventName;
        _debounceId++;
        var myId = _debounceId;
        SpireBridgeMod.ScheduleAction(DebounceSec, () =>
        {
            // Only fire if no newer debounce has been scheduled
            if (myId == _debounceId)
                FlushPush();
        });
    }

    /// <summary>
    /// Like DebouncePush but also sets a hard max deadline.
    /// Used after command execution — ensures state arrives within MaxDelaySec
    /// even if game events keep resetting the debounce.
    /// </summary>
    public static void DebouncePushWithDeadline(string eventName)
    {
        DebouncePush(eventName);
        _deadlineId++;
        var myDeadline = _deadlineId;
        SpireBridgeMod.ScheduleAction(MaxDelaySec, () =>
        {
            // Force push if we still have a pending event (debounce kept resetting)
            if (myDeadline == _deadlineId && _pendingEvent != null)
                FlushPush();
        });
    }

    /// <summary>
    /// Immediately push state (bypasses debounce). Used for command responses
    /// where we want the client to get state right away.
    /// </summary>
    public static void PushState(string eventName)
    {
        _pendingEvent = null;
        _debounceId++; // Cancel any pending debounce
        _seq++;
        try
        {
            var stateJson = StateReader.GetFullState();
            var envelope = $"{{\"type\":\"state_update\",\"event\":\"{eventName}\",\"seq\":{_seq},\"state\":{ExtractData(stateJson)}}}";
            SpireBridgeMod.BroadcastToClients(envelope);
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"GameEventBridge.PushState({eventName}) error: {ex.Message}");
        }
    }

    private static void FlushPush()
    {
        var evt = _pendingEvent ?? "debounced";
        _pendingEvent = null;
        _seq++;
        try
        {
            var stateJson = StateReader.GetFullState();
            var envelope = $"{{\"type\":\"state_update\",\"event\":\"{evt}\",\"seq\":{_seq},\"state\":{ExtractData(stateJson)}}}";
            SpireBridgeMod.BroadcastToClients(envelope);
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"GameEventBridge.FlushPush({evt}) error: {ex.Message}");
        }
    }

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
