using System;
using System.Collections.Generic;
using Assets.Scripts.Inventory__Items__Pickups.Items;

namespace Wraeclast.Data;

/// <summary>
/// The relic sheet against the live item assets. Item effects live in code
/// (<c>ItemFactory</c> dispatches <c>EItem</c> to an <c>ItemImplementations.*</c>
/// class that writes its own <c>ItemBase.SetStat</c> modifiers), so a slot can
/// only ever carry the donor's native effect: the relic text in
/// <c>mod/data/items.json</c> states that effect and the numbers the donor
/// class writes.
///
/// This pass is the load-time audit of the mapping *and* the source of the
/// rename: it resolves every mapped slot on the game's own registry and checks
/// that the slot still answers with the donor the sheet was written against
/// (enum member and native display name), then logs the native card, the
/// donor's <c>SetStat</c> lines and our text:
///
///   relic Battery -> Battery: native "Increase {stat1} by {value1}"
///   [AttackSpeed Flat amount * 0.08] (ok), "+8% Attack Speed per stack."
///
/// A <c>MISMATCH</c> means the game retuned or repurposed that slot: the text
/// would then promise an effect the donor no longer has, so it is reported
/// instead of shipped silently.
/// </summary>
internal static class ItemSwap
{
    /// <summary>
    /// Slots the card patches answer for: one per mapped relic whose slot still
    /// holds the documented donor. A MISMATCH stays out, so a retuned slot keeps
    /// Megabonk's own card instead of promising an effect it no longer has.
    /// </summary>
    private static readonly Dictionary<int, ItemDefinition> BySlot =
        new Dictionary<int, ItemDefinition>();

    internal static bool TryGet(EItem item, out ItemDefinition definition) =>
        BySlot.TryGetValue((int)item, out definition);

    /// <summary>Sheet rarity name -> the card's own enum, for the re-tier.</summary>
    private static EItemRarity RarityOf(string rarity) => rarity switch
    {
        "rare" => EItemRarity.Rare,
        "epic" => EItemRarity.Epic,
        "legendary" => EItemRarity.Legendary,
        _ => EItemRarity.Common
    };

    internal static void Prime(DataManager manager)
    {
        BySlot.Clear();
        if (manager == null)
        {
            return;
        }

        var audited = 0;
        foreach (var definition in ItemDatabase.All)
        {
            if (!Enum.TryParse(definition.Slot, ignoreCase: false, out EItem slot) ||
                !Enum.IsDefined(typeof(EItem), slot))
            {
                Plugin.Logger.LogWarning($"Relic {definition.Id}: slot {definition.Slot} is not in this build");
                continue;
            }

            ItemData data;
            try
            {
                data = manager.GetItem(slot);
            }
            catch (Exception error)
            {
                Plugin.Logger.LogWarning($"Relic {definition.Id}: the game's item table has no {slot} ({error.Message})");
                continue;
            }

            if (data == null)
            {
                Plugin.Logger.LogWarning($"Relic {definition.Id}: DataManager has no ItemData for {slot}");
                continue;
            }

            audited++;
            var matches = Audit(data, slot, definition, out var line);
            Plugin.Logger.LogInfo($"relic {slot} -> {definition.Name}: {line}");

            if (matches && Plugin.ReplaceItems.Value)
            {
                BySlot[(int)slot] = definition;

                // S5+: the card's own texture field too, so a screen that reads
                // `data.icon` directly sees the client unique art; the two
                // relics with no exported art keep the donor texture.
                var icon = PoeAssets.GetItemIcon(definition.IconId);
                if (icon != null)
                {
                    data.icon = icon;
                }

                // The frame and the drop roll are the card's own ItemData.rarity
                // (S5 made the table agree with the donor slot). An override in
                // the sheet (`rarity`) re-tiers a relic whose power did not match
                // its slot, so the table and the frame still agree.
                if (RewardTable.TryGet(definition.Id, out var entry))
                {
                    var reTiered = RarityOf(entry.Rarity);
                    if (data.rarity != reTiered)
                    {
                        data.rarity = reTiered;
                        Plugin.Logger.LogInfo(
                            $"relic {definition.Id}: rarity {reTiered} (sheet override, was the donor slot's)");
                    }
                }
            }
        }

        Plugin.Logger.LogInfo($"Relic map: {audited}/{ItemDatabase.All.Count} slot(s) audited");
        Plugin.Logger.LogInfo(Plugin.ReplaceItems.Value
            ? $"Item swap active: {BySlot.Count}/{ItemDatabase.All.Count} slot(s) (cards are PoE relics)"
            : "Item swap off: the base game's item cards stay");
    }

    /// <summary>
    /// "ok" means the slot still holds the donor the sheet documents; the text
    /// beside it is the card our relic will show. "MISMATCH" means the game
    /// answered a different slot and the sheet entry has to be re-authored.
    ///
    /// The identity checked is the enum, because that is what
    /// <c>ItemFactory.CreateItem</c> switches on to build the effect class. The
    /// *display* name cannot be compared: it is a Unity Localization entry, so
    /// it answers in the player's language ("Clé", "Bière", "Bouclier
    /// épineux") while the sheet records the English card the donor table
    /// documents. <c>data.name</c> is the language-independent asset name and is
    /// logged next to it for the eye.
    /// </summary>
    private static bool Audit(ItemData data, EItem slot, ItemDefinition definition, out string line)
    {
        var matches = false;
        var assetName = "?";

        try
        {
            assetName = data.name;
            matches = data.eItem == slot;
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"Relic {definition.Id}: native card unreadable ({error.Message})");
        }

        var native = definition.Native ?? "";
        var stats = string.IsNullOrEmpty(definition.NativeStats)
            ? "no SetStat writes"
            : definition.NativeStats;

        if (!matches)
        {
            Plugin.Logger.LogWarning(
                $"relic {definition.Id}: the game answered {assetName} for slot {slot}, which is not the " +
                $"donor the sheet documents (\"{definition.NativeName}\", native \"{native}\"); " +
                "the sheet entry has to be re-authored");
        }

        if (definition.EngineOwned)
        {
            // R4: the slot is only a container. The donor class is replaced by
            // ItemNoImplementation, so the engine's own stats and triggers are
            // the whole effect and the card states our numbers, not the donor's.
            line = $"native {assetName} ({(matches ? "ok" : "MISMATCH")}), engine " +
                   $"{definition.StatCount} stat(s) + {definition.TriggerCount} trigger(s), " +
                   $"\"{definition.Text}\"";
            return matches;
        }

        line = $"native {assetName} \"{native}\" [{stats}] ({(matches ? "ok" : "MISMATCH")}), \"{definition.Text}\"";
        return matches;
    }
}
