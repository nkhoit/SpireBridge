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
                    "console" => ConsoleAction.Execute(doc.RootElement),
                    "choose_card" => ScreenActions.ChooseCard(doc.RootElement),
                    "skip" => ScreenActions.Skip(),
                    "choose_option" => ScreenActions.ChooseOption(doc.RootElement),
                    "choose_reward" => ScreenActions.ChooseReward(doc.RootElement),
                    "proceed" => ScreenActions.Proceed(),
                    "confirm" => ScreenActions.Confirm(),
                    "start_run" => RunActions.StartRun(doc.RootElement),
                    "abandon_run" => RunActions.AbandonRun(),
                    "debug_tree" => DebugTree(doc.RootElement),
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

    private static string DebugTree(JsonElement request)
    {
        var path = "/root";
        if (request.TryGetProperty("path", out var pathEl))
            path = pathEl.GetString() ?? "/root";
        int depth = 3;
        if (request.TryGetProperty("depth", out var depthEl))
            depth = depthEl.GetInt32();

        var tree = (Godot.SceneTree)Godot.Engine.GetMainLoop();
        var node = tree.Root.GetNodeOrNull(path);
        if (node == null)
            return Error("not_found", $"Node not found: {path}");

        var lines = new System.Collections.Generic.List<string>();
        DumpNode(node, 0, depth, lines);
        return Ok("debug_tree", new { path, lines });
    }

    private static void DumpNode(Godot.Node node, int level, int maxDepth, System.Collections.Generic.List<string> lines)
    {
        var indent = new string(' ', level * 2);
        var vis = node is Godot.CanvasItem ci ? (ci.Visible ? "V" : "H") : "?";
        lines.Add($"{indent}{node.Name} [{node.GetType().Name}] ({vis})");
        if (level >= maxDepth) return;
        foreach (var child in node.GetChildren())
            DumpNode(child, level + 1, maxDepth, lines);
    }

    internal static JsonSerializerOptions GetJsonOpts() => JsonOpts;
}
