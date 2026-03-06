using System;
using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.Nodes.Debug;

namespace SpireBridge;

/// <summary>
/// Executes dev console commands programmatically.
/// </summary>
public static class ConsoleAction
{
    public static string Execute(JsonElement root)
    {
        if (!root.TryGetProperty("command", out var cmdEl))
            return CommandHandler.Error("missing_param", "console requires 'command' string");

        var command = cmdEl.GetString();
        if (string.IsNullOrWhiteSpace(command))
            return CommandHandler.Error("invalid_param", "command must not be empty");

        try
        {
            // Access the NDevConsole singleton
            var nDevConsole = NDevConsole.Instance;

            // Get the private _devConsole field via reflection
            var field = typeof(NDevConsole).GetField("_devConsole",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (field == null)
                return CommandHandler.Error("reflection_error", "Could not find _devConsole field");

            var devConsole = (DevConsole)field.GetValue(nDevConsole);
            if (devConsole == null)
                return CommandHandler.Error("reflection_error", "DevConsole instance is null");

            // Execute the command
            var result = devConsole.ProcessCommand(command);

            SpireBridgeMod.Log($"Console command '{command}' → success={result.success}, msg={result.msg}");

            return CommandHandler.Ok("console", new
            {
                command,
                success = result.success,
                message = result.msg
            });
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"Console command error: {ex}");
            return CommandHandler.Error("console_error", ex.Message);
        }
    }
}
