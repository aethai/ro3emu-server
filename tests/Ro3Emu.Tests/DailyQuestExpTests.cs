using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>
/// The daily side quests (TaskService.DailyQuest.cs): the first DailyQuests.PerDay quests a role turns in each game day
/// pay extra base EXP — DailyQuests.ExpPercent of the EXP its base level needs — and the count starts over on the next
/// game day. The count rides in TaskRuntimeState, so it commits with the delivery that paid it.
/// </summary>
public sealed class DailyQuestExpTests : IAsyncLifetime
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private const long FirstTask = 10101010; // the main story root, accepted when the character is created

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ro3emu-dailyquest-" + Guid.NewGuid().ToString("N"));
    private WebApplication _app = null!;
    private string _http = "";
    private BotClient _bot = null!;

    private T Service<T>() where T : notnull => _app.Services.GetRequiredService<T>();
    private GameSession Session => Service<SessionRegistry>().All().Single(s => s.Account == "daley" && s.Role != null);
    private Task<T> Read<T>(Func<GameSession, T> read) => Service<GameLoop>().InvokeAsync(() => Task.FromResult(read(Session)));
    private Task Run(Action<GameSession> act) => Read(s => { act(s); return 0; });
    private Task Gm(string line) => QuestProbe.GmAsync(_http, "daley", line);

    public async Task InitializeAsync()
    {
        var (http, micro, game) = TestPorts.FreePorts();
        _app = ServerHost.Create([], o =>
        {
            o.DataDir = _dir; o.GameDataDir = Path.Combine(ClientData.RepoRoot, "data");
            o.HttpPort = http; o.MicroGatePort = micro; o.GameGatePort = game;
            o.DailyQuests = new DailyQuestOptions { PerDay = 2, ExpPercent = 100 };
        });
        await _app.StartAsync();
        _http = $"http://127.0.0.1:{http}";
        _bot = new BotClient(_http, File.ReadAllText(Path.Combine(_dir, "gate_rsa_public.pem")));
        await _bot.LoginAsync("daley", "pw");
        await _bot.EnterGateAsync();
        await _bot.EnterWorldAsync((await _bot.CreateRoleAsync("Daley")).RoleBaseInfo.Id);
    }

    public async Task DisposeAsync()
    {
        await _bot.DisposeAsync();
        await _app.StopAsync();
        await _app.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static async Task Until(Func<Task<bool>> done, string what)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (!await done())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException(what);
            await Task.Delay(25);
        }
    }

    /// <summary>Base EXP earned since level 1 (BaseLvConfig._iBaseExp of every level passed, plus the current EXP).</summary>
    private Task<long> TotalExp() => Read(s => s.Progress!.Exp + Enumerable.Range(1, s.Progress.Level - 1)
        .Sum(level => GameData.Int(Service<GameData>().Row("BaseLvConfig", level)!.Value, "_iBaseExp")));

    /// <summary>The lowest id the role runs and has not delivered yet; the story chain hands one out after each turn-in.</summary>
    private Task<long> NextQuest() => Read(s => s.Tasks!.Infos
        .Where(kv => kv.Value.Status == TaskState.Running)
        .Select(kv => kv.Key).DefaultIfEmpty(0).Min());

    private async Task<TaskDelivered> Deliver(long task)
    {
        long before = await TotalExp();
        await Gm($"!quest done {task}");
        await Until(() => Read(s => TaskService.IsDelivered(s, task)), $"quest {task} delivered");
        return new TaskDelivered(await TotalExp() - before, await Read(s => s.TaskRuntime.DailyQuestCount));
    }

    private readonly record struct TaskDelivered(long Exp, int Count);

    /// <summary>Two quests a day pay a level's worth each; the third pays only its own reward, and a new day pays again.</summary>
    [ClientDataFact]
    public async Task QuestsPayTheBonusUntilTheDaysAllowanceIsSpent()
    {
        var data = Service<GameData>();
        await Gm("!lvl 40");
        long levelNeed = GameData.Int(data.Row("BaseLvConfig", 40)!.Value, "_iBaseExp");
        Assert.True(levelNeed > 0, "level 40 needs EXP");

        var first = await Deliver(FirstTask);
        Assert.True(first.Exp >= levelNeed, $"first quest paid {first.Exp}, the bonus alone is {levelNeed}");
        Assert.Equal(1, first.Count);

        var second = await Deliver(await NextQuest());
        Assert.True(second.Exp >= levelNeed, $"second quest paid {second.Exp}, the bonus alone is {levelNeed}");
        Assert.Equal(2, second.Count);

        var third = await Deliver(await NextQuest());
        Assert.True(third.Exp < levelNeed, $"third quest paid {third.Exp}: the day's allowance was {2} quests");
        Assert.Equal(2, third.Count); // spent for the day, not reset by a delivery

        await Run(s => s.TaskRuntime.DailyQuestDay -= 86_400); // an earlier game day: the count belongs to it
        var nextDay = await Deliver(await NextQuest());
        Assert.True(nextDay.Exp >= levelNeed, $"first quest of the new day paid {nextDay.Exp}, the bonus alone is {levelNeed}");
        Assert.Equal(1, nextDay.Count);
    }

    /// <summary>The bonus is ExpPercent of the level's own BaseLvConfig row; a level past the table takes the cap level's.</summary>
    [ClientDataFact]
    public void DailyQuestExpFollowsTheLevelRow()
    {
        var data = Service<GameData>();
        long need40 = GameData.Int(data.Row("BaseLvConfig", 40)!.Value, "_iBaseExp");
        Assert.Equal(need40 / 4, TaskService.DailyQuestExp(data, 40, 25));
        Assert.Equal(need40, TaskService.DailyQuestExp(data, 40, 100));
        long lastWithExp = Enumerable.Range(1, 100).Select(l => GameData.Int(data.Row("BaseLvConfig", l)!.Value, "_iBaseExp"))
            .Last(exp => exp > 0);
        Assert.Equal(lastWithExp, TaskService.DailyQuestExp(data, 100, 100)); // the cap level pays the last level's EXP
        Assert.Equal(1, TaskService.DailyQuestExp(data, 40, 0)); // a percent that would pay nothing still pays 1
        Assert.True(TaskService.DailyQuestExp(data, 1, 100) >= 1, "level 1 pays at least 1");
    }
}
