using System.Text.Json;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Per-instance gear rolls. Lua_CFG_EquipConfig.GetExtraAttribute joins _iEntries to EquipAttriGroupConfig._iGroup;
/// GetSpecialAttribute joins _kSpecial to EquipSpecialGroupConfig. LC_ItemProtoParse.ParseEquipInfo reads attribute
/// IDs/values at 100/140 and SpecialEffectConfig IDs at 80. See docs/systems/equipment-affixes.md for inference boundaries.
/// </summary>
internal static class EquipmentAffixes
{
    // A roll's pools are fixed per (table, quality or group). Caching per GameData avoids scanning the whole table
    // with by-name JSON lookups for every rolled item during a new role's starter-kit login.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<GameData, Dictionary<(string Table, long Key), (long Weight, JsonElement Row)[]>> Pools = [];

    /// <summary>The table's rows for the key passing the table's fixed rule, in table order; a fresh list per call.</summary>
    private static List<(long Weight, JsonElement Row)> Pool(GameData data, string table, long key)
    {
        var cache = Pools.GetValue(data, _ => []);
        (long Weight, JsonElement Row)[]? rows;
        lock (cache)
            if (!cache.TryGetValue((table, key), out rows))
                cache[(table, key)] = rows = table switch
                {
                    "EquipAttriRandom" => [.. EquipAttriRandom.All(data).Where(r => r.Quality == key && r.Weight > 0).Select(r => (r.Weight, r.Row))],
                    "EquipEntryRandom" => [.. EquipEntryRandom.All(data).Where(r => r.Quality == key && r.Weight > 0).Select(r => (r.Weight, r.Row))],
                    "EquipAttriGroupConfig" => [.. EquipAttriGroupConfig.All(data)
                        .Where(r => r.Group == key && r.Weight > 0 && r.AttriId > 0 && r.Max >= r.Min).Select(r => (r.Weight, r.Row))],
                    "EquipSpecialGroupConfig" => [.. EquipSpecialGroupConfig.All(data).Where(r => r.Group == key && r.Weight > 0).Select(r => (r.Weight, r.Row))],
                    _ => [],
                };
        return [.. rows];
    }

    internal const int EntryKey = 100, EntryValue = 140, EntrySlots = 20, SpecialKey = 80, SpecialSlots = 10;

    /// <summary>
    /// (inferred) Roll base stats on creation, preserving the existing quality-weighted ratio policy. For gear that
    /// needs appraisal, defer entries and stunts until 31401; otherwise finish the roll before persisting the grant.
    /// </summary>
    internal static Dictionary<int, long>? Create(int entry, GameData data)
    {
        if (Config(entry, data) is not { } equipRow || ItemConfig.Get(data, entry) is not { } item) return null;
        var equip = new EquipConfig(equipRow);
        var attrs = new Dictionary<int, long> { [3] = equip.IfAppraise == 1 ? 0 : 1 };
        RollBase(attrs, item, equip, data);
        if (attrs[3] == 1) RollAffixes(attrs, item, equip, data);
        return attrs;
    }

    /// <summary>Called only for a validated detached appraisal candidate; preserve base rolls and unrelated state.</summary>
    internal static void Appraise(InvItem item, GameData data)
    {
        if (Config(item.Entry, data) is not { } equipRow || ItemConfig.Get(data, item.Entry) is not { } config)
            throw new InvalidDataException($"Equipment {item.Entry} has no config");
        var equip = new EquipConfig(equipRow);
        item.Attrs ??= [];
        if (!item.Attrs.ContainsKey(30)) RollBase(item.Attrs, config, equip, data);
        RollAffixes(item.Attrs, config, equip, data);
        item.Attrs[3] = 1;
    }

    /// <summary>
    /// (inferred) Repair pre-affix emulator saves only when an appraised item lacks a guaranteed entry. A completed
    /// roll, including a valid zero-stunt outcome, is never rerolled on login. Existing base stats remain unchanged.
    /// </summary>
    internal static bool NeedsLegacyEntries(InvItem item, GameData data)
    {
        if (item.Attrs?.GetValueOrDefault(3) != 1 || Entries(item).Any() || Config(item.Entry, data) is not { } equipRow)
            return false;
        var equip = new EquipConfig(equipRow);
        if (equip.FixedEntries.Length > 0) return true;
        if (equip.Entries == 0 || ItemConfig.Get(data, item.Entry) is not { } config) return false;
        var counts = Pool(data, "EquipEntryRandom", config.Quality);
        return counts.Count > 0 && counts.All(r => new EquipEntryRandom(r.Row).Num > 0);
    }

    internal static JsonElement? Config(int entry, GameData data) =>
        ItemConfig.Get(data, entry) is { Type: 5 } item ? data.Row("EquipConfig", item.EquipId) : null;

    private static void RollBase(Dictionary<int, long> attrs, ItemConfig item, EquipConfig equip, GameData data)
    {
        var ratios = Pool(data, "EquipAttriRandom", item.Quality);
        int slot = 0;
        foreach (var row in equip.BasicAttribute)
        {
            if (row.Length < 2 || slot >= 20) continue;
            long min = row[1], max = row.Length > 2 ? row[2] : min;
            long ratio = ratios.Count == 0 ? 5000 : new EquipAttriRandom(Draw(ratios)).Ratio;
            attrs[30 + slot] = row[0];
            attrs[60 + slot++] = min + (max - min) * ratio / 10000;
        }
    }

    /// <summary>
    /// Pool/ranges and fixed entries are client-proven. Weighted selection without replacement, inclusive uniform
    /// entry values, independent _kSpecial [group,chance/10000] draws and conflict filtering are (inferred).
    /// EquipEntryExclusion / EquipSpecialExclusion have only empty rows in both client exports: no hidden cross-ID
    /// rule is invented. Exclude duplicate attributes/effects and nonzero SpecialEffectConfig families.
    /// </summary>
    private static void RollAffixes(Dictionary<int, long> attrs, ItemConfig item, EquipConfig equip, GameData data)
    {
        var entryIds = new HashSet<long>();
        int entrySlot = 0;
        for (int i = 0; i < EntrySlots; i++)
        {
            attrs.Remove(EntryKey + i);
            attrs.Remove(EntryValue + i);
        }
        foreach (var fixedEntry in equip.FixedEntries)
            if (fixedEntry.Length >= 2 && fixedEntry[0] > 0 && entryIds.Add(fixedEntry[0]) && entrySlot < EntrySlots)
            {
                attrs[EntryKey + entrySlot] = fixedEntry[0];
                attrs[EntryValue + entrySlot++] = fixedEntry[1];
            }
        long group = equip.Entries;
        if (group > 0)
        {
            var counts = Pool(data, "EquipEntryRandom", item.Quality);
            int count = counts.Count == 0 ? 0 : (int)new EquipEntryRandom(Draw(counts)).Num;
            var pool = Pool(data, "EquipAttriGroupConfig", group);
            pool.RemoveAll(r => entryIds.Contains(new EquipAttriGroupConfig(r.Row).AttriId));
            for (int n = 0; n < count && entrySlot < EntrySlots && pool.Count > 0; n++)
            {
                var picked = new EquipAttriGroupConfig(Draw(pool));
                long id = picked.AttriId, min = picked.Min, max = picked.Max;
                attrs[EntryKey + entrySlot] = id;
                attrs[EntryValue + entrySlot++] = min == max ? min : Random.Shared.NextInt64(min, max + 1);
                entryIds.Add(id);
                pool.RemoveAll(r => entryIds.Contains(new EquipAttriGroupConfig(r.Row).AttriId));
            }
        }

        // A legacy entry repair must preserve any already persisted stunt roll, including its order.
        if (attrs.ContainsKey(SpecialKey)) return;
        // Respect the parser's ten slots; never spill into the adjacent itemInfos namespaces (debug configs have 11).
        for (int i = 0; i < SpecialSlots; i++) attrs.Remove(SpecialKey + i);
        var specials = new HashSet<long>();
        var families = new HashSet<long>();
        int specialSlot = 0;
        bool Allowed(long id) => id > 0 && !specials.Contains(id) && SpecialEffectConfig.Get(data, id) is { } effect
            && (effect.GroupId == 0 || !families.Contains(effect.GroupId));
        void AddSpecial(long id)
        {
            if (specialSlot >= SpecialSlots || !Allowed(id)) return;
            attrs[SpecialKey + specialSlot++] = id;
            specials.Add(id);
            long family = SpecialEffectConfig.Get(data, id)!.Value.GroupId;
            if (family != 0) families.Add(family);
        }
        foreach (long id in equip.FixedSpecialAttribute) AddSpecial(id);
        foreach (var directive in equip.Special)
        {
            if (directive.Length < 2 || specialSlot >= SpecialSlots || Random.Shared.Next(10000) >= directive[1]) continue;
            var pool = Pool(data, "EquipSpecialGroupConfig", directive[0]);
            pool.RemoveAll(r => new EquipSpecialGroupConfig(r.Row).SpecialId is var sid and not 0 && !Allowed(sid));
            if (pool.Count > 0) AddSpecial(new EquipSpecialGroupConfig(Draw(pool)).SpecialId);
        }
    }

    private static JsonElement Draw(List<(long Weight, JsonElement Row)> rows)
    {
        long roll = Random.Shared.NextInt64(rows.Sum(r => r.Weight));
        foreach (var (weight, row) in rows)
        {
            roll -= weight;
            if (roll < 0) return row;
        }
        return rows[^1].Row;
    }

    internal static IEnumerable<long[]> Rows(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var list) || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() == 0) yield break;
        if (list[0].ValueKind != JsonValueKind.Array)
        {
            yield return list.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
            yield break;
        }
        foreach (var tuple in list.EnumerateArray())
            if (tuple.ValueKind == JsonValueKind.Array)
                yield return tuple.EnumerateArray().Select(v => (long)(GameData.Number(v) ?? 0)).ToArray();
    }

    internal static IEnumerable<(long Id, double Value)> Entries(InvItem item)
    {
        for (int i = 0; i < EntrySlots && item.Attrs != null && item.Attrs.TryGetValue(EntryKey + i, out long id)
            && item.Attrs.TryGetValue(EntryValue + i, out long value); i++) yield return (id, value);
    }

    internal static IEnumerable<long> Specials(InvItem item)
    {
        for (int i = 0; i < SpecialSlots && item.Attrs != null && item.Attrs.TryGetValue(SpecialKey + i, out long id); i++)
            if (id > 0) yield return id;
    }
}
