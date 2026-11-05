using System;
using HarmonyLib;
using Il2CppSystem.Collections.Generic;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// Hides the fourteen base characters from the selection grid without touching
/// the enum or the data: <c>CharacterMenu.TryInit</c> copies
/// <c>DataManager.unsortedCharacterData</c> into its own list and builds one
/// button per entry, so for the duration of that call the registry is swapped
/// for the mapped slots only, then restored.
///
/// The data stays intact for everything else (progression, skins, achievements,
/// the unlocks shop) because only the menu's view is filtered.
/// </summary>
[HarmonyPatch(typeof(CharacterMenu))]
internal static class CharacterMenuPatches
{
    [HarmonyPrefix]
    [HarmonyPatch(nameof(CharacterMenu.TryInit))]
    private static void BeforeTryInit(
        CharacterMenu __instance,
        out Il2CppSystem.Collections.Generic.List<CharacterData> __state)
    {
        __state = null;

        try
        {
            var manager = DataManager.Instance;
            if (manager == null || manager.unsortedCharacterData == null)
            {
                return;
            }

            var original = manager.unsortedCharacterData;
            var filtered = new Il2CppSystem.Collections.Generic.List<CharacterData>();

            for (var index = 0; index < original.Count; index++)
            {
                var data = original[index];
                if (data != null && ClassSwap.IsMapped(data.eCharacter))
                {
                    filtered.Add(data);
                }
            }

            if (filtered.Count == 0 || filtered.Count == original.Count)
            {
                return;
            }

            __state = original;
            manager.unsortedCharacterData = filtered;
            Plugin.Logger.LogInfo($"Character selection: showing {filtered.Count} of {original.Count} cards");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Character selection filter failed: {error}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CharacterMenu.TryInit))]
    private static void AfterTryInit(Il2CppSystem.Collections.Generic.List<CharacterData> __state)
    {
        if (__state == null)
        {
            return;
        }

        try
        {
            var manager = DataManager.Instance;
            if (manager != null)
            {
                manager.unsortedCharacterData = __state;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Character selection restore failed: {error}");
        }
    }
}

/// <summary>
/// The character card, cut down to our content. The card is filled by
/// <c>CharacterInfoUI.OnCharacterSelected</c>, so this one postfix does both:
///
/// V1 — drops Megabonk's <c>Rank xxx / Runs xxx</c> line and the
/// <c>LevelProgress</c> bar under it (both values come from
/// <c>ProgressionSaveFile.GetCharacterProgression</c>, which is the native
/// slot's lifetime save, not our run, so they say nothing about a Wraeclast
/// character). The bar's field is only the blue fill, the dark track is its
/// parent, so the whole row goes. Hidden through
/// <see cref="MainMenuPatches.HideObject"/>.
///
/// V2 — the passive block is the only place the card shows a description
/// (the prefab's <c>T_Description</c> row is inactive), and it currently
/// states the native passive ("Blood Magic"). It now carries the PoE class
/// role and blurb from <c>classes.json</c>, and the weapon block names the
/// class's starter unique instead of the Megabonk weapon the slot shipped
/// with. Same switch as the menu cleanup: <c>[UI] CleanMenu</c>.
///
/// V9 — the two blocks speak the run's own content instead: the first names the
/// class skill and what it does (there is no native weapon to name any more,
/// and the slot's icon is the skill's), the second names the class passive and
/// its numbers. The native passive's own icon is hidden with its effect: the
/// passive art belongs to Megabonk's ability, which <c>ClassSwap</c> switched
/// off.
/// </summary>
[HarmonyPatch(typeof(CharacterInfoUI))]
internal static class CharacterCardPatches
{
    private static bool logged;
    private static bool loggedText;

    [HarmonyPostfix]
    [HarmonyPatch(nameof(CharacterInfoUI.OnCharacterSelected))]
    private static void AfterCharacterSelected(CharacterInfoUI __instance, MyButtonCharacter btn)
    {
        if (!Plugin.CleanMainMenu.Value || __instance == null)
        {
            return;
        }

        try
        {
            WriteClassText(__instance, btn);

            var hidden = 0;
            hidden += MainMenuPatches.HideObject(__instance.t_rank) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(__instance.t_runs) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(__instance.i_rankFrame) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(__instance.i_rankIcon) ? 1 : 0;
            hidden += MainMenuPatches.HideObject(__instance.i_runs) ? 1 : 0;
            hidden += HideLevelProgress(__instance.progressBar) ? 1 : 0;

            if (hidden > 0 && !logged)
            {
                logged = true;
                Plugin.Logger.LogInfo(
                    $"character card: Rank/Runs hidden ({hidden} part(s))");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"character card: hiding Rank/Runs failed ({error.Message})");
        }
    }

    /// <summary>V2 + V9: the two blocks answer from the class sheet. The slot is
    /// read from the selected button's own <c>CharacterData</c>, so the text
    /// follows the card the player clicked (the postfix's <c>btn</c>). An
    /// unmapped slot keeps the native text.</summary>
    private static void WriteClassText(CharacterInfoUI card, MyButtonCharacter btn)
    {
        var data = btn == null ? null : btn.characterData;
        if (data == null || !ClassSwap.TryGetDefinition(data.eCharacter, out var definition))
        {
            return;
        }

        var skill = SkillDatabase.TryGet(definition.SkillId, out var found) ? found : null;
        if (card.t_weaponName != null)
        {
            card.t_weaponName.text = skill == null ? string.Empty : skill.Name ?? string.Empty;
        }

        if (card.t_weaponDesc != null)
        {
            card.t_weaponDesc.text = skill == null ? string.Empty : skill.Description ?? string.Empty;
        }

        if (card.i_weapon != null && skill != null)
        {
            var icon = PoeAssets.GetSkillIcon(skill.Id);
            if (icon != null)
            {
                card.i_weapon.texture = icon;
            }
        }

        var passive = definition.Passive;
        if (card.t_passiveName != null)
        {
            card.t_passiveName.text = passive != null && !string.IsNullOrEmpty(passive.Name)
                ? passive.Name
                : definition.Title;
        }

        if (card.t_passiveDesc != null)
        {
            card.t_passiveDesc.text = passive != null && !string.IsNullOrEmpty(passive.Text)
                ? passive.Text
                : definition.Flavour ?? string.Empty;
        }

        // The passive icon is the native ability's art (Flex, Blood Magic…), which
        // no longer exists in this build: hide it rather than label our passive
        // with someone else's picture.
        if (card.i_passive != null)
        {
            MainMenuPatches.HideObject(card.i_passive);
        }

        if (!loggedText)
        {
            loggedText = true;
            Plugin.Logger.LogInfo(
                $"character card: {definition.Name} shows skill {definition.SkillId} and " +
                $"passive {definition.Passive?.Name ?? "none"} (native passive off)");
        }
    }

    /// <summary><c>progressBar</c> is only the blue fill; the dark track behind
    /// it is its <c>LevelProgress</c> row (a 5 px line under the Rank/Runs
    /// texts), so the whole row goes, not just the fill.</summary>
    private static bool HideLevelProgress(UnityEngine.UI.RawImage fill)
    {
        try
        {
            var node = fill == null ? null : fill.transform;
            while (node != null && node.name != "LevelProgress")
            {
                node = node.parent;
            }

            return node != null && MainMenuPatches.HideObject(node.gameObject);
        }
        catch
        {
            return false;
        }
    }
}
