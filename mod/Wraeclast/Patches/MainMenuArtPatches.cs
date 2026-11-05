using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Wraeclast.Data;

namespace Wraeclast.Patches;

/// <summary>
/// V6: the main menu's title becomes the Path of Exile logo. **Nothing else on
/// the menu is touched** — the buttons keep Megabonk's own plates, labels,
/// clicks and hover behaviour. Re-skinning the buttons was tried twice
/// (0.9.60 with <c>Sprite.Create</c>, 0.9.62 with sprite fallbacks, 0.9.63 with
/// nine <c>RawImage</c> pieces) and cancelled by the operator: the sprite path
/// is stripped from the build and the plate path cost the other buttons. Do not
/// retry it here.
///
/// <c>MainMenu</c> has no logo field, so the node is resolved live: the child
/// named <c>Menu</c> whose parent is <c>Tabs</c>, then its <c>Logo</c> child
/// (the level1 scene dump puts the title there). Whatever graphic the node
/// carries (an <c>Image</c> or the <c>RawImage</c> this build actually uses) is
/// switched off, and the client's logo (<c>assets/ui/poe-logo.png</c>,
/// <c>Art/2DArt/Logos/POELogo.dds</c>) is drawn over it as a <c>RawImage</c>
/// child at the slot's own height and the texture's aspect. The node, its rect
/// and its fun-fact child are kept.
///
/// The discovery half is <c>[Debug] MenuProbeKeys</c>: F4 dumps the menu
/// hierarchy (path, rect, <c>Image.sprite</c>/type, <c>RawImage.texture</c>, TMP
/// text). Gated by <c>[UI] PoeMenuArt</c>.
/// </summary>
[HarmonyPatch]
internal static class MainMenuArtPatches
{
    private static bool logged;
    private static readonly HashSet<string> Reported = new HashSet<string>(StringComparer.Ordinal);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainMenu), nameof(MainMenu.Start))]
    private static void AfterMenuStart(MainMenu __instance)
    {
        Apply(__instance);
    }

    /// <summary>F4 dumps the menu tree; the key is polled from the always-on
    /// <c>UiManager</c> because the main menu has no <c>Update</c> of its own
    /// and the player does not exist yet.</summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UiManager), nameof(UiManager.Update))]
    private static void BeforeUiUpdate()
    {
        if (!Plugin.MenuProbeKeys.Value || !Input.GetKeyDown(KeyCode.F4))
        {
            return;
        }

        Dump();
    }

    private static void Apply(MainMenu menu)
    {
        if (!Plugin.PoeMenuArt.Value || menu == null)
        {
            return;
        }

        try
        {
            var menuTab = FindMenuTab(menu.transform);
            if (menuTab == null)
            {
                Warn("no Tabs/Menu node under MainMenu, the title is unchanged");
                return;
            }

            var logo = menuTab.Find("Logo");
            var texture = PoeAssets.GetMenuLogo();
            var swapped = SwapLogo(logo, texture);

            if (!logged)
            {
                logged = true;
                Plugin.Logger.LogInfo(
                    $"menu art: {(swapped ? "PoE logo" : "no logo graphic")} on " +
                    $"{(logo == null ? "a missing Logo node" : "Tabs/Menu/Logo")}, " +
                    $"texture {(texture == null ? "missing" : texture.width + "x" + texture.height)}; " +
                    "buttons untouched");
            }
        }
        catch (Exception error)
        {
            Warn($"the PoE logo failed: {error.Message}");
        }
    }

    /// <summary>The title node is a plain child of the menu tab (no
    /// <c>MainMenu</c> field), so it is found by name: the node named
    /// <c>Menu</c> whose parent is <c>Tabs</c>.</summary>
    private static Transform FindMenuTab(Transform root)
    {
        Transform fallback = null;
        var all = root.GetComponentsInChildren<Transform>(true);
        for (var index = 0; index < all.Length; index++)
        {
            var node = all[index];
            if (node == null || !string.Equals(node.name, "Menu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parent = node.parent;
            if (parent != null && string.Equals(parent.name, "Tabs", StringComparison.OrdinalIgnoreCase))
            {
                return node;
            }

            fallback ??= node;
        }

        return fallback;
    }

    /// <summary>Replaces the Megabonk title. The node is kept (its rect, its
    /// fun-fact child and its place in the canvas), only its own graphic is
    /// switched off and the PoE logo is drawn over it at the slot's height.</summary>
    private static bool SwapLogo(Transform logo, Texture2D texture)
    {
        if (logo == null || texture == null)
        {
            return false;
        }

        if (logo.Find("WraeclastLogo") != null)
        {
            return true;
        }

        HideNativeGraphic(logo.GetComponent<Image>());
        HideNativeGraphic(logo.GetComponent<RawImage>());
        var images = logo.GetComponentsInChildren<Image>(true);
        for (var index = 0; index < images.Length; index++)
        {
            HideNativeGraphic(images[index]);
        }

        var raws = logo.GetComponentsInChildren<RawImage>(true);
        for (var index = 0; index < raws.Length; index++)
        {
            HideNativeGraphic(raws[index]);
        }

        // RawImage is a Graphic, so adding it gives the holder its RectTransform
        // (Graphic has RequireComponent(RectTransform)); building the GameObject
        // from a Type[] is not available through this interop binding.
        var holder = new GameObject("WraeclastLogo");
        holder.transform.SetParent(logo, false);

        var image = holder.AddComponent<RawImage>();
        image.texture = texture;
        image.raycastTarget = false;

        var source = logo.GetComponent<RectTransform>();
        var height = source != null && source.rect.height > 1f ? source.rect.height : 364f;
        var width = height * texture.width / Mathf.Max(1f, texture.height);

        var rect = holder.GetComponent<RectTransform>();
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = Vector2.zero;
        rect.sizeDelta = new Vector2(width, height);
        return true;
    }

    private static void HideNativeGraphic(Graphic graphic)
    {
        if (graphic != null)
        {
            graphic.enabled = false;
        }
    }

    /// <summary>Discovery: one line per node under the live main menu, with the
    /// graphic each one carries. This is what pinned <c>Tabs/Menu/Logo</c>.</summary>
    private static void Dump()
    {
        try
        {
            var menu = UnityEngine.Object.FindObjectOfType<MainMenu>();
            if (menu == null)
            {
                Plugin.Logger.LogWarning("menu probe: no MainMenu in the scene");
                return;
            }

            var root = menu.transform;
            var all = root.GetComponentsInChildren<Transform>(true);
            Plugin.Logger.LogInfo($"menu probe: {all.Length} node(s) under {root.name}");

            foreach (var node in all)
            {
                if (node == null)
                {
                    continue;
                }

                var line = new StringBuilder();
                line.Append("menu tree: ").Append(PathOf(node, root));
                line.Append(node.gameObject.activeInHierarchy ? " [on]" : " [off]");

                var rect = node.GetComponent<RectTransform>();
                if (rect != null)
                {
                    line.Append($" {rect.rect.width:0}x{rect.rect.height:0}");
                    line.Append($" @({rect.anchoredPosition.x:0},{rect.anchoredPosition.y:0})");
                }

                var image = node.GetComponent<Image>();
                if (image != null)
                {
                    var sprite = image.sprite;
                    line.Append($" image={(sprite == null ? "-" : sprite.name)}:{image.type}");
                }

                var raw = node.GetComponent<RawImage>();
                if (raw != null)
                {
                    var texture = raw.texture;
                    line.Append($" raw={(texture == null ? "-" : texture.name)}");
                }

                var text = node.GetComponent<TextMeshProUGUI>();
                if (text != null)
                {
                    var value = text.text ?? string.Empty;
                    if (value.Length > 48)
                    {
                        value = value.Substring(0, 48);
                    }

                    line.Append($" text=\"{value}\"");
                }

                var button = node.GetComponent<Button>();
                if (button != null)
                {
                    var target = button.targetGraphic;
                    line.Append($" button={button.transition}->{(target == null ? "-" : target.name)}");
                }

                Plugin.Logger.LogInfo(line.ToString());
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"menu probe failed: {error.Message}");
        }
    }

    private static string PathOf(Transform node, Transform root)
    {
        var names = new List<string>();
        for (var current = node; current != null && current != root; current = current.parent)
        {
            names.Add(current.name);
        }

        names.Reverse();
        return string.Join("/", names);
    }

    /// <summary>Each distinct problem is logged once.</summary>
    private static void Warn(string message)
    {
        if (!Reported.Add(message))
        {
            return;
        }

        Plugin.Logger.LogWarning($"menu art: {message}");
    }
}
