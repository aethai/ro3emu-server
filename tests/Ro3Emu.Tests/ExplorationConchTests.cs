using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed class ExplorationConchTests
{
    [Fact]
    public void SamePointNumberOnDifferentMapsCountsSeparately()
    {
        var state = new DBWorldExploration();
        Assert.True(NewActivityService.RecordConch(state, 20, 7));
        Assert.True(NewActivityService.RecordConch(state, 20, 8));
        Assert.True(NewActivityService.RecordConch(state, 30, 7));
        Assert.False(NewActivityService.RecordConch(state, 20, 7));
        Assert.Equal(2, state.MapCollect[20].Sum);
        Assert.Equal(1, state.MapCollect[30].Sum);
        Assert.NotEqual(NewActivityService.ConchUid(20, 7), NewActivityService.ConchUid(30, 7));
    }
}
