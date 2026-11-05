using System;
using HarmonyLib;
using UnityEngine;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// In-run body swap. The Megabonk body is not destroyed: its renderer is hidden
/// and our skinned PoE model is parented to the same transform, so the animator,
/// collider, hat socket, damage flash and every gameplay system keep their
/// original objects while <see cref="PoeCharacters"/> animates the PoE skeleton
/// from the clips baked in the GLB.
///
/// Scale comes from the hidden body's own world bounds, not from a guessed
/// constant.
/// </summary>
[HarmonyPatch(typeof(PlayerRenderer))]
internal static class PlayerRendererPatches
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerRenderer.SetCharacter))]
    private static void AfterSetCharacter(PlayerRenderer __instance, CharacterData characterData)
    {
        if (characterData == null || !ClassSwap.TryGetDefinition(characterData.eCharacter, out var definition))
        {
            return;
        }

        try
        {
            var body = __instance.renderer;
            if (body != null)
            {
                body.enabled = false;
            }

            var subRenderers = __instance.subRenderers;
            if (subRenderers != null)
            {
                for (var index = 0; index < subRenderers.Count; index++)
                {
                    var renderer = subRenderers[index];
                    if (renderer != null)
                    {
                        renderer.enabled = false;
                    }
                }
            }

            PoeCharacters.Attach(__instance, definition, characterData);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"{definition.Name}: model swap failed: {error}");
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(nameof(PlayerRenderer.SetHat))]
    private static void AfterSetHat(PlayerRenderer __instance)
    {
        try
        {
            PoeCharacters.HideHostMeshesAfterHat(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"hiding the host hat failed: {error.Message}");
        }
    }
}
