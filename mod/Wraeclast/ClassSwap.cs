using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.AbilitiesPassive;
using Wraeclast.Data;

namespace Wraeclast;

/// <summary>
/// The runtime class swap: each mapped <c>ECharacter</c> slot keeps its enum
/// value, prefab, audio and passive, and gets our name, blurb, icon and stat
/// modifiers written onto its <c>CharacterData</c>. Nothing is added to the
/// enum and no asset is modified on disk; a restart restores the base game.
/// </summary>
internal static class ClassSwap
{
    private static readonly Dictionary<int, ClassDefinition> BySlot =
        new Dictionary<int, ClassDefinition>();

    internal static bool IsMapped(ECharacter character) => BySlot.ContainsKey((int)character);

    internal static bool TryGetDefinition(ECharacter character, out ClassDefinition definition) =>
        BySlot.TryGetValue((int)character, out definition);

    internal static void Apply(DataManager manager)
    {
        if (manager == null)
        {
            return;
        }

        BySlot.Clear();
        var applied = 0;

        foreach (var slot in CharacterSlots.All)
        {
            if (!CharacterSlots.TryParse(slot.Slot, out var character))
            {
                Plugin.Logger.LogWarning($"Slot {slot.Slot} is not an ECharacter member in this build");
                continue;
            }

            if (!ClassDatabase.TryGet(slot.ClassName, out var definition))
            {
                Plugin.Logger.LogWarning($"Class {slot.ClassName} is missing from data/classes.json");
                continue;
            }

            var data = manager.GetCharacterData(character);
            if (data == null)
            {
                Plugin.Logger.LogWarning($"DataManager has no CharacterData for {slot.Slot}");
                continue;
            }

            data.name = definition.Name;
            data.icon = PoeAssets.GetIcon(definition.Id);
            ClassStats.Apply(data, definition);
            DisableNativePassive(data);

            BySlot[(int)character] = definition;
            applied++;

            Plugin.Logger.LogInfo(
                $"slot {slot.Slot} ({(int)character}) -> {definition.Name} \"{definition.Title}\": " +
                $"stats [{ClassStats.Describe(data)}], " +
                $"passive {definition.Passive?.Name ?? "none"} (native off)");
        }

        Plugin.Logger.LogInfo($"Class swap active: {applied}/{CharacterSlots.All.Count} slot(s)");
    }

    /// <summary>
    /// Megabonk's slot passive (Flex, Vampire, …) is code-side: the game builds
    /// the ability from <c>PassiveData.ePassive</c> when the run's
    /// <c>PlayerInventory</c> is constructed, so answering <c>None</c> gives the
    /// neutral <c>PassiveAbilityMissing</c> and the base passive stops ticking.
    /// Our own passive is stat-based and rides the class's <c>statModifiers</c>
    /// (see <see cref="ClassStats.Apply"/>), so nothing is left to patch.
    /// </summary>
    private static void DisableNativePassive(CharacterData data)
    {
        try
        {
            var passive = data.passive;
            if (passive != null)
            {
                passive.ePassive = EPassive.None;
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"{data.name}: the native passive is unreadable ({error.Message})");
        }
    }
}
