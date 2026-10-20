using System.Text.Json;
using System.Text.Json.Nodes;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Ordinary guardian skills, distinct from the tower's PetSkill rows. The ActiveGuardSkill (8)
/// supplies the cooldown; its auto-learned GuardianSkill (41) supplies the actual effect and presentation.
/// Classification comes from SkillConfig/Localization_en, not the presentation's hostile selector.
/// </summary>
internal static class GuardianPetRules
{
    internal enum Kind { Damage, OwnerBuff, AlliedBuff, Heal, Debuff }
    internal sealed record Skill(int Slot, int PetId, int Star, int Stage, int Plan,
        JsonElement Active, JsonElement Effect, Kind Kind)
    {
        public int Id => (int)GameData.Int(Active, "_iID");
        public int EffectId => (int)GameData.Int(Effect, "_iID");
    }

    internal static JsonElement? Child(GameData data, JsonElement active)
    {
        var groups = Numbers(active, "_kLogicData").Select(n => (long)n).ToHashSet();
        var candidates = Numbers(active, "_kAutoLearnSkill")
            .Select(id => data.Row("SkillConfig", (long)id))
            .Where(row => row is { } r && GameData.Int(r, "_iSystemType") == 41)
            .Select(row => row!.Value).ToArray();
        // Some wrappers auto-learn additional proc skills. _kLogicData identifies the primary group.
        var matching = candidates.Where(row => groups.Contains(GameData.Int(row, "_iSkillID"))).ToArray();
        return matching.Length == 1 ? matching[0] : candidates.Length == 1 ? candidates[0] : null;
    }

    internal static string Script(JsonElement row) => row.TryGetProperty("_kScriptLogic", out var s) ? s.GetString() ?? "" : "";

    internal static Kind? Classify(JsonElement effect) => Script(effect) switch
    {
        "PetSkill_2_2_QiaoKeHou" or "BoLiPaoPao" or "yemanhudun" or "GuiNv_HuZhu"
            or "YaoXianNv_HuZhu" => Kind.OwnerBuff,
        "Pet_Tikbalang_23212" => Kind.AlliedBuff,
        "YaoGun" or "SenLinCiFu" or "ChaoXiSongGe" or "Alice_Huzhu" or "SerpentGuard" => Kind.Heal,
        "Pet_Marionette_23392" => Kind.Debuff, // its damage array is not a damage formula
        "Pet_WildRose_23552" or "Pet_Salamander_23462"
            or "yemujuechang" or "huxiaolieduan" or "XieXinLianRen"
            or "PetSkill_2_2_QiWangNvShen" or "Observation_Huzhu" or "Pet_Wanderman_23172"
            or "Pet_NightmareTerror_23302" or "AiLiSe_HuZhu" or "Pet_Hodremlin_23052"
            => Kind.Damage,
        // Reactive, periodic, displacement and conditional scripts need their own execution rules.
        // Never execute an unknown script as a generic attack just because it has a damage parameter.
        _ => null,
    };

    /// <summary>Make a single explicitly documented hit; other scalar-array entries can be conditional effects.</summary>
    internal static JsonElement DamageRow(Skill skill, StatSheet stats, int component)
    {
        var node = JsonNode.Parse(skill.Effect.GetRawText())!.AsObject();
        var ratios = Numbers(skill.Effect, "_kDamageParam1");
        var flats = Numbers(skill.Effect, "_kDamageParam2");
        node["_kDamageParam1"] = new JsonArray(skill.Kind == Kind.Damage ? ratios.ElementAtOrDefault(component) : 0);
        node["_kDamageParam2"] = new JsonArray(skill.Kind == Kind.Damage ? flats.ElementAtOrDefault(component) : 0);
        // All supported ordinary guardian attacks explicitly describe the master's adaptive attack.
        node["_iDmgType"] = stats[104] > stats[103] ? 2 : 1;
        node["_kBuff1"] = new JsonArray(); // owner/allied buffs and finite shields are applied separately
        if (component != 0) node["_kBuff2"] = new JsonArray();
        return JsonSerializer.SerializeToElement(node);
    }

    internal static int Components(Skill skill) => skill.Kind != Kind.Damage ? 1
        : Script(skill.Effect) is "yemujuechang" or "huxiaolieduan" or "XieXinLianRen"
            ? Math.Max(1, Numbers(skill.Effect, "_kDamageParam1").Length) : 1;

    internal static double[] Numbers(JsonElement row, string key) =>
        row.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(n => GameData.Number(n) ?? 0).ToArray() : [];

    internal static long Shield(Skill skill, JsonElement buff, StatSheet owner, StatSheet recipient)
    {
        // Bobo's ordinary guardian shield has no formula in BuffConfig; its ActiveGuardSkill description
        // names adaptive ATK * DescData[0]%. The tower version has a different HP-based formula.
        if (Script(skill.Effect) == "BoLiPaoPao")
            return Math.Max(0, (long)Math.Floor(Math.Max(owner[103], owner[104])
                * Numbers(skill.Active, "_kDescData").ElementAtOrDefault(0) / 100));
        return PetCombatRules.AttributeAmount(buff, owner, recipient);
    }
}
