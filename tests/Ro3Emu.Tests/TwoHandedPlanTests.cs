using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// A saved equipment plan lists a two-handed weapon in both weapon grids, as the client writes one
/// (LC_SchemeMatchHelper.PutOnEquip). Invented items on the synthetic fixture.
/// </summary>
public sealed class TwoHandedPlanTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-twohand-" + Guid.NewGuid().ToString("N"));
    private const int Greatsword = 9300001, Sword = 9300002, Shield = 9300003;
    private const long GreatswordGuid = 71, SwordGuid = 72, ShieldGuid = 73;
    private const int WeaponPower = 1234;
    private SyntheticHost? _host;

    public async ValueTask DisposeAsync()
    {
        if (_host != null) await _host.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private async Task<SyntheticHost> Start()
    {
        foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest);
        }
        string path = Path.Combine(_root, "config", "ItemConfig.json");
        var items = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        // _iEquipPart is enum_EquipPosType: TwoHand 1, MainHand 2, ViceHand 3.
        foreach (var (entry, part) in new[] { (Greatsword, 1), (Sword, 2), (Shield, 3) })
            items[entry.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(new
            {
                _iID = entry, _iType = CharacterService.ItemTypeEquip, _iEquipPart = part, _iStackLimit = 1, _iEquipId = entry,
            });
        File.WriteAllText(path, items.ToJsonString());
        string equipPath = Path.Combine(_root, "config", "EquipConfig.json");
        var equips = JsonNode.Parse(File.ReadAllText(equipPath))!.AsObject();
        foreach (int entry in new[] { Greatsword, Sword, Shield })
            equips[entry.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(new { _iID = entry, _iPower = WeaponPower });
        File.WriteAllText(equipPath, equips.ToJsonString());
        return _host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
    }

    private static List<InvItem> Bag() =>
    [
        new() { Guid = GreatswordGuid, Entry = Greatsword, Pack = 2, Stack = 1 },
        new() { Guid = SwordGuid, Entry = Sword, Pack = 2, Stack = 1 },
        new() { Guid = ShieldGuid, Entry = Shield, Pack = 2, Stack = 1 },
    ];

    private static DBEquipInfo Plans(params (int Grid, long Guid)[] target)
    {
        var state = new DBEquipInfo { CurrentEquipPlan = 0 };
        state.EquipPlans[0] = new DBEquipPlan();
        state.EquipPlans[1] = new DBEquipPlan();
        foreach (var (grid, guid) in target) state.EquipPlans[1].ItemsEquipGrid[grid] = guid;
        return state;
    }

    [Fact]
    public async Task TheGrid2RepeatOfATwoHanderIsTheSameWeaponAndNothingElseSharesItsGrids()
    {
        var character = (await Start()).Service<CharacterService>();
        var progress = new RoleProgress { Level = 50 };
        int Stage(out List<InvItem> bag, params (int, long)[] target)
        {
            bag = Bag();
            return character.StageEquipmentPlan(Plans(target), bag, 1, progress, 0);
        }

        Assert.Equal(0, Stage(out var bag, (1, GreatswordGuid), (2, GreatswordGuid)));
        Assert.Equal(1, bag.Single(i => i.Guid == GreatswordGuid).Grid);
        Assert.Equal(0, Stage(out bag, (2, GreatswordGuid), (1, GreatswordGuid))); // in either order
        Assert.Equal(1, bag.Single(i => i.Guid == GreatswordGuid).Grid);
        Assert.Equal(ErrorCodes.EquipSlot, Stage(out _, (2, GreatswordGuid)));                 // grid 2 alone is no grid for it
        Assert.Equal(ErrorCodes.EquipSlot, Stage(out _, (1, GreatswordGuid), (2, ShieldGuid))); // a two-hander leaves no off hand
        Assert.Equal(ErrorCodes.EquipSlot, Stage(out _, (1, SwordGuid), (2, SwordGuid)));       // only a two-hander repeats
        Assert.Equal(0, Stage(out bag, (1, SwordGuid), (2, ShieldGuid)));
        Assert.Equal((1, 2), (bag.Single(i => i.Guid == SwordGuid).Grid, bag.Single(i => i.Guid == ShieldGuid).Grid));
    }

    [Fact]
    public async Task A31413SwitchWearsATwoHanderListedInBothWeaponGrids()
    {
        var host = await Start();
        await using var bot = await host.InWorldAsync("Wielder");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var loop = host.Service<GameLoop>();
        long greatsword = await loop.InvokeAsync(async () =>
        {
            await host.Service<CharacterService>().GrantAsync(session, [(Greatsword, 1)]);
            long guid = session.Items.Single(i => i.Entry == Greatsword).Guid;
            var plans = session.Profile.EquipmentPlans;
            plans.EquipPlans[1].ItemsEquipGrid[1] = guid;
            plans.EquipPlans[1].ItemsEquipGrid[2] = guid;
            return guid;
        });
        await bot.SendGameAsync(CharacterService.EquipSwitchPlan, new EquipSwitchPlanReq { TargetIndex = 1 });
        var reply = EquipSwitchPlanResp.Parser.ParseFrom((await bot.Inbox.WaitAsync(e => e.IsGame(CharacterService.EquipSwitchPlan),
            TimeSpan.FromSeconds(5))).Body);
        Assert.Equal((0, 1), (reply.Result, reply.CurrentPlan));
        var worn = await loop.InvokeAsync(() => Task.FromResult(session.Items.Where(i => i.Grid > 0).ToList()));
        Assert.Equal(greatsword, Assert.Single(worn).Guid);
        Assert.Equal(1, worn[0].Grid);
    }

    [Fact]
    public async Task ATwoHanderListedInBothWeaponGridsCountsOnceInPlanPower()
    {
        var host = await Start();
        await using var bot = await host.InWorldAsync("Wielder");
        var session = host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        var (single, both) = await host.Service<GameLoop>().InvokeAsync(async () =>
        {
            await host.Service<CharacterService>().GrantAsync(session, [(Greatsword, 1)]);
            long guid = session.Items.Single(i => i.Entry == Greatsword).Guid;
            var info = new DBEquipInfo();
            info.EquipPlans[0] = new DBEquipPlan { ItemsEquipGrid = { [1] = guid } };
            info.EquipPlans[1] = new DBEquipPlan { ItemsEquipGrid = { [1] = guid, [2] = guid } };
            session.PlanPowers!(CharacterService.EquipSync, info);
            return (info.EquipPlans[0].Power.NormalPower, info.EquipPlans[1].Power.NormalPower);
        });
        Assert.True(single >= WeaponPower);
        Assert.Equal(single, both);
    }
}
