using System;
using System.Collections.Generic;
using System.Linq;

namespace Wraeclast;

/// <summary>
/// Which Megabonk <c>ECharacter</c> slot carries which Path of Exile class.
///
/// The enum is compiled into the IL2CPP assembly, so members cannot be added at
/// runtime: we repurpose existing slots and hide the rest. Ids on our side are
/// the class names, never the enum values. Keep in sync with mod/manifest.json.
/// </summary>
internal static class CharacterSlots
{
    internal sealed class ClassSlot
    {
        internal ClassSlot(string slot, string className)
        {
            Slot = slot;
            ClassName = className;
        }

        internal string Slot { get; }
        internal string ClassName { get; }
    }

    internal static readonly IReadOnlyList<ClassSlot> All = new[]
    {
        new ClassSlot("Megachad", "Marauder"),
        new ClassSlot("Robinette", "Ranger"),
        new ClassSlot("Cl4nk", "Witch"),
        new ClassSlot("Athena", "Templar"),
        new ClassSlot("Vlad", "Shadow"),
        new ClassSlot("Noelle", "Duelist"),
        new ClassSlot("Birdo", "Scion"),
    };

    /// <summary>Poe class for a slot, or null when the slot stays Megabonk's.</summary>
    internal static string ClassFor(string slot) =>
        All.FirstOrDefault(entry => entry.Slot == slot)?.ClassName;

    /// <summary>Parse a slot name into the game enum; false when it is not one.</summary>
    internal static bool TryParse(string slot, out ECharacter character)
    {
        character = default;
        return Enum.TryParse(slot, ignoreCase: false, out character) &&
               Enum.IsDefined(typeof(ECharacter), character);
    }

    internal static string Describe() =>
        string.Join(", ", All.Select(entry => $"{entry.Slot}->{entry.ClassName}"));
}
