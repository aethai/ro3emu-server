using Ro3Emu.Server.Accounts.Persistence;
using System.Text.Json;
using Google.Protobuf;
using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;

namespace Ro3Emu.Server.Game;

/// <summary>
/// The Build window (MG_Genre.proto, LC_SchemeServerHelper.lua): 5008 keeps five composed
/// presets. Each child key is PBChildSystemType (1 skill, 2 stats, 3 gear, 4 cards, 5 totems, 6 echoes, 7 pets,
/// 8 talents). Mutating a preset and applying its child owners are separate operations.
/// </summary>
public sealed partial class GenreService(
    RoleStore roles, ItemStore items, CharacterService character, SkillService skills, CardService cards,
    PetService pets, EmblemService emblems, SeasonTalentService talents, TotemService totems, PowerService power,
    FunctionService functions, SessionRegistry sessions, GameData data, ILogger<GenreService> log)
{
    public const uint GenreSync = 5008;
    public const uint Seek = 82800, Respond = 82801, SendPlan = 82802, SaveRec = 82803, DeleteRec = 82804,
        DeleteSeeker = 82805, SwitchPlan = 82806, Code = 82807, MyRecs = 82808, Others = 82809, Concrete = 82810,
        RespondCode = 82812, ApplyRec = 82813, RenamePlan = 82816, UpdateNtf = 82822, Property = 82823, RenameRec = 82824,
        PlanPower = 82825, Season = 82826, School = 82827, QueryRecs = 82828;
    private const int PlanCount = 5; // (inferred) PublicParamConfig.SchemeNum, zero-based Build labels.
    private const int NameMax = 8; // PublicParamConfig.SchemeNameNumMax.
    private const int Skill = 1, Attr = 2, Equip = 3, Card = 4, Totem = 5, Emblem = 6, Pet = 7, Talent = 8;
    private const int FunctionGenre = 30;

    /// <summary>Load after the child owners so initial composite keys refer to actual selected plans.</summary>
    public async Task LoginAsync(GameSession session)
    {
        using var held = await CharacterService.Lock(session);
        var bytes = roles.LoadGenre(session.Role!.Id);
        var genre = bytes is null ? new DBGenre() : DBGenre.Parser.ParseFrom(bytes);
        genre.GenreMap ??= new DBGenreSystemMap();
        for (int index = 0; index < PlanCount; index++)
        {
            if (!genre.GenreMap.SystemMap.TryGetValue(index, out var plan)) genre.GenreMap.SystemMap[index] = plan = new DBGenrePlan();
            if (bytes is null || plan.GenrePlans.Count == 0)
                foreach (var (type, selected) in CurrentChildren(session)) plan.GenrePlans[type] = selected;
        }
        if (!genre.GenreMap.SystemMap.ContainsKey(genre.CurrentPlan)) genre.CurrentPlan = 0;
        genre.Inited = true;
        // The currently selected child combination is the only one guaranteed to represent today's effective state.
        RefreshPlan(session, genre.GenreMap.SystemMap[genre.CurrentPlan], CurrentChildren(session));
        await SaveGenreAsync(session, genre);
        await SyncAsync(session);
    }

    private static Dictionary<int, int> CurrentChildren(GameSession s) => new()
    {
        [Skill] = s.Combat.SkillPlans?.CurrentPlanIndex ?? 0,
        [Attr] = s.Profile.StatPlans.Info.CurPlanIndex,
        [Equip] = s.Profile.EquipmentPlans.CurrentEquipPlan,
        [Card] = s.Profile.CardPlans?.CurrentPlan ?? 1,
        [Totem] = s.Profile.Totems?.CurrentPlan ?? 0,
        [Emblem] = s.Profile.Emblems.CurrentPlan,
        [Pet] = checked((int)(s.Pets?.CurrentPlan ?? 0)),
        [Talent] = checked((int)(s.Season.SeasonTalent?.CurPlanId ?? 0)),
    };

    private static bool ValidName(string name) => !string.IsNullOrWhiteSpace(name) && !name.Any(char.IsControl)
        && name.EnumerateRunes().Count() <= NameMax;

    private Task SyncAsync(GameSession s) => CharacterService.SyncAsync(s, GenreSync, s.Profile.Genre!.Clone());

    private int Gate(GameSession s) => !functions.IsOpen(s, FunctionGenre) ? ErrorCodes.FeatureUnavailable : 0;

    private sealed class Stage(GameSession s, SkillService skills)
    {
        public RoleProgress Progress { get; } = CharacterService.GenreCloneProgress(s.Progress!);
        public BaseAttrInfo Attrs { get; } = s.Profile.StatPlans.Info.Clone();
        public DBSkillData Skills { get; } = skills.Build(s);
        public DBEquipInfo Equipment { get; } = s.Profile.EquipmentPlans.Clone();
        public DBStorageHoleInfo Cards { get; } = s.Profile.CardPlans!.Clone();
        public DBTotemInfo Totems { get; } = s.Profile.Totems!.Clone();
        public DBPetInfo Pets { get; } = s.Pets!.Clone();
        public EmblemService.EmblemState Echoes { get; } = JsonSerializer.Deserialize<EmblemService.EmblemState>(JsonSerializer.Serialize(s.Profile.Emblems))!;
        public DBSeasonTalent Talents { get; } = s.Season.SeasonTalent!.Clone();
        public List<InvItem> Items { get; } = [.. s.Items.Select(i => i.Clone())];
    }

    /// <summary>Validate all requested owners on detached copies before any database or live-session mutation.</summary>
    private int StageSelection(GameSession s, Stage next, IReadOnlyDictionary<int, int> selected, IReadOnlySet<int>? replaced = null)
    {
        if (selected.Keys.Any(k => k is < Skill or > Talent)) return ErrorCodes.Parameter;
        int code;
        if (selected.TryGetValue(Attr, out int attr) && (code = character.StageStatPlan(next.Attrs, next.Progress, attr)) != 0) return code;
        if (selected.TryGetValue(Skill, out int skill) && (skill != next.Skills.CurrentPlanIndex || replaced?.Contains(Skill) == true)
            && (code = skills.StageSkillPlan(s, next.Skills, next.Progress, skill)) != 0) return code;
        if (selected.TryGetValue(Equip, out int equip) && (code = character.StageEquipmentPlan(next.Equipment, next.Items, equip, next.Progress, replaced?.Contains(Equip) == true)) != 0) return code;
        if (selected.TryGetValue(Card, out int card) && (code = cards.StageGenrePlan(s, next.Cards, next.Items, card, replaced?.Contains(Card) == true)) != 0) return code;
        if (selected.TryGetValue(Totem, out int totem) && (totem != next.Totems.CurrentPlan || replaced?.Contains(Totem) == true))
        {
            var old = s.Profile.Totems;
            try { s.Profile.Totems = next.Totems; code = totems.ValidatePlan(s, totem); }
            finally { s.Profile.Totems = old; }
            if (code != 0) return code;
            next.Totems.CurrentPlan = totem;
            TotemService.ApplyMarkers(next.Items, next.Totems, skills.Clock.GetUtcNow().ToUnixTimeMilliseconds());
        }
        if (selected.TryGetValue(Emblem, out int emblem) && (emblem != next.Echoes.CurrentPlan || replaced?.Contains(Emblem) == true))
        {
            if ((code = emblems.ValidateGenrePlan(s, next.Echoes, emblem)) != 0) return code;
            emblems.ApplyGenreMarkers(next.Items, next.Echoes);
        }
        if (selected.TryGetValue(Pet, out int pet) && (pet != next.Pets.CurrentPlan || replaced?.Contains(Pet) == true)
            && (code = pets.ValidateGenrePlan(s, next.Pets, pet)) != 0) return code;
        if (selected.TryGetValue(Talent, out int talent)
            && (code = talents.ValidateGenrePlan(s, next.Talents, talent, replaced?.Contains(Talent) == true)) != 0) return code;
        return 0;
    }

    /// <summary>Temporarily project detached, validated state for the existing combat stat calculator; no await occurs.</summary>
    private T WithProjection<T>(GameSession s, Stage next, Func<T> read)
    {
        var progress = s.Progress; var attr = s.Profile.StatPlans.Info; var skill = s.Combat.SkillPlans;
        var equip = s.Profile.EquipmentPlans; var cards = s.Profile.CardPlans; var totem = s.Profile.Totems;
        var pet = s.Pets; var echo = s.Profile.Emblems; var talent = s.Season.SeasonTalent; var owned = s.Items;
        try
        {
            s.Progress = next.Progress; s.Profile.StatPlans.Info = next.Attrs; s.Combat.SkillPlans = next.Skills;
            s.Profile.EquipmentPlans = next.Equipment; s.Profile.CardPlans = next.Cards; s.Profile.Totems = next.Totems;
            s.Pets = next.Pets; s.Profile.Emblems = next.Echoes; s.Season.SeasonTalent = next.Talents; s.Items = next.Items;
            return read();
        }
        finally
        {
            s.Progress = progress; s.Profile.StatPlans.Info = attr; s.Combat.SkillPlans = skill;
            s.Profile.EquipmentPlans = equip; s.Profile.CardPlans = cards; s.Profile.Totems = totem;
            s.Pets = pet; s.Profile.Emblems = echo; s.Season.SeasonTalent = talent; s.Items = owned;
        }
    }

    private Dictionary<int, long> Properties(GameSession s, Stage next) => WithProjection(s, next, () => new Dictionary<int, long>(character.Stats(s).Data));

    /// <summary>
    /// The selected children's power: the role's power package for the projected state minus what the client adds
    /// outside the plans (LC_PlayerInfoHelper.GetPlanOtherPower: base level, title, card suit). The cross-system
    /// aggregation itself is inferred (docs/genre-implementation.md).
    /// </summary>
    private PowerUnit Power(GameSession s, Stage next) => WithProjection(s, next, () => power.GenrePower(s));

    private void RefreshPlan(GameSession s, DBGenrePlan plan, IReadOnlyDictionary<int, int> selected)
    {
        var next = new Stage(s, skills);
        if (StageSelection(s, next, selected) != 0) return;
        plan.Property.Clear();
        foreach (var (id, value) in Properties(s, next)) plan.Property[id] = value;
        plan.Power = Power(s, next);
    }

    private int ApplyCooldownError(GameSession s, Stage next)
    {
        if (next.Skills.CurrentPlanIndex != s.Combat.SkillPlans!.CurrentPlanIndex)
        {
            int error = skills.PlanSwitchCooldownError(s);
            if (error != 0) return error;
        }
        if (next.Totems.CurrentPlan != s.Profile.Totems!.CurrentPlan)
        {
            int error = totems.PlanSwitchCooldownError(s);
            if (error != 0) return error;
        }
        if (next.Echoes.CurrentPlan != s.Profile.Emblems.CurrentPlan)
        {
            int error = emblems.PlanSwitchCooldownError(s);
            if (error != 0) return error;
        }
        return 0;
    }

    internal async Task SwitchAsync(GameSession s, SwitchGenrePlanReq req)
    {
        bool skillChanged = false;
        {
            using var held = await CharacterService.Lock(s);
            var state = s.Profile.Genre!;
            var reply = new SwitchGenrePlanResp { IsApply = req.IsApply, TargetPlan = req.TargetPlan };
            reply.ChildSystem.Add(req.ChildSystem);
            int code = Gate(s);
            if (code == 0 && !state.GenreMap.SystemMap.TryGetValue(req.TargetPlan, out _)) code = ErrorCodes.Parameter;
            var selected = state.GenreMap.SystemMap.TryGetValue(req.TargetPlan, out var preset)
                ? preset.GenrePlans.ToDictionary(kv => kv.Key, kv => kv.Value) : [];
            foreach (var (type, index) in req.ChildSystem) selected[type] = index;
            var next = new Stage(s, skills);
            if (code == 0) code = StageSelection(s, next, selected);
            if (code == 0 && req.IsApply) code = ApplyCooldownError(s, next);
            if (code == 0)
            {
                var genre = state.Clone();
                var plan = genre.GenreMap.SystemMap[req.TargetPlan];
                plan.GenrePlans.Clear();
                foreach (var (type, index) in selected) plan.GenrePlans[type] = index;
                if (req.IsApply) genre.CurrentPlan = req.TargetPlan;
                RefreshPlan(s, plan, selected);
                try
                {
                    if (req.IsApply)
                    {
                        bool changed = next.Skills.CurrentPlanIndex != s.Combat.SkillPlans!.CurrentPlanIndex;
                        await CommitAsync(s, next, genre);
                        skillChanged = changed;
                    }
                    else await SaveGenreAsync(s, genre);
                }
                catch (Exception e) when (e is Microsoft.Data.Sqlite.SqliteException or PersistenceException
                    or InvalidOperationException)
                {
                    log.LogError(e, "Genre build change failed for role {Role}", s.Role!.Id);
                    code = ErrorCodes.Unknown;
                }
            }
            reply.Result = code;
            await s.SendGameAsync(SwitchPlan, reply);
            if (code == 0)
            {
                await SyncAsync(s);
                if (req.IsApply) await PublishSyncAsync(s);
            }
        }
        // Skill observers (including cart capacity) may acquire StateLock after the atomic commit.
        if (skillChanged) await skills.OnActivePlanChangedAsync(s);
    }

    private async Task CommitAsync(GameSession s, Stage next, DBGenre genre)
    {
        long role = s.Role!.Id;
        bool skillChanged = next.Skills.CurrentPlanIndex != s.Combat.SkillPlans!.CurrentPlanIndex;
        bool totemChanged = next.Totems.CurrentPlan != s.Profile.Totems!.CurrentPlan;
        bool emblemChanged = next.Echoes.CurrentPlan != s.Profile.Emblems.CurrentPlan;
        bool talentChanged = next.Talents.CurPlanId != s.Season.SeasonTalent!.CurPlanId;
        // History and item markers are part of the same detached result, before notifications may await.
        next.Echoes.HistorySeasonPower = Math.Max(next.Echoes.HistorySeasonPower,
            WithProjection(s, next, () => emblems.GenrePower(s, next.Echoes).SeasonPower));
        var tx = roles.NewTransaction();
        int itemReceipt = tx.ItemDelta(role, s.Items, next.Items);
        roles.SaveGenre(role, genre.ToByteArray(), tx);
        roles.SaveStatPlans(role, next.Attrs.ToByteArray(), tx);
        roles.SaveSkillPlans(role, next.Skills.ToByteArray(), tx);
        roles.SaveProgress(role, next.Progress, tx);
        roles.SaveEquipPlans(role, next.Equipment.ToByteArray(), tx);
        roles.SaveCardPlans(role, next.Cards.ToByteArray(), tx);
        roles.SaveTotems(role, next.Totems.ToByteArray(), tx);
        roles.SaveEmblems(role, JsonSerializer.SerializeToUtf8Bytes(next.Echoes), tx);
        roles.SavePets(role, next.Pets.ToByteArray(), tx);
        roles.SaveSeasonTalent(role, next.Talents.ToByteArray(), tx);
        tx.WithEconomy(character.FamilyEconomy(s, "cd13b/genre-apply", [], [],
            new
            {
                Version = 1, Genre = genre.ToByteArray(), Cards = next.Cards.ToByteArray(),
                Totems = next.Totems.ToByteArray(), Emblems = next.Echoes
            }));
        await roles.CommitAsync(tx, "cd13b/genre-apply", receipts =>
        {
            ItemSnapshotWritePlan.ApplyGuids(next.Items, (long[])receipts[itemReceipt]!);
            s.Profile.Genre = genre; s.Progress = next.Progress; s.Profile.StatPlans.Info = next.Attrs; s.Combat.SkillPlans = next.Skills;
            s.Profile.EquipmentPlans = next.Equipment; s.Profile.CardPlans = next.Cards; s.Profile.Totems = next.Totems;
            s.Profile.Emblems = next.Echoes; s.Pets = next.Pets; s.Season.SeasonTalent = next.Talents; s.Items = next.Items;
            DateTime switchedAt = skills.Clock.GetUtcNow().UtcDateTime;
            if (skillChanged) s.Combat.SkillPlanSwitchUtc = switchedAt;
            if (totemChanged) s.Profile.TotemSwitchUtc = switchedAt;
            if (emblemChanged) s.Profile.EmblemPlanSwitchUtc = switchedAt;
            if (talentChanged) s.Season.TalentPlanSwitchUtc = switchedAt;
        });
    }

    private async Task PublishSyncAsync(GameSession s)
    {
        await character.SyncStatPlansAsync(s);
        await skills.SyncAsync(s);
        await character.SyncEquipmentAsync(s);
        await character.GenreAppearanceChangedAsync(s);
        await cards.SyncGenrePlanAsync(s);
        await totems.SyncAsync(s);
        await totems.RefreshEffectsAsync(s);
        await pets.SyncGenrePlanAsync(s);
        await emblems.SyncGenrePlanAsync(s, s.Profile.EmblemPower);
        await talents.SyncGenrePlanAsync(s);
        await character.ItemsChangedAsync(s, stats: true);
    }

    internal async Task RenameAsync(GameSession s, GenreChangePlanNameReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && (!s.Profile.Genre!.GenreMap.SystemMap.TryGetValue(req.TargetIndex, out _) || !ValidName(req.Name))) code = ErrorCodes.Parameter;
        if (code == 0)
        {
            var next = s.Profile.Genre!.Clone();
            next.GenreMap.SystemMap[req.TargetIndex].PlanName = req.Name;
            try { await SaveGenreAsync(s, next); await SyncAsync(s); }
            catch (Microsoft.Data.Sqlite.SqliteException e) { log.LogWarning(e, "Build rename failed"); code = ErrorCodes.Unknown; }
        }
        await s.SendGameAsync(RenamePlan, new GenreChangePlanNameResp { Result = code });
    }

    internal async Task PropertyAsync(GameSession s, GenreGetPlanPropertyReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        var next = new Stage(s, skills);
        if (code == 0) code = StageSelection(s, next, req.GenrePlans);
        var reply = new GenreGetPlanPropertyResp { Result = code };
        if (code == 0) foreach (var (id, value) in Properties(s, next)) reply.Propertys[id] = value;
        await s.SendGameAsync(Property, reply);
    }

    internal async Task PowerAsync(GameSession s, GenrePlanPowerReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        var next = new Stage(s, skills);
        if (code == 0) code = StageSelection(s, next, req.GenrePlans);
        await s.SendGameAsync(PlanPower, new GenrePlanPowerResp { Result = code, Power = code == 0 ? Power(s, next) : new() });
    }

    internal async Task SeasonAsync(GameSession s)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0)
        {
            var next = s.Profile.Genre!.Clone(); next.IsNewSeasonNtf = false;
            try { await SaveGenreAsync(s, next); await SyncAsync(s); }
            catch (Microsoft.Data.Sqlite.SqliteException e) { log.LogWarning(e, "Build season flag failed"); code = ErrorCodes.Unknown; }
        }
        await s.SendGameAsync(Season, new GenreSeasonResp { Result = code });
    }

    internal async Task SchoolAsync(GameSession s, GenreAttrSettingSchoolReq req)
    {
        using var held = await CharacterService.Lock(s);
        int code = Gate(s);
        if (code == 0 && (!s.Profile.StatPlans.Info.AllPlanBaseProperty.TryGetValue(req.PlanId, out _)
            || data.Row("JobConfig", req.ProfessionId) == null)) code = ErrorCodes.Parameter;
        if (code == 0)
        {
            var next = s.Profile.StatPlans.Info.Clone();
            next.AllPlanBaseProperty[req.PlanId].SkillGenre = req.ProfessionId;
            try
            {
                var tx = roles.NewTransaction();
                tx.RoleBlob(s.Role!.Id, "stat_plans", next.ToByteArray());
                await roles.CommitAsync(tx, "cd13b/genre-school", _ => s.Profile.StatPlans.Info = next);
                await character.SyncStatPlansAsync(s);
            }
            catch (Microsoft.Data.Sqlite.SqliteException e) { log.LogWarning(e, "Build school selection failed"); code = ErrorCodes.Unknown; }
        }
        await s.SendGameAsync(School, new GenreAttrSettingSchoolResp
        { Result = code, PlanId = req.PlanId, ProfessionId = req.ProfessionId });
    }
}
