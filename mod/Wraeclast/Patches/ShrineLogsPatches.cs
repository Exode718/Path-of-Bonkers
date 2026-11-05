using System;
using System.Collections.Generic;
using HarmonyLib;
using TMPro;
using UnityEngine;
using Wraeclast.Skills;

namespace Wraeclast.Patches;

/// <summary>
/// V4: the Échap panel's “Shrine Logs” becomes the class's skill-mod list.
///
/// The panel is a <c>ShrineLogs</c> component whose own entries are Megabonk
/// shrine blessings, and R6's mods are not <c>StatModifier</c>s, so the entries
/// are ours: the native <c>AddLog</c> is refused, and each time the pause opens
/// the content is rebuilt from <see cref="SkillMods.Describe"/> by cloning the
/// panel's own <c>ShrineLogEntry</c> prefab. The title is renamed with it, so
/// nothing on screen still says “Shrine Logs”.
///
/// Two hooks, because the panel is created once but read many times:
/// <c>ShrineLogs.Start</c> draws the first empty state, and <c>PauseUi.OnEnable</c>
/// (which is the pause opening; its <c>OnDisable</c> is the pause closing)
/// redraws the list with the mods taken since.
/// </summary>
[HarmonyPatch]
internal static class SkillModListPatches
{
    /// <summary>Our entries, so a redraw can drop the previous list.</summary>
    private static readonly List<GameObject> Entries = new List<GameObject>();

    private static string signature;
    private static bool loggedProblem;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(ShrineLogs), nameof(ShrineLogs.Start))]
    private static void AfterStart(ShrineLogs __instance)
    {
        Render(__instance);
    }

    /// <summary>A shrine blessing must not add a native line: the panel lists
    /// skill mods now. Shrines themselves stay vanilla.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ShrineLogs), nameof(ShrineLogs.AddLog))]
    private static bool BeforeAddLog()
    {
        return !SkillMods.Active;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(PauseUi), nameof(PauseUi.OnEnable))]
    private static void AfterPauseEnable()
    {
        var manager = UiManager.Instance;
        Render(manager == null ? null : manager.shrineLogs);
    }

    private static void Render(ShrineLogs logs)
    {
        if (!SkillMods.Active || logs == null)
        {
            return;
        }

        try
        {
            var template = logs.prefab;
            var parent = template == null ? null : template.transform.parent;
            if (parent == null)
            {
                Warn("the panel's entry prefab is missing, the skill-mod list stays empty");
                return;
            }

            Clear();
            RenameTitle(parent);
            HideNativeEntries(parent, template);

            var lines = SkillMods.Describe();
            if (lines.Count == 0)
            {
                AddEntry(template, parent, "No skill mods taken yet.");
            }
            else
            {
                foreach (var line in lines)
                {
                    AddEntry(template, parent, line);
                }
            }

            var now = string.Join("|", lines);
            if (!string.Equals(now, signature, StringComparison.Ordinal))
            {
                signature = now;
                Plugin.Logger.LogInfo(
                    $"skill mod list: {lines.Count} line(s) in the Échap panel (was \"Shrine Logs\")");
            }
        }
        catch (Exception error)
        {
            Warn($"the list failed: {error.Message}");
        }
    }

    /// <summary>The panel's title is a <c>TextMeshProUGUI</c> named “Title”
    /// above the entries; it says what the panel now carries.</summary>
    private static void RenameTitle(Transform from)
    {
        for (var current = from; current != null; current = current.parent)
        {
            var title = current.Find("Title");
            var text = title == null ? null : title.GetComponent<TextMeshProUGUI>();
            if (text == null)
            {
                continue;
            }

            text.text = "Skill Mods";
            return;
        }
    }

    /// <summary>Any entry that is not the prefab is a native shrine line; the
    /// <c>AddLog</c> prefix already stops new ones, this catches the ones a
    /// scene may have drawn before the switch was read.</summary>
    private static void HideNativeEntries(Transform parent, GameObject template)
    {
        var entries = parent.GetComponentsInChildren<ShrineLogEntry>(true);
        if (entries == null)
        {
            return;
        }

        for (var index = 0; index < entries.Length; index++)
        {
            var entry = entries[index];
            if (entry == null || entry.gameObject == template)
            {
                continue;
            }

            if (entry.gameObject.activeSelf)
            {
                entry.gameObject.SetActive(false);
            }
        }
    }

    private static void AddEntry(GameObject template, Transform parent, string line)
    {
        var clone = UnityEngine.Object.Instantiate(template, parent);
        if (clone == null)
        {
            return;
        }

        clone.name = "WraeclastSkillMod";
        clone.SetActive(true);

        var entry = clone.GetComponent<ShrineLogEntry>();
        if (entry != null)
        {
            entry.Set(line);
        }

        Entries.Add(clone);
    }

    private static void Clear()
    {
        for (var index = 0; index < Entries.Count; index++)
        {
            var clone = Entries[index];
            if (clone != null)
            {
                UnityEngine.Object.Destroy(clone);
            }
        }

        Entries.Clear();
    }

    private static void Warn(string message)
    {
        if (loggedProblem)
        {
            return;
        }

        loggedProblem = true;
        Plugin.Logger.LogWarning($"skill mod list: {message}");
    }
}
