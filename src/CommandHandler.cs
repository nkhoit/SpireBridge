using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SpireBridge;

/// <summary>
/// Routes incoming WebSocket commands to the appropriate handler.
/// All methods run on the main (Godot) thread.
/// </summary>
public static class CommandHandler
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public static string Handle(string raw)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch { return Error("invalid_json", "Could not parse JSON"); }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("action", out var actionEl))
                return Error("missing_action", "Request must include 'action'");

            var action = actionEl.GetString();
            var id = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;

            try
            {
                var result = action switch
                {
                    "get_state" => StateReader.GetFullState(),
                    "play" => CombatActions.PlayCard(doc.RootElement),
                    "end_turn" => CombatActions.EndTurn(),
                    "use_potion" => CombatActions.UsePotion(doc.RootElement),
                    "choose_node" => MapActions.ChooseNode(doc.RootElement),
                    // Stubs for v0.1
                    "choose_card" => Stub("choose_card"),
                    "skip" => Stub("skip"),
                    "choose_option" => Stub("choose_option"),
                    "proceed" => Stub("proceed"),
                    "start_run" => Stub("start_run"),
                    "abandon_run" => Stub("abandon_run"),
                    _ => Error("unknown_action", $"Unknown action: {action}")
                };

                // Inject the request id if provided
                if (id != null && result != null)
                {
                    var resultDoc = JsonDocument.Parse(result);
                    using (resultDoc)
                    {
                        var dict = new Dictionary<string, object?> { ["id"] = id };
                        foreach (var prop in resultDoc.RootElement.EnumerateObject())
                        {
                            dict[prop.Name] = prop.Value.Clone();
                        }
                        return JsonSerializer.Serialize(dict, JsonOpts);
                    }
                }

                return result ?? Error("null_response", "Handler returned null");
            }
            catch (Exception ex)
            {
                SpireBridgeMod.Log($"Error handling '{action}': {ex}");
                return Error("handler_error", ex.Message, id);
            }
        }
    }

    public static string Ok(string action, object? data = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["status"] = "ok",
            ["action"] = action
        };
        if (data != null) response["data"] = data;
        return JsonSerializer.Serialize(response, JsonOpts);
    }

    public static string Error(string code, string message, string? id = null)
    {
        var response = new Dictionary<string, object?>
        {
            ["status"] = "error",
            ["error"] = code,
            ["message"] = message
        };
        if (id != null) response["id"] = id;
        return JsonSerializer.Serialize(response, JsonOpts);
    }

    public static string Stub(string action)
    {
        return Ok(action, new { stub = true, message = $"'{action}' is not yet implemented" });
    }

    internal static JsonSerializerOptions GetJsonOpts() => JsonOpts;
}
