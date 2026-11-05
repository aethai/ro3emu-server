using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Classic mode walks by mouse: a role that kept Classic through the mode panel without its keyboard settings logged in
/// with mouse movement off (no ground cell, clicks did not walk). RoleSessionService.RepairClassicMovement turns it on.
/// </summary>
public sealed class ClassicMovementTests
{
    private static DBSettingInfo Settings(int gameMode, KeyBoardInfo? keys)
    {
        var s = new DBSettingInfo();
        s.ClientSetting[(int)SettingType.GameMode] = new GameModeSetting { GameMode = gameMode, PcPlatformRecord = true }.ToByteString();
        if (keys != null) s.ClientSetting[(int)SettingType.KeyBoard] = keys.ToByteString();
        return s;
    }

    private static KeyBoardInfo Keys(DBSettingInfo s) => KeyBoardInfo.Parser.ParseFrom(s.ClientSetting[(int)SettingType.KeyBoard]);

    [Fact]
    public void ClassicWithoutKeyboardSettingsGetsMouseMovement()
    {
        var s = Settings(1, null);
        Assert.True(RoleSessionService.RepairClassicMovement(s));
        Assert.True(Keys(s).EnableMouseMove);
        Assert.Equal(1, Keys(s).KeyBoardMode);
    }

    [Fact]
    public void ClassicWithModernKeyboardSettingsGetsMouseMovement()
    {
        var s = Settings(1, new KeyBoardInfo { EnableKeyboardMove = true, OverrideBindingJson = "{}" });
        Assert.True(RoleSessionService.RepairClassicMovement(s));
        Assert.True(Keys(s).EnableMouseMove);
        Assert.Equal("{}", Keys(s).OverrideBindingJson);
    }

    [Fact]
    public void HealthyOrModernSettingsAreLeftAlone()
    {
        Assert.False(RoleSessionService.RepairClassicMovement(Settings(1, new KeyBoardInfo { KeyBoardMode = 1, EnableMouseMove = true })));
        Assert.False(RoleSessionService.RepairClassicMovement(Settings(2, new KeyBoardInfo { EnableKeyboardMove = true })));
        Assert.False(RoleSessionService.RepairClassicMovement(new DBSettingInfo()));
    }
}
