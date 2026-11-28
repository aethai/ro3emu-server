using System.Runtime.CompilerServices;
using System.Text.Json;
using Ro3Emu.Server.Data.Config;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Server.Game;

/// <summary>The resonance rows that add a later or a following hit: the Arch Bishop's songs and the Mechanic's Melody Bubble.</summary>
public sealed partial class ResonancePassives
{
    // SkillConfig "Musical Phrase" rows of one, two and three phrases (Baptism's description links skill/11431701).
    private static readonly int[] PhraseRows = [11431701, 11431801, 11431901];
    private const int OrganumRow = 11431101; // SkillConfig "Song of Suffering - Organum", linked by Organum's description
    private const int VoicePart = 11431701, LowHum = 11431301; // BuffConfig, linked by Baptism's description
    private const int VoicePartGroup = 114317;
    private const int MelodyBubble = 1631601; // BuffConfig "Melody Bubble"
    private const long Neutral = 1; // the client's element enum: None 1

    /// <summary>
    /// Baptism: "Each time you cast [Judex], [Sacrament] will Launch @{1}~@{2} [Phrases] in sequence. Each HIT grants you
    /// one stack of [Voice Part] and one stack of [Low Hum]." Voice Part: "Each floor adds one phrase launched by a
    /// Sacrament." So a Judex launches @{1} phrase and one more per Voice Part stack, up to @{2}, as the row of that many
    /// phrases; each phrase that lands grants a stack of both (inferred). "Gain the accessory [Sacrament]" is the
    /// client's pendant.
    /// </summary>
    private async Task PhrasesAsync(GameSession s, ActiveSkillCast action, IReadOnlyList<MonsterUnit> processed)
    {
        var target = processed.FirstOrDefault(m => m.Uid == action.Request.TargetId) ?? (processed.Count > 0 ? processed[0] : null);
        if (target is not { Hp: > 0 }) return;
        int fewest = (int)Math.Max(1, Arg(EmblemService.Baptism, 0));
        int most = Math.Min(PhraseRows.Length, (int)Math.Max(fewest, Arg(EmblemService.Baptism, 1)));
        int phrases = Math.Clamp(fewest + Piles(s, VoicePartGroup), fewest, most);
        if (SkillConfig.Get(_data, PhraseRows[phrases - 1]) is not { } phrase) return;
        long before = target.Hp;
        await _combat.CastExtraAsync(s, phrase.Row, [target]);
        if (target.Hp >= before) return;
        int voiceMs = (int)(EmblemService.DescribedValue(BuffConfig.Get(_data, VoicePart)?.DescData ?? [], 0) * 1000);
        int humMs = (int)((BuffConfig.Get(_data, LowHum)?.DescriptionArgs() is [var seconds, ..] ? seconds : 0) * 1000);
        if (s.Role is not { } role) return;
        for (int i = 0; i < phrases; i++)
        {
            await _buffs.AddAsync(s, VoicePart, voiceMs, role.Id);
            await _buffs.AddAsync(s, LowHum, humMs, role.Id);
        }
    }

    /// <summary>
    /// Organum: "After your [Stigmata] ends, trigger [Organum] once more in the reverse direction." Holy Stigma has no
    /// time of its own; it ends with its show (skill_4314 m_fTotalTime, 3 s after its chant). Organum (11431101, a 7 m
    /// circle, so the direction is its show's) then strikes up to its _iTargetMax monsters around the Stigma's point
    /// (inferred).
    /// </summary>
    private void OrganumAfterStigma(GameSession s, ActiveSkillCast action)
    {
        if (SkillConfig.Get(_data, OrganumRow) is not { } organum) return;
        var stigma = new SkillConfig(action.Row);
        long ends = action.StartMs + action.ChantMs + (long)(SkillShow.TotalTime(_data.SkillTemplate(stigma.ScriptShow)) * 1000);
        var center = (action.Request.TargetPos ?? s.Position).Clone();
        float radius = (organum.Range is [var range, ..] ? range : 0) / 100f;
        LaterHit(s, TimeSpan.FromMilliseconds(Math.Max(0, ends - _loop.NowMs)), async () =>
        {
            if (!Holds(s, EmblemService.Organum)) return;
            var targets = _combat.MonstersNear(s, center, radius).Where(m => m.Hp > 0).Take((int)Math.Max(1, organum.TargetMax)).ToList();
            if (targets.Count > 0) await _combat.CastExtraAsync(s, organum.Row, targets);
        }, "organum");
    }

    private int Piles(GameSession s, int group)
    {
        long now = _loop.NowMs;
        return s.Buffs.Where(b => b.ExpireMs > now && b.ConfigId / 100 == group).Sum(b => b.PileCount);
    }

    /// <summary>One caster's Melody Bubble on a monster: its stacks, until when, and the cannon row it came from.</summary>
    private sealed class Bubble(GameSession caster, JsonElement row, long untilMs)
    {
        public GameSession Caster { get; } = caster;
        public JsonElement Row { get; } = row;
        public long UntilMs { get; } = untilMs;
        public int Stacks { get; set; } = 1;
    }

    private readonly ConditionalWeakTable<MonsterUnit, List<Bubble>> _bubbles = [];

    /// <summary>
    /// Melody Cannon: "Enhance [Cannon] into [Melody Cannon]" (its mode rows, ArmCannonS1BD*). "After a successful HIT,
    /// applies [Melody Bubble] to the target for @{1} sec." One bubble per caster on a monster, up to the buff's
    /// _kMaxMultiSourceCount casters; a monster that already carries the caster's keeps it (inferred).
    /// </summary>
    private async Task ApplyBubblesAsync(GameSession s, JsonElement row, IReadOnlyList<MonsterUnit> struck)
    {
        int ms = (int)(Arg(EmblemService.MelodyCannon, 0) * 1000);
        if (ms <= 0 || s.Role is not { } role) return;
        int sources = BuffConfig.Get(_data, MelodyBubble)?.MaxMultiSourceCount is [var cap, ..] && cap > 0 ? (int)cap : int.MaxValue;
        foreach (var monster in struck.Where(m => m.Hp > 0))
        {
            var bubbles = _bubbles.GetOrCreateValue(monster);
            if (bubbles.Count >= sources || bubbles.Any(b => b.Caster == s)) continue;
            var bubble = new Bubble(s, row, _loop.NowMs + ms);
            bubbles.Add(bubble);
            await _buffs.AddSupportAsync(monster, MelodyBubble, ms, role.Id);
            // "... it explodes and deals damage when its duration ends."
            LaterHit(s, TimeSpan.FromMilliseconds(ms), () => BurstAsync(monster, bubble), "melody bubble");
        }
    }

    /// <summary>"Each time an enemy carrying [Melody Bubble] takes damage, the bubble expands. Upon reaching @{2} stacks, it
    /// explodes". The burst lands after the hit that filled it (ResonancePassives.LaterHit); its damage expands the others.</summary>
    private async Task BubblesTakeDamageAsync(GameSession s, MonsterUnit monster, long amount)
    {
        if (amount <= 0 || !_bubbles.TryGetValue(monster, out var bubbles) || bubbles.Count == 0) return;
        int most = (int)Arg(EmblemService.MelodyCannon, 1);
        foreach (var bubble in bubbles.ToArray())
        {
            if (++bubble.Stacks >= most)
            {
                LaterHit(bubble.Caster, TimeSpan.Zero, () => BurstAsync(monster, bubble), "melody bubble");
                continue;
            }
            if (monster.Hp > 0 && bubble.Caster.Role is { } role && bubble.UntilMs > _loop.NowMs)
                await _buffs.AddSupportAsync(monster, MelodyBubble, (int)(bubble.UntilMs - _loop.NowMs), role.Id); // one more pile
        }
    }

    /// <summary>"... dealing @{3} instances of Neutral Ranged Physical DMG equal to Physical ATK*@{4}%. If [Melody Bubble]
    /// does not fully expand and explode, it explodes and deals damage when its duration ends." The hit is the cannon's
    /// row with that damage. A bubble bursts once, whichever comes first.</summary>
    private async Task BurstAsync(MonsterUnit monster, Bubble bubble)
    {
        if (!_bubbles.TryGetValue(monster, out var bubbles) || !bubbles.Remove(bubble)) return;
        var caster = bubble.Caster;
        if (caster.Role is not { } role) return;
        await _buffs.RemoveUnitSourceAsync(monster, MelodyBubble, role.Id);
        if (monster.Hp <= 0 || caster.WorldScene != monster.WorldScene) return;
        var burst = new SkillConfig(bubble.Row).AsDescribedHit((long)(Arg(EmblemService.MelodyCannon, 3) * 100), 0, Neutral);
        for (int i = 0; i < Math.Max(1, (int)Arg(EmblemService.MelodyCannon, 2)) && monster.Hp > 0; i++)
            await _combat.CastExtraAsync(caster, burst, [monster]);
    }
}
