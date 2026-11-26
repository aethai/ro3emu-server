using Romsg;
using Ro3Emu.Server.Game;

namespace Ro3Emu.Tests;

/// <summary>#409, #410: the cards as a player sockets them, through 31406, and read from the hit the client gets.</summary>
public sealed partial class CardRulesTests
{
    // Card: Maya T0: "When hit by a player's ST magic skill, reflects 32% of the damage taken, up to 35% of the attacker's Max Life.
    // For every 1 VIT, reflects an Extra 0.1% of the damage taken, up to 60%." Fire Bolt is ST and magic.
    [ClientDataFact]
    public async Task MayaReflectsAPlayersSingleTargetMagicSkill()
    {
        var (alice, a, bob, b) = await ReflectDuel(Maya, 2, 0, 2100, 12004);
        await using (alice)
        await using (bob)
        {
            long vit = await Attribute(b, 3), maxHp = await Attribute(a, 101);
            var taken = await CastAt(alice, a, 1200401, b);
            Assert.True(taken.Damage >= 100, "too little damage to read a share of: " + taken.Damage);
            var reflected = await Next(alice, CombatService.SkillHit, SkillHitInfo.Parser, h => h.SrcID == b.Role!.Id);
            long share = Math.Min(3200 + 10 * vit, 6000);
            Assert.Equal(Math.Min((long)Math.Floor(taken.Damage * share / 10000d), maxHp * 3500 / 10000), Assert.Single(reflected.Dsts).Damage);
            Assert.Equal(a.Role!.Id, reflected.Dsts[0].DstID);
        }
    }

    // Card: Drake T0: "Damage against Small, Medium, and Large monsters +7.5%". The same hit before and after the real socket message, at
    // each star, on a monster of each size, as the hit path asks every adjuster.
    [ClientDataFact]
    public async Task DrakeRaisesTheHitOnceSocketedByThePlayer()
    {
        const int drake = 1460100;
        var (bot, session) = await Holder("Bob", drake, 1);
        await using (bot)
        {
            var monsters = Service<MonsterService>();
            var effects = Service<CardEffects>();
            var normal = Service<Ro3Emu.Server.Data.GameData>().Row("SkillConfig", 1000001)!.Value;
            for (int star = 0; star <= 3; star++)
            {
                await AtStar(session, drake, star);
                long expected = new long[] { 10750, 10850, 11000, 11150 }[star];
                foreach (int size in new[] { 1, 2, 3 })
                {
                    var template = monsters.Template(5000002)! with { Size = size };
                    var unit = await Service<GameLoop>().InvokeAsync(async () => (await monsters.SpawnSingleAsync(session.SceneEntry, 5000002,
                        new Ro3Emu.Server.Maps.MapPoint(session.Position.X, session.Position.Y, session.Position.Z, 0), session.InstanceId, customTemplate: template))!);
                    long raised = await OnLoop(() => effects.AdjustDamage(session, unit, normal, DamageHitResult.Normal, 10000));
                    Assert.Equal(expected, raised);
                }
            }
        }
    }
}
