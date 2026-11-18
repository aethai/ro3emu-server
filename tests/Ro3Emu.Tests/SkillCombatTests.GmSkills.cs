using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

public sealed partial class SkillCombatTests
{
    [ClientDataFact] // #461: the chat alias of the GM panel's 110 learns the job's skills at their max level, "0" clears them
    public async Task SkillsMaxLearnsAndClearsTheJobsSkills()
    {
        await using var alice = await InWorld("Alice");
        await Gm("alice", "!lvl 99 40"); await Gm("alice", "!job 1400");
        await Gm("alice", "!skillsmax");
        Assert.Matches(@"^[1-9]\d* skills learned at their max level", await Reply(alice, "skills learned"));
        await Gm("alice", "!skillsmax 0");
        Assert.Equal("Skills cleared to their free levels.", await Reply(alice, "cleared"));
    }

    private static async Task<string> Reply(Ro3Emu.Bot.BotClient bot, string part) => BroadCastNotify.Parser.ParseFrom((await bot.Inbox.WaitAsync(
        e => e.IsGame(GmCommands.BroadcastNotify) && BroadCastNotify.Parser.ParseFrom(e.Body).Content.Contains(part, StringComparison.Ordinal), Wait)).Body).Content;
}
