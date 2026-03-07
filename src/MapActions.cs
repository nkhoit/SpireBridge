using System;
using System.Linq;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Runs;

namespace SpireBridge;

/// <summary>
/// Handles map navigation: choose_node.
/// Runs on the main Godot thread.
/// </summary>
public static class MapActions
{
    public static string ChooseNode(JsonElement request)
    {
        var runState = RunManager.Instance.DebugOnlyGetState();
        if (runState == null)
            return CommandHandler.Error("no_run", "No run in progress");

        if (!request.TryGetProperty("row", out var rowEl) || !request.TryGetProperty("col", out var colEl))
            return CommandHandler.Error("missing_param", "choose_node requires 'row' and 'col'");

        int row = rowEl.GetInt32();
        int col = colEl.GetInt32();

        var map = runState.Map;
        var targetPoint = map.GetPoint(new MapCoord { row = row, col = col });
        if (targetPoint == null)
            return CommandHandler.Error("invalid_node", $"No map node at ({row}, {col})");

        // Validate it's a reachable node
        if (runState.VisitedMapCoords.Count == 0)
        {
            // Must be in row 0
            if (row != 0)
                return CommandHandler.Error("invalid_node", "First node must be in row 0");
        }
        else
        {
            var lastCoord = runState.VisitedMapCoords[^1];
            var currentPoint = map.GetPoint(lastCoord);
            if (currentPoint == null || !currentPoint.Children.Any(c => c.coord.row == row && c.coord.col == col))
                return CommandHandler.Error("unreachable", $"Node ({row}, {col}) is not reachable from current position");
        }

        // Find and click the NMapPoint in the scene tree
        var tree = (SceneTree)Engine.GetMainLoop();
        var root = tree.Root;
        try
        {
            var nRun = root.GetNode<Node>("/root/Game/RootSceneContainer/Run") as NRun;
            if (nRun == null)
                return CommandHandler.Error("no_ui", "Run UI not found");

            var mapScreen = nRun.GlobalUi.MapScreen;
            var mapPoints = FindAll<NMapPoint>(mapScreen);
            SpireBridgeMod.Log($"choose_node: found {mapPoints.Count} map points, looking for ({row},{col})");
            var targetNMapPoint = mapPoints.FirstOrDefault(mp => mp.Point.coord.row == row && mp.Point.coord.col == col);

            if (targetNMapPoint == null)
            {
                // Log available points for debugging
                foreach (var mp in mapPoints)
                    SpireBridgeMod.Log($"  available: ({mp.Point.coord.row},{mp.Point.coord.col}) {mp.Point.PointType}");
                return CommandHandler.Error("node_not_found", $"Map point UI element at ({row}, {col}) not found");
            }

            SpireBridgeMod.Log($"choose_node: clicking ({row},{col}) type={targetNMapPoint.Point.PointType}");
            SpireBridgeMod.Log($"  IsVisibleInTree: {targetNMapPoint.IsVisibleInTree()}");
            SpireBridgeMod.Log($"  IsEnabled: {targetNMapPoint.IsEnabled}");
            SpireBridgeMod.Log($"  MouseFilter: {targetNMapPoint.MouseFilter}");
            SpireBridgeMod.Log($"  MapScreen.IsOpen: {NMapScreen.Instance?.IsOpen}");
            // Force-enable if disabled (map nodes can be temporarily disabled during transitions)
            if (!targetNMapPoint.IsEnabled)
            {
                SpireBridgeMod.Log($"  Map point disabled, attempting to enable via reflection");
                // Try to find and set the backing field for IsEnabled
                var type = targetNMapPoint.GetType();
                while (type != null)
                {
                    foreach (var field in type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (field.Name.ToLower().Contains("enable") || field.Name.ToLower().Contains("disabled"))
                            SpireBridgeMod.Log($"    Field: {field.DeclaringType?.Name}.{field.Name} = {field.GetValue(targetNMapPoint)}");
                    }
                    foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    {
                        if (prop.Name.ToLower().Contains("enable") || prop.Name.ToLower().Contains("disabled") || prop.Name.ToLower().Contains("interact"))
                        {
                            try { SpireBridgeMod.Log($"    Prop: {prop.DeclaringType?.Name}.{prop.Name} = {prop.GetValue(targetNMapPoint)} (canWrite={prop.CanWrite})"); }
                            catch { SpireBridgeMod.Log($"    Prop: {prop.DeclaringType?.Name}.{prop.Name} (read failed)"); }
                        }
                    }
                    type = type.BaseType;
                }
            }
            // Use ForceClick like AutoSlayer does (NMapPoint → NButton → NClickableControl)
            targetNMapPoint.ForceClick();
        }
        catch (Exception ex)
        {
            return CommandHandler.Error("click_error", $"Error clicking map node: {ex.Message}");
        }

        return CommandHandler.Ok("choose_node", new { row, col, type = targetPoint.PointType.ToString() });
    }

    /// <summary>Recursively find all nodes of type T.</summary>
    internal static System.Collections.Generic.List<T> FindAll<T>(Node root) where T : Node
    {
        var results = new System.Collections.Generic.List<T>();
        FindAllRecursive(root, results);
        return results;
    }

    private static void FindAllRecursive<T>(Node node, System.Collections.Generic.List<T> results) where T : Node
    {
        if (node is T t) results.Add(t);
        foreach (var child in node.GetChildren())
        {
            FindAllRecursive(child, results);
        }
    }
}
