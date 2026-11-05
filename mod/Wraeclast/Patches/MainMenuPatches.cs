using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Wraeclast.Patches;

/// <summary>
/// The main menu, cut down to what this conversion has: Play, Characters, Maps
/// and Settings.
///
/// Everything the base game advertises but we do not run is hidden — Unlocks
/// (we ship our own tomes/relics, so the native unlock tree is dead), Quests and
/// the quick-quest progress cards on the home screen (they name Megabonk
/// achievements such as "kill 15 000 enemies as a bush"), the right-hand
/// leaderboard, the credits panel and the Discord button. Their screens are
/// refused too, so a hotkey or a stale button cannot open a window we emptied.
///
/// Two layers, deliberately:
/// - the fields <c>MainMenu</c> exposes (exact, no guessing), and
/// - a name sweep of the menu hierarchy for what has no field at all — Discord
///   is a plain button, not a <c>MainMenu</c> member.
///
/// The pass runs on <c>Start</c> and again on <c>RefreshButtons</c>, because the
/// game re-applies button visibility every time a window closes.
/// </summary>
[HarmonyPatch]
internal static class MainMenuPatches
{
    /// <summary>Object names (lower case, substring) the sweep hides. Discord,
    /// credits and leaderboards have no <c>MainMenu</c> field; the rest catch
    /// the panels the fields do not cover (tabs, badges).</summary>
    private static readonly string[] SweptNames =
    {
        "discord",
        "credit",
        "leaderboard",
        "unlocks",
        "unlock",
        "quest"
    };

    private static bool logged;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.Start))]
    private static void AfterMenuStart(MainMenu __instance)
    {
        Clean(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.RefreshButtons))]
    private static void AfterMenuRefresh(MainMenu __instance)
    {
        Clean(__instance);
    }

    /// <summary>The screens themselves: refused while the menu is cleaned, so
    /// nothing can navigate into an empty window.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoToUnlocks))]
    private static bool BeforeGoToUnlocks()
    {
        return !Plugin.CleanMainMenu.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoToQuests))]
    private static bool BeforeGoToQuests()
    {
        return !Plugin.CleanMainMenu.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoToLeaderboards))]
    private static bool BeforeGoToLeaderboards()
    {
        return !Plugin.CleanMainMenu.Value;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.GoToCredits))]
    private static bool BeforeGoToCredits()
    {
        return !Plugin.CleanMainMenu.Value;
    }

    private static void Clean(MainMenu menu)
    {
        if (!Plugin.CleanMainMenu.Value || menu == null)
        {
            return;
        }

        try
        {
            var hidden = 0;
            hidden += HideObject(menu.btnUnlocks) ? 1 : 0;
            hidden += HideObject(menu.btnQuests) ? 1 : 0;
            hidden += HideObject(menu.tabUnlocks) ? 1 : 0;
            hidden += HideObject(menu.quickQuests) ? 1 : 0;
            hidden += HideObject(menu.quests) ? 1 : 0;
            hidden += HideObject(menu.leaderboards) ? 1 : 0;
            hidden += HideObject(menu.leaderboardsFull) ? 1 : 0;
            hidden += HideObject(menu.credits) ? 1 : 0;

            var swept = Sweep(menu.transform, new HashSet<int>());

            if (!logged || swept > 0)
            {
                logged = true;
                Plugin.Logger.LogInfo(
                    $"menu: {hidden} field(s) and {swept} named object(s) hidden " +
                    "(unlocks, quests, quick quests, leaderboards, credits, discord)");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"menu cleanup failed: {error.Message}");
        }
    }

    /// <summary>Hides every descendant whose name matches, once. An object that
    /// is already off is not counted, so the pass can run on every refresh
    /// without a growing number in the log.</summary>
    private static int Sweep(Transform root, HashSet<int> seen)
    {
        var hidden = 0;
        var transforms = root.GetComponentsInChildren<Transform>(true);
        foreach (var child in transforms)
        {
            if (child == null || child == root)
            {
                continue;
            }

            var name = child.name.ToLowerInvariant();
            var match = false;
            foreach (var pattern in SweptNames)
            {
                if (name.Contains(pattern))
                {
                    match = true;
                    break;
                }
            }

            if (!match || !seen.Add(child.GetInstanceID()))
            {
                continue;
            }

            if (HideObject(child.gameObject))
            {
                hidden++;
            }
        }

        return hidden;
    }

    internal static bool HideObject(UnityEngine.Object target)
    {
        if (target == null)
        {
            return false;
        }

        try
        {
            GameObject gameObject;
            if (target is Component component)
            {
                gameObject = component.gameObject;
            }
            else if (target is GameObject direct)
            {
                gameObject = direct;
            }
            else
            {
                return false;
            }

            if (!gameObject.activeSelf)
            {
                return false;
            }

            gameObject.SetActive(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// The character screen loses the parts that only fit Megabonk's own designs:
/// the **hat** picker (our bodies are PoE models on their own skeleton, so a
/// Megabonk hat has nothing to sit on) and the **skin** colour selector (a PoE
/// body is one painted atlas, there is no recolour channel). Both are fields of
/// <c>CharacterMenu</c>, so they are hidden by reference on every open, and the
/// hat popup is hidden with them in case a hotkey or a stale state opens it.
/// Same switch as <see cref="MainMenuPatches"/>: <c>[UI] CleanMenu</c>.
/// </summary>
[HarmonyPatch]
internal static class CharacterMenuCleanup
{
    private static bool logged;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharacterMenu), nameof(CharacterMenu.OnEnable))]
    private static void AfterCharacterMenuEnable(CharacterMenu __instance)
    {
        Hide(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(CharacterMenu), nameof(CharacterMenu.TryInit))]
    private static void AfterCharacterMenuInit(CharacterMenu __instance)
    {
        Hide(__instance);
    }

    private static void Hide(CharacterMenu menu)
    {
        if (!Plugin.CleanMainMenu.Value || menu == null)
        {
            return;
        }

        try
        {
            var hidden = 0;
            hidden += MainMenuPatches.HideObject(menu.b_hats) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(menu.b_hatPrev) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(menu.skinSelection) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(menu.hatSelection) ? 1 : 0;

            if (hidden > 0 && !logged)
            {
                logged = true;
                Plugin.Logger.LogInfo(
                    $"menu: hats and the skin colour selector hidden ({hidden} part(s))");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"menu: hiding the hat/skin controls failed ({error.Message})");
        }
    }
}
