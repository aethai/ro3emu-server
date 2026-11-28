using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's FriendlyItemConfig (data/config/FriendlyItemConfig.json): the items the friend gift panel offers.</summary>
[ConfigTable("FriendlyItemConfig")]
public readonly partial record struct FriendlyItemConfig(JsonElement Row)
{
    [ConfigField("_iChatId")] public partial long ChatId { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iItemId")] public partial long ItemId { get; }
    [ConfigField("_iItemType")] public partial long ItemType { get; }
}
