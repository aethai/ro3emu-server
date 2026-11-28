using System.Text.Json;

namespace Ro3Emu.Server.Data.Config;

/// <summary>The client's ChatChannelConfig (data/config/ChatChannelConfig.json): the chat channels (1 world .. 11), their
/// function and the cooldown the client keeps between two lines (_iCD, seconds).</summary>
[ConfigTable("ChatChannelConfig")]
public readonly partial record struct ChatChannelConfig(JsonElement Row)
{
    [ConfigField("_iCD")] public partial long Cd { get; }
    [ConfigField("_iID")] public partial long Id { get; }
    [ConfigField("_iType")] public partial long Type { get; }
}
