using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>
/// The client's ItemUseLimitConfig (data/config/ItemUseLimitConfig.json): the rows ItemConfig._kUseLimit names. No client
/// Lua reads it (only the server did); see <see cref="ForbidsScene"/> for how its one limit type is read.
/// </summary>
[ConfigTable("ItemUseLimitConfig")]
public readonly partial record struct ItemUseLimitConfig(JsonElement Row)
{
    /// <summary>_iLimitType 1: a list of maps in _kParam.</summary>
    public const long MapList = 1;

    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iLimitType")] public partial long LimitType { get; }
    [ConfigField("_kParam")] public partial long[] Param { get; }

    /// <summary>
    /// Whether this limit refuses the item on <paramref name="scene"/>. (inferred) _iLimitType 1 is read as MapConfig's own
    /// _iLimitType 1 (LC_ItemHelper.ItemCanUseInCurrentMap: 1 forbids the listed items): the maps in _kParam forbid the
    /// item. Both rows list 10110/10111 (the starting maps, PublicParamConfig.SpawnSceneEntry 10111) on the Fly and
    /// Butterfly Wings, the Quest Fly Wings, the Yggdrasil Leaf and quest items whose quests run elsewhere (TaskConfig
    /// 105080 on map 10514), so the opposite reading, "usable only there", would disable them on every other map.
    /// Other limit types are unknown and limit nothing.
    /// </summary>
    public bool ForbidsScene(long scene) => LimitType == MapList && Param.Contains(scene);

    /// <summary>Whether any of the item's _kUseLimit rows refuses it on <paramref name="scene"/>.</summary>
    public static bool Forbids(GameData data, JsonElement itemRow, long scene) =>
        new ItemConfig(itemRow).UseLimit.Any(id => Get(data, id) is { } limit && limit.ForbidsScene(scene));
}
