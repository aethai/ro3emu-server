using Google.Protobuf;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// Gear's wear rules (LC_ItemHelper.CanUseByInfo): _iLevelNeed (657), _kJobNeed (655), _iSexNeed (656). 31400 and 31413
/// refuse gear the role may not wear, and gear worn against them comes off at login. The fixture's invented rows:
/// 3990101 asks level 30, 3990102 the Thief line (5100), 3990103 a sex no role has, 3990104 nothing.
/// </summary>
public sealed class EquipRequirementTests
{
    private const int HighLevel = 3990101, ThiefOnly = 3990102, OtherSex = 3990103, Free = 3990104;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private static async Task<T> Next<T>(BotClient bot, uint id, MessageParser<T> parser) where T : IMessage<T> =>
        parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(id), Wait)).Body);

    /// <summary>A level-1 Novice with the gear in its bag (or worn, where a grid is given), appraised; then in the world.</summary>
    private static async Task<(BotClient Bot, GameSession Session, InvItem[] Items)> EnterAsync(SyntheticHost host,
        DBEquipInfo? plans, params (int Entry, int Grid)[] gear)
    {
        var bot = host.NewBot();
        await bot.LoginAsync("alice", "pw");
        await bot.EnterGateAsync();
        long role = (await bot.CreateRoleAsync("Alice")).RoleBaseInfo.Id;
        var roles = host.Service<RoleStore>();
        var progress = roles.LoadProgress(role);
        progress.StarterKit = true;
        roles.SaveProgress(role, progress);
        var store = host.Service<ItemStore>();
        var items = gear.Select((g, n) => new InvItem
        {
            Entry = g.Entry, Stack = 1, Pack = 2, Grid = g.Grid, Sort = n + 1, Attrs = new() { [CharacterService.AttrAppraise] = 1 },
        }).ToArray();
        foreach (var item in items) store.Insert(role, item);
        if (plans != null)
        {
            foreach (var plan in plans.EquipPlans.Values)
                foreach (var (grid, index) in plan.ItemsEquipGrid.ToArray()) plan.ItemsEquipGrid[grid] = items[index].Guid;
            roles.SaveEquipPlans(role, plans.ToByteArray());
        }
        await bot.EnterWorldAsync(role);
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role?.Id == role);
        return (bot, session, items);
    }

    private static async Task<int> EquipAsync(BotClient bot, InvItem item)
    {
        await bot.SendGameAsync(CharacterService.EquipItemOpr, new EquipItemOprReq { ItemGUID = item.Guid, Opr = EquipItemOprType.EquipOprTypeEquip });
        return (await Next(bot, CharacterService.EquipItemOpr, EquipItemOprResp.Parser)).Result;
    }

    private static int Grid(GameSession session, InvItem item) => session.Items.Single(i => i.Guid == item.Guid).Grid;

    [Fact]
    public async Task EquippingRefusesGearTheRoleMayNotWear()
    {
        await using var host = await SyntheticHost.StartAsync();
        var (bot, session, items) = await EnterAsync(host, null, (HighLevel, 0), (ThiefOnly, 0), (OtherSex, 0), (Free, 0));
        await using var _ = bot;
        Assert.Equal(ErrorCodes.EquipLevel, await EquipAsync(bot, items[0]));
        Assert.Equal(ErrorCodes.EquipJob, await EquipAsync(bot, items[1]));
        Assert.Equal(ErrorCodes.EquipSex, await EquipAsync(bot, items[2]));
        Assert.Equal(0, await EquipAsync(bot, items[3]));
        Assert.Equal([0, 0, 0], items.Take(3).Select(i => Grid(session, i)));
        Assert.NotEqual(0, Grid(session, items[3]));
    }

    [Fact]
    public async Task GearWornAgainstItsRulesComesOffAtLoginAndLeavesTheCurrentPlan()
    {
        await using var host = await SyntheticHost.StartAsync();
        var plans = new DBEquipInfo
        {
            CurrentEquipPlan = 0, Inited = true,
            EquipPlans = { [0] = new DBEquipPlan { ItemsEquipGrid = { [1] = 0, [4] = 1, [2] = 2 } }, [1] = new DBEquipPlan(), [2] = new DBEquipPlan() },
        };
        var (bot, session, items) = await EnterAsync(host, plans, (Free, 1), (OtherSex, 4), (HighLevel, 2));
        await using var _ = bot;
        Assert.Equal([1, 0, 0], items.Select(i => Grid(session, i)));
        var current = session.Profile.EquipmentPlans.EquipPlans[0].ItemsEquipGrid;
        Assert.Equal(items[0].Guid, Assert.Single(current).Value);
        // Durable: the stored items and plans say the same.
        var stored = host.Service<ItemStore>().Load(session.Role!.Id);
        Assert.Equal([1, 0, 0], items.Select(i => stored.Single(s => s.Guid == i.Guid).Grid));
        var saved = DBEquipInfo.Parser.ParseFrom(host.Service<RoleStore>().LoadEquipPlans(session.Role.Id));
        Assert.Single(saved.EquipPlans[0].ItemsEquipGrid);
    }

    [Fact]
    public async Task APlanWithGearTheRoleMayNotWearCannotBeSelected()
    {
        await using var host = await SyntheticHost.StartAsync();
        var plans = new DBEquipInfo
        {
            CurrentEquipPlan = 0, Inited = true,
            EquipPlans = { [0] = new DBEquipPlan(), [1] = new DBEquipPlan { ItemsEquipGrid = { [1] = 0 } }, [2] = new DBEquipPlan() },
        };
        var (bot, session, items) = await EnterAsync(host, plans, (HighLevel, 0));
        await using var _ = bot;
        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = 1 });
        var reply = await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser);
        Assert.Equal((ErrorCodes.EquipLevel, 0), (reply.Result, reply.CurrentPlan));
        Assert.Equal(0, Grid(session, items[0]));
        // An empty plan is always open.
        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = 2 });
        Assert.Equal(0, (await Next(bot, CharacterService.EquipSwitchPlan, EquipSwitchPlanResp.Parser)).Result);
    }
}
