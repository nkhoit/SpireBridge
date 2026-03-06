using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Screens.CharacterSelect;

namespace SpireBridge;

/// <summary>
/// Handles run lifecycle: start_run, abandon_run.
/// These involve async UI navigation so they use a coroutine approach.
/// </summary>
public static class RunActions
{
    private static bool _navigating;

    public static string StartRun(JsonElement request)
    {
        if (_navigating)
            return CommandHandler.Error("busy", "Already navigating menus");

        var character = "Ironclad";
        if (request.TryGetProperty("character", out var charEl))
            character = charEl.GetString() ?? "Ironclad";

        int ascension = -1; // -1 = don't change
        if (request.TryGetProperty("ascension", out var ascEl))
            ascension = ascEl.GetInt32();

        _navigating = true;
        StartRunAsync(character, ascension);
        return CommandHandler.Ok("start_run", new { status = "navigating", character, ascension = ascension >= 0 ? (int?)ascension : null });
    }

    public static string AbandonRun()
    {
        if (_navigating)
            return CommandHandler.Error("busy", "Already navigating menus");

        _navigating = true;
        AbandonRunAsync();
        return CommandHandler.Ok("abandon_run", new { status = "abandoning" });
    }

    private static void BroadcastProgress(string step, string detail = "")
    {
        var msg = JsonSerializer.Serialize(new { @event = "run_progress", step, detail });
        SpireBridgeMod.BroadcastToClients(msg);
        SpireBridgeMod.Log($"start_run: {step} {detail}".TrimEnd());
    }

    private static async void StartRunAsync(string characterId, int ascension)
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var root = tree.Root;

            // Find main menu
            var mainMenu = root.GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
            if (mainMenu == null)
            {
                BroadcastProgress("error", "Main menu not found — are you in a run? Use abandon_run first.");
                return;
            }

            // Check for existing run and abandon
            var abandonBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
            if (abandonBtn != null && abandonBtn.Visible)
            {
                BroadcastProgress("abandoning_existing_run");
                abandonBtn.ForceClick();
                await WaitFor(() => NModalContainer.Instance?.OpenModal != null, 5000);

                if (NModalContainer.Instance?.OpenModal is Node modal)
                {
                    var yesBtn = modal.GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                    if (yesBtn != null) yesBtn.ForceClick();
                    await WaitFor(() => NModalContainer.Instance?.OpenModal == null, 5000);
                    await Task.Delay(500);
                }
            }

            // Click singleplayer
            BroadcastProgress("clicking_singleplayer");
            var spBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
            if (spBtn == null)
            {
                BroadcastProgress("error", "SingleplayerButton not found");
                return;
            }
            spBtn.ForceClick();
            await Task.Delay(500);

            // Wait for either character select or standard run submenu
            Control? charSelect = null;
            NButton? standardBtn = null;

            await WaitFor(() =>
            {
                charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
                standardBtn = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
                return (charSelect?.Visible ?? false) || (standardBtn?.Visible ?? false);
            }, 5000);

            if (standardBtn?.Visible == true && !(charSelect?.Visible == true))
            {
                BroadcastProgress("clicking_standard_run");
                standardBtn.ForceClick();
                await WaitFor(() => mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.Visible ?? false, 5000);
                charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            }

            if (charSelect == null || !charSelect.Visible)
            {
                BroadcastProgress("error", "CharacterSelectScreen not found");
                return;
            }

            // Select character
            BroadcastProgress("selecting_character", characterId);
            var btnContainer = charSelect.GetNodeOrNull<Node>("CharSelectButtons/ButtonContainer");
            if (btnContainer != null)
            {
                var charButtons = FindAll<NCharacterSelectButton>(btnContainer);
                var target = charButtons.FirstOrDefault(b =>
                    b.Character?.Id?.ToString().Equals(characterId, StringComparison.OrdinalIgnoreCase) == true ||
                    b.Character?.GetType().Name?.ToString().Equals(characterId, StringComparison.OrdinalIgnoreCase) == true);

                if (target == null)
                    target = charButtons.FirstOrDefault(b => !b.IsLocked);

                if (target != null)
                {
                    SpireBridgeMod.Log($"start_run: Found character {target.Character?.Id}");
                    target.Select();
                    await Task.Delay(300);
                }
                else
                {
                    BroadcastProgress("warning", $"Character '{characterId}' not found, using default");
                }
            }

            // Set ascension if requested
            if (ascension >= 0)
            {
                var ascPanel = charSelect.GetNodeOrNull<NAscensionPanel>("%AscensionPanel");
                if (ascPanel != null)
                {
                    BroadcastProgress("setting_ascension", ascension.ToString());
                    ascPanel.SetAscensionLevel(ascension);
                    await Task.Delay(200);
                }
                else
                {
                    BroadcastProgress("warning", "AscensionPanel not found");
                }
            }

            // Click confirm/embark
            BroadcastProgress("confirming");
            var confirmBtn = charSelect.GetNodeOrNull<NButton>("ConfirmButton");
            if (confirmBtn != null)
            {
                confirmBtn.ForceClick();
            }
            else
            {
                BroadcastProgress("error", "ConfirmButton not found");
                return;
            }

            // Wait for run to actually start (main menu disappears)
            await WaitFor(() =>
            {
                var mm = root.GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
                return mm == null || !mm.Visible;
            }, 10000);

            BroadcastProgress("complete", characterId);
        }
        catch (Exception ex)
        {
            BroadcastProgress("error", ex.Message);
        }
        finally
        {
            _navigating = false;
        }
    }

    private static async void AbandonRunAsync()
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var root = tree.Root;

            // Check if we're on main menu with a saved run
            var mainMenu = root.GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
            if (mainMenu != null && mainMenu.Visible)
            {
                var abandonBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
                if (abandonBtn != null && abandonBtn.Visible)
                {
                    SpireBridgeMod.Log("abandon_run: Abandoning from main menu");
                    abandonBtn.ForceClick();
                    await WaitFor(() => NModalContainer.Instance?.OpenModal != null, 5000);

                    if (NModalContainer.Instance?.OpenModal is Node modal)
                    {
                        var yesBtn = modal.GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                        if (yesBtn != null)
                        {
                            yesBtn.ForceClick();
                            await WaitFor(() => NModalContainer.Instance?.OpenModal == null, 5000);
                        }
                    }
                    SpireBridgeMod.Log("abandon_run: Complete (from main menu)");
                    return;
                }

                SpireBridgeMod.Log("abandon_run: On main menu but no run to abandon");
                return;
            }

            // In-run: click options → abandon → confirm
            var optionsBtn = root.GetNodeOrNull<NButton>("/root/Game/RootSceneContainer/Run/GlobalUi/TopBar/RightAlignedStuff/Options");
            if (optionsBtn != null)
            {
                SpireBridgeMod.Log("abandon_run: Clicking Options");
                optionsBtn.ForceClick();
                await Task.Delay(500);

                var abandonRunBtn = root.GetNodeOrNull<NButton>("/root/Game/RootSceneContainer/Run/GlobalUi/CapstoneScreenContainer/OptionsScreen/AbandonRunButton");
                if (abandonRunBtn != null)
                {
                    await WaitFor(() => abandonRunBtn.IsVisibleInTree(), 3000);
                    SpireBridgeMod.Log("abandon_run: Clicking Abandon");
                    abandonRunBtn.ForceClick();
                    await Task.Delay(500);

                    var proceedBtn = root.GetNodeOrNull<NButton>("/root/Game/RootSceneContainer/Run/GlobalUi/OverlayScreensContainer/GameOverScreen/UI/ProceedButton");
                    if (proceedBtn != null)
                    {
                        await WaitFor(() => proceedBtn.IsVisibleInTree(), 5000);
                        SpireBridgeMod.Log("abandon_run: Clicking Proceed");
                        proceedBtn.ForceClick();
                    }
                }
            }
            else
            {
                SpireBridgeMod.Log("abandon_run: Not in a run and not on main menu");
            }

            SpireBridgeMod.Log("abandon_run: Complete");
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"abandon_run error: {ex.Message}");
        }
        finally
        {
            _navigating = false;
        }
    }

    private static async Task WaitFor(Func<bool> condition, int timeoutMs)
    {
        var elapsed = 0;
        while (!condition() && elapsed < timeoutMs)
        {
            await Task.Delay(100);
            elapsed += 100;
        }
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
}
