using System;
using System.Collections.Generic;
using Assets.Scripts._Data;
using Assets.Scripts._Data.Tomes;
using Assets.Scripts.Actors.Player;
using Assets.Scripts.Inventory__Items__Pickups;
using Assets.Scripts.Inventory__Items__Pickups.Interactables;
using Assets.Scripts.Inventory__Items__Pickups.Items;
using Assets.Scripts.Inventory__Items__Pickups.Stats;
using Assets.Scripts.Menu.Shop;
using UnityEngine;
using Wraeclast.Skills;
using Random = UnityEngine.Random;

namespace Wraeclast.Data;

/// <summary>
/// The one draw the reward table feeds (R5): the level-up draft, chests, the
/// shop and the boss reward all pick from <c>mod/data/rewards.json</c>, so
/// balancing is one file.
///
/// Rules (ours, stated once here):
///
/// - **Tier is a depth gate**: a draw at tier N may offer tier 1..N, so a
///   deeper chest can still hand a common and a shallow one never hands a
///   legendary.
/// - **The draft band** comes from the character level (1-9 / 10-19 / 20+),
///   the **chest tier** from the chest type, the **shop tier** from the rarity
///   the merchant rolled, the **boss tier** from the floor.
/// - **Weight** is <c>weight x rarityScale x (1 + luck)</c>; the scales are
///   <see cref="RewardTable.EffectiveWeight"/>. Luck is the game's own
///   <c>EStat.Luck</c>.
/// - A card is skipped when the run is below its <c>minLevel</c> or the player
///   already holds <c>maxStacks</c> of it (tome level / item amount), so the
///   pool never offers a card that would do nothing.
/// - Nothing is drawn twice in one draw (no duplicate ids).
///
/// The result is the sheet entry; <see cref="ResolveTome"/> and
/// <see cref="ResolveRelic"/> answer with the game object the caller needs.
/// </summary>
internal static class RewardPool
{
    /// <summary>Level bands for the draft: 1-9, 10-19, 20+.</summary>
    private const int BandSize = 10;

    /// <summary>The shape the last class-filter line named, so it is logged
    /// once per run instead of on every draw.</summary>
    private static string filteredShape;

    /// <summary>The tag whose relic filter was already named, so the
    /// "13 relics do nothing for this class" line prints once per run.</summary>
    private static string filteredTag;

    /// <summary>The level the level-up screen is drafting, set by
    /// <c>UpgradePicker.ShuffleUpgrades</c>. The inventory's own level can lag
    /// behind a multi-level XP burst (two skill drafts at 110/111), which is
    /// what this override fixes.</summary>
    private static int draftLevel;

    /// <summary>Fallback for a screen that repeats the old level: if a skill
    /// pick was completed since the last draft, the repeated level is the next
    /// one, not a redraw of the same draft.</summary>
    private static int lastDraftLevel;
    private static int lastDraftStacks;

    /// <summary>The character level at which the next skill palier unlocks. It
    /// advances once per skill draft, so the cadence is counted in levels the
    /// run actually reached: a big XP pickup — a pot, a level burst — can
    /// neither fire a palier early (the level-up screen's own counter can run
    /// ahead of the character level) nor fire the same palier twice.</summary>
    private static int nextSkillLevel = 1;

    internal static void SetDraftLevel(int level) => draftLevel = level;

    internal static void ClearDraftLevel() => draftLevel = 0;

    /// <summary>Run reset: the first palier is level 1 again and no draft state
    /// from the previous run survives.</summary>
    internal static void ResetSkillCadence()
    {
        nextSkillLevel = 1;
        LastDraftWasSkill = false;
        draftLevel = 0;
        lastDraftLevel = 0;
        lastDraftStacks = 0;
    }

    /// <summary>Clears the forced-pick flag. A sanctuary or merchant screen
    /// shuffles cards without going through <see cref="FillDraft"/>, so it must
    /// say so or the previous level-up's skill draft would keep its refresh,
    /// skip and banish buttons locked.</summary>
    internal static void ClearSkillDraft() => LastDraftWasSkill = false;

    internal static int DraftLevel()
    {
        var level = draftLevel;
        draftLevel = 0;
        return level > 0 ? level : Level();
    }

    internal static bool Active => Plugin.UseRewardTable.Value;

    /// <summary>The list the game returns from <c>GetRandomUpgrades</c> (three
    /// card slots).</summary>
    internal const int DraftCards = 3;

    /// <summary>The merchant's own count is three; it is mirrored here so a
    /// replaced list keeps the window's layout.</summary>
    internal const int ShopCards = 3;

    /// <summary>A mod's role in <c>mods.json</c>; the skill draft guarantees one
    /// <see cref="Offence"/> card so a full-DPS path is always available.</summary>
    internal const string Offence = "offence";

    internal static int TierForLevel(int level) =>
        Mathf.Clamp(1 + Mathf.Max(0, level) / BandSize, 1, 3);

    internal static int ChestTier(EChest chest) => chest switch
    {
        EChest.Corrupt => 3,
        EChest.Ghost => 2,
        EChest.FreeCrypt => 2,
        _ => 1
    };

    internal static int ShopTier(EItemRarity rarity) => rarity switch
    {
        EItemRarity.Legendary => 3,
        EItemRarity.Corrupted => 3,
        EItemRarity.Rare => 2,
        EItemRarity.Epic => 2,
        _ => 1
    };

    internal static int Level()
    {
        var inventory = Inventory();
        if (inventory == null)
        {
            return 1;
        }

        try
        {
            return Mathf.Max(1, inventory.GetCharacterLevel());
        }
        catch (Exception)
        {
            return 1;
        }
    }

    /// <summary>The game's own luck stat, a fraction (the tome adds 0.08).</summary>
    internal static float Luck()
    {
        var stats = Stats();
        if (stats == null)
        {
            return 0f;
        }

        try
        {
            return Mathf.Max(0f, stats.GetStat(EStat.Luck));
        }
        catch (Exception)
        {
            return 0f;
        }
    }

    internal static List<RewardEntry> DrawTomes(int count, int tier) =>
        Draw(RewardTable.Tome, count, tier, Level(), Luck());

    internal static List<RewardEntry> DrawRelics(int count, int tier, string rarity = null) =>
        Draw(RewardTable.Relic, count, tier, Level(), Luck(), null, rarity);

    /// <summary>
    /// The rarity a shady guy asks for, as a card rarity. The host's merchant
    /// comes in kinds and passes its kind's rarity to
    /// <c>InventoryUtility.GetRandomItemsShadyGuy(EItemRarity)</c> — a yellow hat
    /// shows yellow cards, a blue one shows blue — and our own draw used to
    /// ignore it, so every trader offered the whole table.
    ///
    /// 0.9.111: **`Common` is a filter too.** It used to return “no filter”, so
    /// the blue merchant sold rare and epic cards (“pas normal”). 0.9.114 stops
    /// folding `Epic` into `rare`: the table now carries the host's four card
    /// bands, so each merchant kind matches its own. Only `Corrupted` and
    /// `Quest` keep the unfiltered draw.
    /// </summary>
    internal static string ShadyRarity(EItemRarity rarity) => rarity switch
    {
        EItemRarity.Common => RewardTable.Common,
        EItemRarity.Rare => RewardTable.Rare,
        EItemRarity.Epic => RewardTable.Epic,
        EItemRarity.Legendary => RewardTable.Legendary,
        _ => null
    };

    /// <summary>
    /// The level-up draft. R11 routes it by level: at a **skill-upgrade level**
    /// (`[Skills] SkillUpgradeLevels`, default 1/6/11/16/21/26/31/36) it offers
    /// the class skill's upgrade cards — the R6/R7 mods, three per skill — and
    /// nothing else, so the run picks its build instead of rolling Megabonk's
    /// pool. Every other level stays a tome draft.
    /// </summary>
    internal static List<RewardEntry> DrawDraft(int count, int tier, int level, float luck)
    {
        var drawn = new List<RewardEntry>();
        if (!Active || count <= 0)
        {
            return drawn;
        }

        if (IsSkillDraftLevel())
        {
            // The cadence is the gate: every mod of the skill is offered from
            // the first draft, whatever its sheet tier or level floor says.
            drawn.AddRange(Draw(
                RewardTable.Mod, count, 3, level, luck, PoeSkills.ActiveSkillId,
                ignoreBranches: true, uniform: true));

            // The operator's rule (2026-11-05): a "full DPS" path must always
            // exist, so a skill draft never rolls three defence/utility cards.
            // If none of the three is offensive, the last card is redrawn from
            // the skill's offence mods; the pick stays forced, and there is no
            // reroll. A draft whose offence mods are all maxed keeps its roll.
            if (count > 1 && drawn.Count > 0 &&
                !drawn.Exists(entry => string.Equals(entry.Role, Offence, StringComparison.OrdinalIgnoreCase)))
            {
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in drawn)
                {
                    excluded.Add(entry.Id);
                }

                var offence = Draw(
                    RewardTable.Mod, 1, 3, level, luck, PoeSkills.ActiveSkillId,
                    ignoreBranches: true, uniform: true, role: Offence, exclude: excluded);
                if (offence.Count > 0)
                {
                    drawn[drawn.Count - 1] = offence[0];
                    Plugin.Logger.LogInfo(
                        $"reward pool: skill draft guaranteed one offence card ({offence[0].Name})");
                }
            }

            return drawn;
        }

        drawn.AddRange(Draw(
            RewardTable.Tome, count, tier, level, luck,
            uniform: true, ignoreGates: true));
        return drawn;
    }

    /// <summary>True when the next draft is the class skill's three upgrades.
    /// The operator's rule (2026-11-02): level 1, then every N levels
    /// (<c>[Skills] SkillUpgradeEvery</c>), until the skill has 40 upgrades —
    /// any distribution across its ten mods.
    ///
    /// The gate is the **character level** (<see cref="Level"/>), not the
    /// level-up screen's counter: a pot's XP chunk can push that counter ahead
    /// of the level the run is actually at, which offered a palier at level 3.
    /// The level must also *be* a palier: a chunk that jumps from 3 straight to
    /// 6 does not turn 6 into the level-5 draft. <see cref="nextSkillLevel"/>
    /// then only stops a lagging screen from drafting the same palier twice.</summary>
    internal static bool IsSkillDraftLevel()
    {
        if (!SkillMods.Active || SkillMods.TotalStacks >= SkillMods.MaxStacks)
        {
            return false;
        }

        var level = Level();
        if (level < nextSkillLevel)
        {
            return false;
        }

        if (level <= 1)
        {
            return true;
        }

        var every = Plugin.SkillUpgradeEvery.Value;
        return every > 0 && level % every == 0;
    }

    /// <summary>Moves the cadence past the palier just drafted. A level jump
    /// consumes the paliers it skipped, exactly like the levels it skipped.</summary>
    private static void AdvanceSkillCadence()
    {
        var level = Level();
        var every = Plugin.SkillUpgradeEvery.Value;
        nextSkillLevel = every <= 0 ? int.MaxValue : (level / every + 1) * every;
        if (nextSkillLevel <= level)
        {
            nextSkillLevel = level + Mathf.Max(1, every);
        }

        Plugin.Logger.LogInfo(
            $"reward pool: skill palier taken at level {level}; next at {nextSkillLevel}");
    }

    /// <summary>Weighted draw without replacement. Returns fewer than
    /// <paramref name="count"/> entries when the eligible pool is smaller.
    /// <paramref name="skill"/> filters mods to the class's own skill,
    /// <paramref name="rarity"/> keeps the draw to one rarity (the shady guy's
    /// request, see <see cref="ShadyRarity"/>) and <paramref name="ignoreBranches"/>
    /// offers every mod of the skill (R11's three-card draft), leaving the
    /// branch choice to the player instead of closing it in the pool.
    /// <paramref name="uniform"/> ignores weight/rarity, so a bounded choice
    /// among a skill's own upgrades is not biased by their card rarity.
    /// <paramref name="ignoreGates"/> also drops the tier band and the level
    /// floor: the operator's rule is that every tome and every upgrade shares
    /// exactly the same chance in a level-up draft, whatever its sheet tier.</summary>
    internal static List<RewardEntry> Draw(
        string kind, int count, int tier, int level, float luck, string skill = null, string rarity = null,
        bool ignoreBranches = false, bool uniform = false, bool ignoreGates = false,
        string role = null, HashSet<string> exclude = null)
    {
        var drawn = new List<RewardEntry>();
        if (!Active || count <= 0)
        {
            return drawn;
        }

        var shape = Shape();
        var tag = Tag();
        var candidates = new List<RewardEntry>();
        var hidden = 0;
        var hiddenRelics = 0;
        foreach (var entry in RewardTable.All)
        {
            if (!string.Equals(entry.Kind, kind, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A specific rarity **is** the gate: the shady guy's yellow hat
            // sells yellow cards at any depth, exactly like the host's own draw,
            // so the tier band and the level floor do not apply to that request.
            // (0.9.105: without this the legendary filter found nothing below
            // level 12 — every legendary row is tier 3 — and the shop fell back
            // to commons, which is what the operator saw.)
            if (!ignoreGates && rarity == null && (entry.Tier > tier || level < entry.MinLevel))
            {
                continue;
            }

            if (rarity != null)
            {
                if (!string.Equals(entry.Rarity, rarity, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (role != null && !string.Equals(entry.Role, role, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (exclude != null && exclude.Contains(entry.Id))
            {
                continue;
            }

            // S3: a tome that cannot help the class's skill never enters that
            // class's draft (+1 Projectile does not touch an aura, projectile
            // speed does nothing for a spin).
            if (entry.IsTome && !TomeApplies(entry, shape))
            {
                hidden++;
                continue;
            }

            // Same rule for the relics (2026-11-05): 13 of the 79 are pure
            // element cards, so a Duelist or Shadow drawing six relics had a
            // 67% chance of getting at least one that does nothing at all.
            // A relic with no stat this class reads (and no trigger) is never
            // offered.
            if (entry.IsRelic && !RelicApplies(entry, tag, shape))
            {
                hiddenRelics++;
                continue;
            }

            if (entry.IsMod)
            {
                // A mod only ever touches its own skill. The R11 skill draft
                // asks for every mod of that skill (the three cards) and leaves
                // the branch choice to the player; every other draw keeps R6's
                // "a taken branch closes the rest of that branch".
                if (skill == null ||
                    !string.Equals(entry.Skill, skill, StringComparison.OrdinalIgnoreCase) ||
                    (!ignoreBranches && SkillMods.BranchTaken(SkillMods.Branch(entry), entry.Id)))
                {
                    continue;
                }
            }

            if (Stacks(entry) >= entry.MaxStacks || Maxed(entry) || NoTomeSlot(entry))
            {
                continue;
            }

            candidates.Add(entry);
        }

        // One line per shape so the live run shows the class filter working
        // instead of a card quietly going missing (S3).
        if (hidden > 0 && string.Equals(kind, RewardTable.Tome, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(filteredShape, shape, StringComparison.Ordinal))
        {
            filteredShape = shape;
            Plugin.Logger.LogInfo(
                $"reward pool: shape {shape} hides {hidden} tome(s) that do nothing for it");
        }

        if (hiddenRelics > 0 && !string.Equals(filteredTag, tag, StringComparison.Ordinal))
        {
            filteredTag = tag;
            Plugin.Logger.LogInfo(
                $"reward pool: tag {tag} hides {hiddenRelics} relic(s) that do nothing for it");
        }

        while (drawn.Count < count && candidates.Count > 0)
        {
            var picked = Pick(candidates, luck, uniform);
            if (picked == null)
            {
                break;
            }

            drawn.Add(picked);
            candidates.Remove(picked);
        }

        return drawn;
    }

    /// <summary>The shape of the skill this run casts. Null means unknown (no
    /// player yet, or a skill missing from the sheet): the filter then lets
    /// every tome through instead of emptying the draft.</summary>
    private static string Shape()
    {
        try
        {
            return SkillDatabase.TryGet(PoeSkills.ActiveSkillId, out var skill) ? skill.Shape : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The damage tag of the skill this run casts; null lets every
    /// relic through, like <see cref="Shape"/>.</summary>
    private static string Tag()
    {
        try
        {
            return SkillDatabase.TryGet(PoeSkills.ActiveSkillId, out var skill) ? skill.Tag : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>True when at least one of the relic's stats is read by this
    /// class's skill, or when it carries a trigger (which any class can use).
    /// A generic stat (damage, attack speed, life, defence, gold…) is always
    /// useful, so a mixed relic is never hidden.</summary>
    private static bool RelicApplies(RewardEntry entry, string tag, string shape)
    {
        if (tag == null || shape == null ||
            !ItemDatabase.TryGet(entry.Id, out var definition) ||
            definition.Stats == null || definition.Stats.Count == 0)
        {
            return true;
        }

        var restricted = false;
        var applies = false;
        foreach (var stat in definition.Stats)
        {
            switch (stat.Stat)
            {
                case "FireDamage":
                case "BurnChance":
                    restricted = true;
                    applies |= string.Equals(tag, "fire", StringComparison.OrdinalIgnoreCase);
                    break;
                case "IceDamage":
                case "FreezeChance":
                    restricted = true;
                    applies |= string.Equals(tag, "cold", StringComparison.OrdinalIgnoreCase);
                    break;
                case "LightningDamage":
                    restricted = true;
                    applies |= string.Equals(tag, "lightning", StringComparison.OrdinalIgnoreCase);
                    break;
                case "PoisonDamageMultiplier":
                    restricted = true;
                    applies |= string.Equals(tag, "chaos", StringComparison.OrdinalIgnoreCase);
                    break;
                case "Projectiles":
                    restricted = true;
                    applies |= shape == "projectile" || shape == "chain" || shape == "ground";
                    break;
                case "ProjectileSpeedMultiplier":
                case "ProjectileBounces":
                    restricted = true;
                    applies |= shape == "projectile";
                    break;
                case "SizeMultiplier":
                    restricted = true;
                    applies |= shape != "projectile";
                    break;
                default:
                    // Damage, attack speed, crit, life, defence, gold, luck,
                    // pickup, chest price: every class reads them.
                    return true;
            }
        }

        return !restricted || applies || definition.TriggerCount > 0;
    }

    /// <summary>A tome's <c>shapes</c> list is the set of skill shapes it helps;
    /// a row with no list helps every shape (S3).</summary>
    private static bool TomeApplies(RewardEntry entry, string shape)
    {
        if (shape == null ||
            !Enum.TryParse(entry.Slot, out ETome tome) ||
            !TomeDatabase.TryGet(tome, out var definition) ||
            definition.Shapes == null ||
            definition.Shapes.Count == 0)
        {
            return true;
        }

        foreach (var allowed in definition.Shapes)
        {
            if (string.Equals(allowed, shape, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static RewardEntry Pick(List<RewardEntry> candidates, float luck, bool uniform = false)
    {
        var total = 0f;
        foreach (var entry in candidates)
        {
            total += uniform ? 1f : RewardTable.EffectiveWeight(entry, luck);
        }

        if (total <= 0f)
        {
            return null;
        }

        var roll = Random.value * total;
        foreach (var entry in candidates)
        {
            roll -= uniform ? 1f : RewardTable.EffectiveWeight(entry, luck);
            if (roll <= 0f)
            {
                return entry;
            }
        }

        return candidates[candidates.Count - 1];
    }

    /// <summary>Tome level or item amount, whichever the card's kind uses.</summary>
    private static int Stacks(RewardEntry entry)
    {
        var inventory = Inventory();
        if (inventory == null)
        {
            return 0;
        }

        try
        {
            if (entry.IsTome && Enum.TryParse(entry.Slot, out ETome tome))
            {
                return inventory.tomeInventory == null ? 0 : inventory.tomeInventory.GetTomeLevel(tome);
            }

            if (entry.IsRelic && Enum.TryParse(entry.Slot, out EItem item))
            {
                return inventory.itemInventory == null ? 0 : inventory.itemInventory.GetAmount(item);
            }

            if (entry.IsMod)
            {
                return Skills.SkillMods.Stacks(entry.Id);
            }
        }
        catch (Exception)
        {
            // A stack lookup must never block a draw; an unreadable count is 0.
        }

        return 0;
    }

    /// <summary>The game's own cap (<c>TomeInventory.IsMaxLevel</c>), which can
    /// be lower than the sheet's stack cap; a maxed card is never offered.</summary>
    private static bool Maxed(RewardEntry entry)
    {
        var inventory = Inventory();
        if (inventory == null || inventory.tomeInventory == null)
        {
            return false;
        }

        if (!entry.IsTome || !Enum.TryParse(entry.Slot, out ETome tome))
        {
            return false;
        }

        try
        {
            return inventory.tomeInventory.IsMaxLevel(tome);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// R9: the four tome slots are a hard cap. The game's own rule is
    /// <c>TomeInventory.IsMaxed()</c> = <c>GetNumTomes() &gt;=
    /// InventoryUtility.GetNumAvailableTomeSlots()</c>, but <c>AddTome</c> only
    /// *records* that state — it never refuses — and the boss reward, the
    /// chests and the merchant all grant through our pool, so a fifth tome used
    /// to arrive whenever the pool offered one. A tome already owned still
    /// levels up (that is not a new slot); a new one is never offered once every
    /// slot is taken, which is the single gate for every draw.
    /// </summary>
    private static bool NoTomeSlot(RewardEntry entry)
    {
        if (entry == null || !entry.IsTome || !Enum.TryParse(entry.Slot, out ETome tome))
        {
            return false;
        }

        var inventory = Inventory();
        if (inventory == null || inventory.tomeInventory == null)
        {
            return false;
        }

        try
        {
            if (inventory.tomeInventory.HasTome(tome))
            {
                return false;
            }

            var slots = InventoryUtility.GetNumAvailableTomeSlots();
            return slots > 0 && inventory.tomeInventory.GetNumTomes() >= slots;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The sheet row behind a tome entry, for the card text of a grant
    /// (null when the slot cannot resolve, in which case the card says nothing).</summary>
    private static TomeDefinition TomeSwapDefinition(RewardEntry entry) =>
        entry != null &&
        Enum.TryParse(entry.Slot, out ETome tome) &&
        TomeDatabase.TryGet(tome, out var definition)
            ? definition
            : null;

    internal static bool ResolveTome(RewardEntry entry, out TomeData data)
    {
        data = null;
        if (entry == null || !Enum.TryParse(entry.Slot, out ETome slot))
        {
            return false;
        }

        try
        {
            var manager = DataManager.Instance;
            data = manager == null ? null : manager.GetTome(slot);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"reward pool: {entry.Id} has no TomeData ({error.Message})");
        }

        return data != null;
    }

    internal static ItemData ResolveRelic(RewardEntry entry)
    {
        if (entry == null || !Enum.TryParse(entry.Slot, out EItem slot))
        {
            return null;
        }

        try
        {
            var manager = DataManager.Instance;
            return manager == null ? null : manager.GetItem(slot);
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"reward pool: {entry.Id} has no ItemData ({error.Message})");
            return null;
        }
    }

    /// <summary>True when the draft last written by <see cref="FillDraft"/> was
    /// a real skill-upgrade draft (mod cards, not a tome fallback). The level-up
    /// screen's refresh is refused while it is set, so a skill draft is a forced
    /// choice of its three upgrades.</summary>
    internal static bool LastDraftWasSkill { get; private set; }

    /// <summary>
    /// Replaces the draft's cards with this table's draw. Returns how many
    /// cards were written; 0 leaves the caller's list alone.
    /// </summary>
    internal static int FillDraft(Il2CppSystem.Collections.Generic.List<IUpgradable> cards, string source)
    {
        if (!Active || cards == null)
        {
            return 0;
        }

        var previousWasSkill = LastDraftWasSkill;
        LastDraftWasSkill = false;
        var level = DraftLevel();
        var stacks = Skills.SkillMods.TotalStacks;
        if (level == lastDraftLevel && previousWasSkill && stacks > lastDraftStacks)
        {
            // The screen fed the same level again after a completed skill pick
            // (the inventory/screen lagged): the next draft is one level up.
            Plugin.Logger.LogInfo(
                $"reward pool: repeated draft level {level} after a skill pick -> {level + 1}");
            level++;
        }

        lastDraftLevel = level;
        lastDraftStacks = stacks;
        var tier = TierForLevel(level);
        var luck = Luck();
        var skillDraft = IsSkillDraftLevel();
        var drawn = DrawDraft(DraftCards, tier, level, luck);
        LastDraftWasSkill = skillDraft && drawn.Count > 0 && drawn[0].IsMod;
        if (LastDraftWasSkill)
        {
            // The pick is forced, so the palier is spent as soon as its three
            // cards appear: a repeated draft cannot offer the same palier twice.
            AdvanceSkillCadence();
        }

        var replacement = new List<IUpgradable>();
        var names = new List<string>();
        var taken = new HashSet<string>();
        foreach (var entry in drawn)
        {
            taken.Add(entry.Id);
            var card = Resolve(entry);
            if (card == null)
            {
                continue;
            }

            replacement.Add(card);
            names.Add($"{entry.Name} [{entry.Rarity}/t{entry.Tier}]");
        }

        // A card the game could not build (a missing sheet row, a failed mod
        // clone) must not cost the draft a slot: fill the rest with tomes. A
        // skill draft is the exception: it shows the skill's own upgrades, and
        // only falls back when the class has none left (so the picker is never
        // empty), never padding it with a tome.
        Plugin.Logger.LogInfo(
            $"reward pool: draft level {level} (inventory {Level()}), " +
            $"{(skillDraft ? "skill upgrades" : "tomes")}, {replacement.Count} card(s)");
        if (replacement.Count < DraftCards && (!skillDraft || replacement.Count == 0))
        {
            foreach (var entry in Draw(RewardTable.Tome, DraftCards * 2, tier, level, luck))
            {
                if (replacement.Count >= DraftCards)
                {
                    break;
                }

                if (!taken.Add(entry.Id))
                {
                    continue;
                }

                var card = Resolve(entry);
                if (card == null)
                {
                    continue;
                }

                replacement.Add(card);
                names.Add($"{entry.Name} [{entry.Rarity}/t{entry.Tier}]");
            }
        }

        if (replacement.Count == 0)
        {
            Plugin.Logger.LogWarning($"reward pool: {source} has no eligible tome");
            return 0;
        }

        cards.Clear();
        foreach (var card in replacement)
        {
            cards.Add(card);
        }

        // R11b item 12 follow-up: the log names the mode so a live run can prove
        // the draft is flat. The sheet's 80/50 weights are not read here, and
        // since 0.9.185 neither is the tome tier/level gate: every tome and
        // every upgrade has the same chance in a level-up draft.
        const string mode = " uniform";
        Plugin.Logger.LogInfo(
            $"reward pool: {source} -> {replacement.Count} card(s){mode}: {string.Join(", ", names)}");
        return replacement.Count;
    }

    /// <summary>A sheet entry as the card object the draft needs: a mod is a
    /// cloned <c>TomeData</c> (the pick is intercepted by <c>SkillMods</c>), a
    /// tome is the game's own <c>TomeData</c>.</summary>
    private static IUpgradable Resolve(RewardEntry entry)
    {
        if (entry.IsMod)
        {
            var modCard = Skills.SkillMods.Card(entry);
            return modCard == null ? null : modCard.TryCast<IUpgradable>();
        }

        if (!ResolveTome(entry, out var data))
        {
            return null;
        }

        return data.TryCast<IUpgradable>();
    }

    /// <summary>
    /// The boss reward: 1 card for a floor boss, 2 for the final boss, applied
    /// through the game's own upgrade entry (<c>PlayerInventory.AddUpgrade</c>
    /// dispatches to the tome/weapon inventory) so the stat pipeline, the HUD
    /// and the run reset stay native. The card is logged with its rarity.
    /// </summary>
    internal static void GrantBossReward(int tier, int count, string source)
    {
        if (!Active)
        {
            return;
        }

        var inventory = Inventory();
        if (inventory == null)
        {
            return;
        }

        var drawn = new List<RewardEntry>();
        drawn.AddRange(Draw(RewardTable.Relic, count, tier, Level(), Luck()));
        if (drawn.Count < count)
        {
            drawn.AddRange(Draw(RewardTable.Tome, count - drawn.Count, tier, Level(), Luck()));
        }

        foreach (var entry in drawn)
        {
            Grant(entry, source);
        }
    }

    /// <summary>Tome of Hoarding: the native effect spawns a free chest the
    /// player has to walk to. The operator's rule (2026-11-05) is a direct
    /// reward instead: one relic drawn with the normal weighted rules, and a
    /// tome when every relic is maxed. The tome's card states it.</summary>
    internal static void GrantHoarderItem(string source)
    {
        if (!Active)
        {
            return;
        }

        var drawn = Draw(RewardTable.Relic, 1, 3, Level(), Luck());
        if (drawn.Count == 0)
        {
            drawn = Draw(RewardTable.Tome, 1, 3, Level(), Luck());
        }

        foreach (var entry in drawn)
        {
            Grant(entry, source);
        }
    }

    private static void Grant(RewardEntry entry, string source)
    {
        try
        {
            var inventory = Inventory();
            if (inventory == null)
            {
                return;
            }

            if (entry.IsRelic && Enum.TryParse(entry.Slot, out EItem item))
            {
                inventory.itemInventory.AddItem(item);
                Plugin.Logger.LogInfo(
                    $"reward pool: {source} grants relic {entry.Name} [{entry.Rarity}, tier {entry.Tier}]");
                return;
            }

            if (entry.IsTome && ResolveTome(entry, out var tomeData))
            {
                var upgrade = tomeData.TryCast<IUpgradable>();
                if (upgrade == null)
                {
                    Plugin.Logger.LogWarning($"reward pool: {entry.Id} is not an upgrade card");
                    return;
                }

                // The stat a tome applies comes from the *offer*, not from the
                // asset: TomeInventory.AddTome adds the offer's modification per
                // stack (an empty offer grants nothing). The offer is the game's
                // own, so it carries the rarity multiplier the card states.
                var rarity = Rarity(entry);
                var offer = tomeData.GetUpgradeOffer(rarity);
                inventory.AddUpgrade(upgrade, offer, rarity);
                Plugin.Logger.LogInfo(
                    $"reward pool: {source} grants tome {entry.Name} [{entry.Rarity}, tier {entry.Tier}] " +
                    $"\"{TomeSwap.CardText(TomeSwapDefinition(entry), rarity)}\"");
            }
        }
        catch (Exception error)
        {
            Plugin.Logger.LogWarning($"reward pool: could not grant {entry.Id} ({error.Message})");
        }
    }

    internal static ERarity Rarity(RewardEntry entry)
    {
        if (string.Equals(entry.Rarity, RewardTable.Legendary, StringComparison.OrdinalIgnoreCase))
        {
            return ERarity.Legendary;
        }

        if (string.Equals(entry.Rarity, RewardTable.Epic, StringComparison.OrdinalIgnoreCase))
        {
            return ERarity.Epic;
        }

        if (string.Equals(entry.Rarity, RewardTable.Rare, StringComparison.OrdinalIgnoreCase))
        {
            return ERarity.Rare;
        }

        return ERarity.Common;
    }

    private static PlayerInventory Inventory()
    {
        try
        {
            var player = MyPlayer.Instance;
            return player == null ? null : player.inventory;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static PlayerStatsNew Stats()
    {
        var inventory = Inventory();
        return inventory == null ? null : inventory.playerStats;
    }
}
