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
        Assert.Equal(0, Keys(s).SelectBtn);
        Assert.Equal(0, Keys(s).MoveBtn);
        Assert.Equal(0, Keys(s).CameraMode);
    }

    [Fact]
    public void SavedKeyboardSettingsAreNeverOverwritten()
    {
        var s = Settings(1, new KeyBoardInfo { EnableKeyboardMove = true, OverrideBindingJson = "{}" });
        Assert.False(RoleSessionService.RepairClassicMovement(s));
        var keys = Keys(s);
        Assert.True(keys.EnableKeyboardMove);
        Assert.False(keys.EnableMouseMove);
        Assert.Equal("{}", keys.OverrideBindingJson);
    }

    [Fact]
    public void ARightClickChoiceIsKept()
    {
        // Right, Right and ShiftRightDrag are a player's own choice in the Button tab, not a broken default.
        var s = Settings(1, new KeyBoardInfo { KeyBoardMode = 1, EnableMouseMove = true, SelectBtn = 1, MoveBtn = 1, CameraMode = 2 });
        Assert.False(RoleSessionService.RepairClassicMovement(s));
        Assert.Equal(1, Keys(s).MoveBtn);
        Assert.Equal(2, Keys(s).CameraMode);
    }

    [Fact]
    public void SavedMouseOffIsNeverOverwritten()
    {
        var s = Settings(1, new KeyBoardInfo { KeyBoardMode = 1, EnableMouseMove = false, EnableKeyboardMove = true, SelectBtn = 0, MoveBtn = 0, CameraMode = 0 });
        Assert.False(RoleSessionService.RepairClassicMovement(s));
        var keys = Keys(s);
        Assert.False(keys.EnableMouseMove);
        Assert.True(keys.EnableKeyboardMove);
    }

    [Fact]
    public void CorruptKeyBoardIsRepairedLikeMissing()
    {
        var s = Settings(1, null);
        s.ClientSetting[(int)SettingType.KeyBoard] = ByteString.CopyFrom(0xFF, 0xFF, 0xFF);
        Assert.True(RoleSessionService.RepairClassicMovement(s));
        Assert.True(Keys(s).EnableMouseMove);
        Assert.Equal(0, Keys(s).CameraMode);
    }

    [Fact]
    public void HealthyOrModernSettingsAreLeftAlone()
    {
        Assert.False(RoleSessionService.RepairClassicMovement(Settings(1, new KeyBoardInfo { KeyBoardMode = 1, EnableMouseMove = true, SelectBtn = 0, MoveBtn = 0, CameraMode = 0 })));
        Assert.False(RoleSessionService.RepairClassicMovement(Settings(1, new KeyBoardInfo { KeyBoardMode = 1, EnableMouseMove = true, SelectBtn = 0, MoveBtn = 0, CameraMode = 1 })));
        Assert.False(RoleSessionService.RepairClassicMovement(Settings(2, new KeyBoardInfo { EnableKeyboardMove = true })));
        Assert.False(RoleSessionService.RepairClassicMovement(new DBSettingInfo()));
    }
}
