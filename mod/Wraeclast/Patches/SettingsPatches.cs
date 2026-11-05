using System;
using Assets.Scripts.Settings___Saves.SaveFiles;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace Wraeclast.Patches;

/// <summary>
/// V12: the mod's own switch inside the game's Settings window.
///
/// The screen is data-driven: <c>Settings.UpdateSettings</c> reflects over each
/// <c>CFSettings</c> object and instantiates one of the serialized row prefabs
/// per field. A mod cannot add a field to that class, so the row is built the
/// same way for the "Other" tab directly: clone <c>Settings.enumPrefab</c>
/// (the OFF/ON row every boolean setting uses) under <c>Settings.otherContent</c>
/// and point it at a name that no <c>CFSettings</c> owns.
///
/// The row's <c>saveAction</c> is ours, so <c>BetterSetting.SaveValue</c> never
/// touches the game's save file: the click writes the BepInEx entry
/// (<c>[Skills] RangeIndicator</c>) and saves it. The entry remains the single
/// source of truth — the row is only a second surface for it, which is why the
/// config file and the in-game switch can never disagree.
/// </summary>
[HarmonyPatch]
internal static class SettingsPatches
{
    /// <summary>The pseudo setting name. It is deliberately not a field of any
    /// <c>CFSettings</c>: <c>BetterSetting.UpdateValue</c> asks the settings
    /// object for the field by name, gets null and leaves our value alone.</summary>
    internal const string RangeSetting = "wraeclast_range_indicator";

    private const string RowName = "WraeclastRangeIndicatorSetting";
    private const string Label = "Range Indicator";
    private const string Description = "Show the class skill's reach as a blue circle on the ground.";

    private static BetterSetting rangeRow;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Settings), "UpdateSettings")]
    private static void AfterUpdateSettings(Settings __instance)
    {
        try
        {
            AddRangeRow(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"settings: range indicator row failed ({error.Message})");
        }
    }

    /// <summary>Second chance: <c>Start</c> is the method that calls
    /// <c>UpdateSettings</c>. If that private call is ever inlined (and the
    /// patch above stops firing) this hook still installs the row; the guard in
    /// <see cref="AddRangeRow"/> makes the pair idempotent.</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(Settings), "Start")]
    private static void AfterSettingsStart(Settings __instance)
    {
        try
        {
            AddRangeRow(__instance);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"settings: range indicator row failed ({error.Message})");
        }
    }

    /// <summary><c>Start</c> runs after the row is configured and repaints the
    /// label from the setting name, so the display text is fixed here (and on
    /// a locale change, which runs the same repaint).</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BetterSetting), "Start")]
    private static void AfterRowStart(BetterSetting __instance)
    {
        FixLabel(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BetterSetting), "RefreshLanguage")]
    private static void AfterRowLanguage(BetterSetting __instance)
    {
        FixLabel(__instance);
    }

    private static void FixLabel(BetterSetting setting)
    {
        try
        {
            if (setting == null ||
                !string.Equals(setting._settingName, RangeSetting, StringComparison.Ordinal))
            {
                return;
            }

            if (setting.settingName != null)
            {
                setting.settingName.text = Label;
            }
        }
        catch (Exception)
        {
            // A label that cannot be repainted is cosmetic only.
        }
    }

    private static void AddRangeRow(Settings settings)
    {
        if (settings == null)
        {
            return;
        }

        var parent = settings.otherContent;
        if (parent == null)
        {
            Plugin.Logger.LogWarning("settings: the Other tab has no content parent; the range row stays out");
            return;
        }

        if (rangeRow != null && rangeRow.gameObject != null)
        {
            return;
        }

        if (parent.Find(RowName) != null)
        {
            return;
        }

        var prefab = settings.enumPrefab;
        if (prefab == null)
        {
            Plugin.Logger.LogWarning("settings: no enum row prefab; the range row stays out");
            return;
        }

        var clone = UnityEngine.Object.Instantiate(prefab, parent);
        clone.name = RowName;

        var row = clone.GetComponent<EnumSetting>();
        if (row == null)
        {
            Plugin.Logger.LogWarning("settings: the enum row prefab has no EnumSetting");
            UnityEngine.Object.Destroy(clone);
            return;
        }

        // The same two option strings a native boolean row uses, so the row
        // reads OFF/ON in the player's language without a new localization key.
        var options = Options();
        row.settingType = (int)SettingType.Enum;
        row.options = options;
        row._settingName = RangeSetting;
        row._settingValue = Plugin.RangeIndicator.Value ? 1 : 0;
        row.settings = settings;
        row.cfSettings = null;
        row.description = Description;

        System.Action<string, Il2CppSystem.Object, CFSettings> save = OnSaved;
        row.saveAction = save;

        // Draws the current value (and calls SaveValue with the same value).
        row.UpdateValue(0);
        FixLabel(row);

        rangeRow = row;
        Plugin.Logger.LogInfo(
            $"settings: Range Indicator switch added to the Other tab ({options.Length} value(s))");
    }

    private static Il2CppStringArray Options()
    {
        try
        {
            var native = ConfigSettingsUtility.GetSettingValues("camera_shake");
            if (native != null && native.Length >= 2)
            {
                return native;
            }
        }
        catch (Exception)
        {
            // Fall back to plain text below.
        }

        return new Il2CppStringArray(new[] { "OFF", "ON" });
    }

    private static void OnSaved(string name, Il2CppSystem.Object value, CFSettings cfSettings)
    {
        if (!string.Equals(name, RangeSetting, StringComparison.Ordinal))
        {
            return;
        }

        var on = 1;
        try
        {
            on = value.Unbox<int>();
        }
        catch (Exception)
        {
            // An unreadable value keeps the switch on rather than failing the click.
        }

        Plugin.RangeIndicator.Value = on != 0;
        Plugin.RangeIndicator.ConfigFile.Save();
        Plugin.Logger.LogInfo($"settings: range indicator {(on != 0 ? "on" : "off")}");
    }
}
