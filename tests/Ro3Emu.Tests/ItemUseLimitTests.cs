using System.Text.Json;
using Ro3Emu.Server.Data.Config;

namespace Ro3Emu.Tests;

/// <summary>ItemUseLimitConfig, the maps ItemConfig._kUseLimit refuses an item on (inferred: a forbidden-map list).</summary>
public sealed class ItemUseLimitTests
{
    private static ItemUseLimitConfig Limit(long type, params long[] maps) =>
        new(JsonDocument.Parse(JsonSerializer.Serialize(new { _iID = 7, _iLimitType = type, _kParam = maps })).RootElement);

    [Fact]
    public void AMapListForbidsOnlyTheListedMaps()
    {
        var limit = Limit(ItemUseLimitConfig.MapList, 501, 502);
        Assert.True(limit.ForbidsScene(501));
        Assert.True(limit.ForbidsScene(502));
        Assert.False(limit.ForbidsScene(503));
    }

    [Fact]
    public void AnUnknownLimitTypeLimitsNothing() => Assert.False(Limit(9, 501).ForbidsScene(501));
}
