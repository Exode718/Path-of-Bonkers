using System;
using System.Collections.Generic;
using Assets.Scripts._Data.MapsAndStages;
using Assets.Scripts.Game.Other;
using Assets.Scripts.Managers;
using HarmonyLib;
using UnityEngine;

namespace Wraeclast.Run;

/// <summary>
/// M1 — Forest only. Megabonk ships five maps (<c>DataManager.maps</c>) and
/// <c>MapData.isEnabled</c> is the flag that already hides Hell and Dungeon, so
/// the conversion closes the other two the same way instead of deleting assets:
/// Desert (<see cref="EMap.Desert"/>) and Graveyard (<see cref="EMap.Graveyard"/>)
/// read <c>isEnabled = false</c> at <c>DataManager.Load</c>, and Forest stays
/// the only map a run can start on. Forest is <c>mapType</c> 0 (tiles), Desert
/// 1 (mesh), which is why the later ground pass targets Forest only.
///
/// Two belts guard the run itself, because a saved selection can still name a
/// map we closed:
/// - <c>MapController.StartNewMap</c> is a prefix: whatever <c>RunConfig</c> the
///   caller built, a closed map is rewritten to Forest before the game reads it.
/// - <c>MapSelectionUi.Init</c> is a postfix: a closed entry cannot be selected
///   (<c>MapEntry.Set</c> turns its button off), the entry row is hidden, and a
///   selection that names a closed map falls back to the Forest button so the
///   window and the run agree.
///
/// <c>[Run] ForestOnly</c> is the switch; off leaves Megabonk's map list alone.
/// </summary>
[HarmonyPatch]
internal static class ForestOnly
{
    /// <summary>Forest's <c>EMap</c> value. The two closed maps are Desert (2)
    /// and Graveyard (8); every map that is not Forest is closed, so the rule
    /// does not depend on those two numbers.</summary>
    private const int ForestMap = (int)EMap.Forest;

    private static bool loggedEntries;
    private static bool loggedSelection;
    private static bool loggedRun;

    internal static bool Active => Plugin.ForestOnlyMaps.Value;

    /// <summary>
    /// Runs from <c>DataManager.Load</c>'s postfix, before any screen reads the
    /// list. The shipped map list is logged with its real asset names, so a run
    /// log states which maps exist and which were closed.
    /// </summary>
    internal static void Apply(DataManager manager)
    {
        if (!Active)
        {
            Plugin.Logger.LogInfo("forest only off: every map the game ships stays selectable");
            return;
        }

        if (manager == null)
        {
            return;
        }

        try
        {
            var maps = manager.maps;
            if (maps == null)
            {
                Plugin.Logger.LogWarning("forest only: DataManager.maps is null, the map list is untouched");
                return;
            }

            var closed = 0;
            var names = new List<string>();
            for (var index = 0; index < maps.Count; index++)
            {
                var map = maps[index];
                if (map == null)
                {
                    continue;
                }

                if ((int)map.eMap == ForestMap)
                {
                    continue;
                }

                if (map.isEnabled)
                {
                    map.isEnabled = false;
                    closed++;
                }

                names.Add(map.name);
            }

            Plugin.Logger.LogInfo(
                $"forest only: {closed} map(s) closed, Forest is the only map " +
                $"({names.Count} non-Forest: {string.Join(", ", names)})");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogError($"forest only: closing the other maps failed: {error}");
        }
    }

    /// <summary>
    /// The run gate: the caller's <c>RunConfig</c> is rebuilt onto Forest when
    /// it names a closed map (or none), before <c>MapController</c> reads it.
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MapController), nameof(MapController.StartNewMap))]
    private static void BeforeStartNewMap(RunConfig newRunConfig)
    {
        if (!Active || newRunConfig == null)
        {
            return;
        }

        try
        {
            var map = newRunConfig.mapData;
            if (map != null && map.isEnabled && (int)map.eMap == ForestMap)
            {
                return;
            }

            var forest = ResolveForest();
            if (forest == null)
            {
                Plugin.Logger.LogWarning("forest only: no Forest map data to fall back to, the run config is untouched");
                return;
            }

            var previous = map == null ? "none" : map.name;
            newRunConfig.mapData = forest;
            LogOnce(ref loggedRun, $"forest only: run config {previous} -> {forest.name}");
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"forest only: rewriting the run config failed: {error.Message}");
        }
    }

    /// <summary>
    /// The map button keeps its slot in the selection group (so the group's own
    /// indices stay valid) but can no longer be selected.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapEntry), nameof(MapEntry.Set))]
    private static void AfterEntrySet(MapEntry __instance, MapData mapData)
    {
        if (!Active || __instance == null || mapData == null || mapData.isEnabled)
        {
            return;
        }

        try
        {
            var button = __instance.buttonScript;
            if (button == null)
            {
                return;
            }

            if (button.canSelect)
            {
                button.canSelect = false;
                LogOnce(ref loggedEntries, $"forest only: map entry {mapData.name} is not selectable");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"forest only: turning a map entry off failed: {error.Message}");
        }
    }

    /// <summary>
    /// The window opening: a selection that names a closed map falls back to
    /// Forest, the closed rows are hidden, and the Forest button is the one the
    /// group holds, so the window and the run cannot disagree.
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(MapSelectionUi), nameof(MapSelectionUi.Init))]
    private static void AfterSelectionInit(MapSelectionUi __instance)
    {
        if (!Active || __instance == null)
        {
            return;
        }

        try
        {
            var forest = ResolveForest();
            if (forest == null)
            {
                Plugin.Logger.LogWarning("forest only: no Forest map data, the map window is untouched");
                return;
            }

            var config = __instance.runConfig;
            if (config != null && !IsForest(config.mapData))
            {
                var previous = config.mapData == null ? "none" : config.mapData.name;
                config.mapData = forest;
                __instance.defaultMapData = forest;
                LogOnce(ref loggedSelection, $"forest only: the run config starts on {forest.name} (saved {previous})");
            }

            var window = __instance.mapButtonsWindow;
            var root = window == null ? __instance.gameObject : window.gameObject;
            if (root != null)
            {
                foreach (var entry in root.GetComponentsInChildren<MapEntry>(true))
                {
                    if (entry == null || entry.mapData == null || entry.mapData.isEnabled)
                    {
                        continue;
                    }

                    if (entry.gameObject.activeSelf)
                    {
                        entry.gameObject.SetActive(false);
                    }
                }
            }

            SelectForest(__instance.mapSelectionGroup);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"forest only: the map window belt failed: {error.Message}");
        }
    }

    /// <summary>Selects the Forest button in the map group, if the group can be
    /// read. The button is resolved through its own <see cref="MapEntry"/>, so
    /// no index is guessed from the map order.</summary>
    private static void SelectForest(SelectionGroupToggleSingle group)
    {
        if (group == null)
        {
            return;
        }

        var buttons = group.buttons;
        if (buttons == null)
        {
            return;
        }

        for (var index = 0; index < buttons.Count; index++)
        {
            var button = buttons[index];
            if (button == null)
            {
                continue;
            }

            var entry = button.GetComponentInParent<MapEntry>();
            if (entry == null || !IsForest(entry.mapData))
            {
                continue;
            }

            if (group.GetSelectedIndex() != index)
            {
                group.ForceSelect(index);
            }

            return;
        }
    }

    private static bool IsForest(MapData map) =>
        map != null && (int)map.eMap == ForestMap;

    /// <summary>Forest's live <c>MapData</c>, read from the game's own list.</summary>
    private static MapData ResolveForest()
    {
        var manager = DataManager.Instance;
        if (manager == null)
        {
            return null;
        }

        var maps = manager.maps;
        if (maps == null)
        {
            return null;
        }

        MapData fallback = null;
        for (var index = 0; index < maps.Count; index++)
        {
            var map = maps[index];
            if (!IsForest(map))
            {
                continue;
            }

            if (map.isEnabled)
            {
                return map;
            }

            fallback ??= map;
        }

        return fallback;
    }

    private static void LogOnce(ref bool flag, string message)
    {
        if (flag)
        {
            return;
        }

        flag = true;
        Plugin.Logger.LogInfo(message);
    }
}
