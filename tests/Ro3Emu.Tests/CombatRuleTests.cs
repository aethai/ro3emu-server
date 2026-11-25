using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Romsg;
using Ro3Emu.Server;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

public sealed class CombatRuleTests
{
    [Fact]
    public async Task EveryProductionCombatRuleIsRegisteredInDiAndBoundToCombatService()
    {
        var expectedTypes = typeof(ServerHost).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract
                && typeof(ICombatRule).IsAssignableFrom(t)
                && t != typeof(DelegateCombatRule))
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(expectedTypes);

        await using var server = await TestServer.StartSyntheticAsync();
        var diTypes = server.App.Services.GetServices<ICombatRule>()
            .Select(r => r.GetType())
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();
        var boundTypes = server.Service<CombatService>().Rules
            .Select(r => r.GetType())
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expectedTypes, diTypes);
        Assert.Equal(expectedTypes, boundTypes);
    }

    [Fact]
    public async Task CombatServiceComposesRulesWithExpectedSemantics()
    {
        await using var server = await TestServer.StartSyntheticAsync();
        var combat = server.Service<CombatService>();

        var a = new GameSession(null!, null!);
        var b = new GameSession(null!, null!);
        var monster = new MonsterUnit
        {
            Uid = 1,
            Template = new MonsterTemplate(10001, 1, 10, 0, 1, 1, 0, 0, 1, 0, 0, 100, 1, 1, 1, 1,
                false, 0, 1000, 5, 10, 0, 1000, 0.1f, 1000, 1, 500),
            Group = new SpawnGroup(-1, false, 1, 0, [], []),
            Home = new SpawnPoint(-1, 0, 0, 0, 0, 10001),
            Hp = 10,
        };

        int killedCalls = 0;
        int castCalls = 0;
        int landCalls = 0;

        var allowAll = new DelegateCombatRule
        {
            PlayerHostile = (_, _) => false,
            MonsterTargetAllowed = (_, _) => true,
            EventCanFight = (_, _) => false,
            EventPlayerKilled = (_, _) => { killedCalls++; return Task.CompletedTask; },
            MatchSkillRule = (_, _) => null,
            SkillLineBlocked = (_, _, _, _) => false,
            OwnSkillAdmit = (_, _) => null,
            SameParty = (_, _) => false,
        };
        var activeRule = new DelegateCombatRule
        {
            PlayerHostile = (x, y) => x == a && y == b,
            MonsterTargetAllowed = (_, m) => m != monster,
            EventCanFight = (x, y) => x == a && y == b,
            EventPlayerKilled = (_, _) => { killedCalls += 10; return Task.CompletedTask; },
            MatchSkillRule = (_, id) => id == 99 ? true : null,
            SkillLineBlocked = (_, _, _, _) => true,
            OwnSkillAdmit = (_, id) => id == 77 ? 0 : null,
            OwnSkillCast = (_, _) => { castCalls++; return Task.CompletedTask; },
            OwnSkillLanded = (_, _) => { landCalls++; return Task.CompletedTask; },
            SameParty = (x, y) => x == a && y == b,
        };

        using (combat.OverrideRulesForTest(allowAll, activeRule))
        {
            Assert.True(combat.IsPlayerHostile(a, b));
            Assert.False(combat.IsPlayerHostile(b, a));
            Assert.False(combat.IsMonsterTargetAllowed(a, monster));
            Assert.True(combat.CanEventFight(a, b));
            Assert.False(combat.CanEventFight(b, a));
            Assert.True(combat.IsSameParty(a, b));
            Assert.True(combat.EvaluateMatchSkillRule(a, 99));
            Assert.Null(combat.EvaluateMatchSkillRule(a, 100));
            Assert.True(combat.IsSkillLineBlocked(a, null, new Vec3(), new Vec3()));

            await combat.NotifyEventPlayerKilledAsync(a, b);
            Assert.Equal(11, killedCalls);

            var admitted = combat.TryAdmitOwnSkill(a, 77);
            Assert.NotNull(admitted);
            Assert.Equal(0, admitted.Value.Result);
            await admitted.Value.Rule.CastOwnSkillAsync(a, 77);
            await admitted.Value.Rule.LandOwnSkillAsync(a, default(JsonElement));
            Assert.Equal(1, castCalls);
            Assert.Equal(1, landCalls);
        }

        // Override restores the original DI-bound rules on dispose.
        Assert.Equal(6, combat.Rules.Length);
    }
}
