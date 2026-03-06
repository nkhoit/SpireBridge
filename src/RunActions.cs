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

        _navigating = true;
        StartRunAsync(character);
        return CommandHandler.Ok("start_run", new { status = "navigating", character });
    }

    public static string AbandonRun()
    {
        if (_navigating)
            return CommandHandler.Error("busy", "Already navigating menus");

        _navigating = true;
        AbandonRunAsync();
        return CommandHandler.Ok("abandon_run", new { status = "navigating" });
    }

    private static async void StartRunAsync(string characterId)
    {
        try
        {
            var tree = (SceneTree)Engine.GetMainLoop();
            var root = tree.Root;

            // Find main menu
            var mainMenu = root.GetNodeOrNull<Control>("/root/Game/RootSceneContainer/MainMenu");
            if (mainMenu == null)
            {
                SpireBridgeMod.Log("start_run: Main menu not found");
                _navigating = false;
                return;
            }

            // Check for existing run and abandon
            var abandonBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/AbandonRunButton");
            if (abandonBtn != null && abandonBtn.Visible)
            {
                SpireBridgeMod.Log("start_run: Abandoning existing run first");
                abandonBtn.ForceClick();
                await WaitFor(() => NModalContainer.Instance?.OpenModal != null, 5000);

                if (NModalContainer.Instance?.OpenModal is Node modal)
                {
                    var yesBtn = modal.GetNodeOrNull<NButton>("VerticalPopup/YesButton");
                    if (yesBtn != null) yesBtn.ForceClick();
                    await WaitFor(() => NModalContainer.Instance?.OpenModal == null, 5000);
                }
            }

            // Click singleplayer
            var spBtn = mainMenu.GetNodeOrNull<NButton>("MainMenuTextButtons/SingleplayerButton");
            if (spBtn == null)
            {
                SpireBridgeMod.Log("start_run: SingleplayerButton not found");
                _navigating = false;
                return;
            }
            spBtn.ForceClick();
            await Task.Delay(500);

            // Check for standard run submenu vs direct character select
            var charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            var standardBtn = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");

            await WaitFor(() =>
            {
                charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
                standardBtn = mainMenu.GetNodeOrNull<NButton>("Submenus/SingleplayerSubmenu/StandardButton");
                return (charSelect?.Visible ?? false) || (standardBtn?.Visible ?? false);
            }, 5000);

            if (standardBtn?.Visible == true && !(charSelect?.Visible == true))
            {
                SpireBridgeMod.Log("start_run: Clicking Standard Run");
                standardBtn.ForceClick();
                await WaitFor(() => mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen")?.Visible ?? false, 5000);
                charSelect = mainMenu.GetNodeOrNull<Control>("Submenus/CharacterSelectScreen");
            }

            if (charSelect == null)
            {
                SpireBridgeMod.Log("start_run: CharacterSelectScreen not found");
                _navigating = false;
                return;
            }

            // Select character
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
                    SpireBridgeMod.Log($"start_run: Selecting character {target.Character?.Id}");
                    target.Select();
                    await Task.Delay(200);
                }
            }

            // Click confirm
            var confirmBtn = charSelect.GetNodeOrNull<NButton>("ConfirmButton");
            if (confirmBtn != null)
            {
                SpireBridgeMod.Log("start_run: Confirming character");
                confirmBtn.ForceClick();
            }

            SpireBridgeMod.Log("start_run: Complete");
        }
        catch (Exception ex)
        {
            SpireBridgeMod.Log($"start_run error: {ex.Message}");
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

            // In-run: click options → abandon → confirm
            var optionsBtn = root.GetNodeOrNull<NButton>("/root/Game/RootSceneContainer/Run/GlobalUi/TopBar/RightAlignedStuff/Options");
            if (optionsBtn != null)
            {
                SpireBridgeMod.Log("abandon_run: Clicking Options");
                optionsBtn.ForceClick();
                await Task.Delay(500);

                var abandonBtn = root.GetNodeOrNull<NButton>("/root/Game/RootSceneContainer/Run/GlobalUi/CapstoneScreenContainer/OptionsScreen/AbandonRunButton");
                if (abandonBtn != null)
                {
                    await WaitFor(() => abandonBtn.IsVisibleInTree(), 3000);
                    SpireBridgeMod.Log("abandon_run: Clicking Abandon");
                    abandonBtn.ForceClick();
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
