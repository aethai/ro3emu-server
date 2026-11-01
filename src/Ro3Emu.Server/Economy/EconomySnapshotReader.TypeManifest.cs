using System.Reflection;
using System.Text.Json.Serialization;
using Google.Protobuf.Reflection;

namespace Ro3Emu.Server.Economy;

public static partial class EconomySnapshotReader
{
    /// <summary>
    /// Frozen classification surface, independent of SQL schema. Adding a field to an existing persisted DTO or
    /// protobuf cannot silently classify a new balance as progression. Review the decoder and bump this pin when
    /// the persisted format changes; a matching runtime type alone is not a reviewed economic classification.
    /// </summary>
    // C/D 13e: Dungeon.State.PendingJourney names a captured act/cycle, not a second holding of
    // its dungeon loot. Authored Journey boss acts have no direct goods; later node claims stay separate.
    // Emotes: role.emoji_play is DBEmojiPlayInfo (emote ids: favourites, unlocks, idle emote); no balance, no asset.
    // Achievements: role.achievements is DBRoleAchievementInfo (statuses, score counters, points, claimed reward and
    // hint levels) and role.achievement_runtime is AchievementService.RuntimeState (the last counted login day). Neither
    // holds a balance or an asset: a claim's goods are granted by its own cd13e/achievement-award receipt, and the
    // claimed status / reward level there is only the once-only source marker of that receipt.
    // Escort: RoleState.Cycle and the ledger's Cycle name the Territory War repeat the escort season belongs to
    // (TerritoryWarsClock.EpochOf); a key, not a balance. The decoder reads it into the escort epoch.
    // Special Class Items: role.career_special is DBCareerSpecial (two CartRemouldTable ids: pendant and mount slot); a
    // free lease, no balance, no asset.
    // Adventure Group: role.adventure_group is DBAdventureGroupRelationData (claimed group level rewards, the leave
    // cooldown, the weekly social-money counter, group fields mirrored for 5068). No balance and no asset: a level reward's
    // goods are granted by its own cd13e/advguild-award receipt, and takenGroupRewardLevel is only that receipt's
    // once-only marker. The adventure_group / adventure_group_member tables hold names, rosters, the tree and group EXP
    // (a progression counter, not a currency); they are classified as social tables.
    // Pass play: role.activity_runtime PassPlay (NewActivityService.PassPlay) holds the day's and week's challenge
    // progress, daily EXP caps, paid chapters and EXP counters. No balance and no asset: pass EXP is currency 132, granted
    // by the pass/play receipt that also saves these markers.
    // Titles: role.title is DBTitleInfo (the title rank). Not a balance: a rank's Crests are paid by its own
    // cd13a/character-title receipt that also saves it.
    // Astral Level: role.paragon is DBParagonLevel (points per star and the installed Star Cores as ItemBaseInfo); the
    // level and its EXP are role.paragon_level / paragon_exp, a progression counter. Not a balance: an installed core
    // left the bag through its cd13b/paragon-* receipt (a ParagonCost sink) and comes back through one (ParagonGoods).
    // Valhalla Trials: role.heroic_spirit is DBOdinStatue (Blessing statue EXP and level). Not a balance: its EXP is
    // bought with Einherjar Coins by the cd13b/valhalla-pray receipt that also saves it. Cleared layers are
    // dungeons Records "valhalla:<layer>", written with the layer's reward by the dungeon obligation.
    // Monster Hunt: role.area_concur is DBAreaConcurInfo (kill counters per monster and map, claimed stage markers,
    // the area's claimed-stage count). No balance: kill counters ride the monster-kill receipt, and a claim's goods are
    // granted by its own cd13e/monsterhunt-award receipt whose claimed marker is only that receipt's once-only source.
    // Growth Classroom: role.growth_class is DBGrowthClassInfo (only rankReward, the claimed rank markers; the rest of
    // 5045 is derived from the quests). No balance: a rank's goods are granted by its own cd13e/growthclass-award receipt.
    // Codex monsters: role.library monsters (kill count, unlock flag) ride the monster-kill receipt; the unlock's
    // Collection Points are granted by its own cd13e/library-award receipt whose unlock flag is that receipt's marker.
    // A card collection unlock (82404) with points is a cd13e/library-award receipt the same way.
    // Seven-Day Goal: role.first_heart is DBFirstHeartTaskActivityData (task progress and status, score bar, claimed score
    // rewards). No balance: a task or score claim's goods come with its own cd13e/firstheart-award receipt, whose claimed
    // marker is only that receipt's once-only source.
    // Profile: role.personal is DBPersonalInfo (card presets, current preset, upvote times). No balance and no asset:
    // stickers placed on a card are counted against the owned currency, never spent. role.personal_upvote is
    // PersonalUpvoteList (who liked the card, when) and role.popularity is DBPopularityStar (popularity and affection
    // credited by friend gifts): counters, no balance and no asset; the gift item is spent by the friend-gift receipt.
    // Rift Crisis: role.timed_battle (TimedBattleService.State) OnePieceExtraDay/OnePieceExtraUsed count the day's extra
    // awards (RestrictConfig 1020) and OnePieceWeek/OnePieceWeekItems the week's capped extra items. No balance: the goods
    // come with the timed_battle_obligation the same settlement writes; these are only its limits. The legacy per-day
    // OnePieceGains/OnePieceItemGains are read and dropped on the next Rift payment.
    // Item actions: role.social_outfit is DBSocialOutfit (chat cosmetics unlocked by 349 items, with unlock and expiry
    // times, and the worn choices) and role.emoji_info is DBEmojiInfo (chat emoticon ids unlocked by 212 items). Unlocks,
    // no balance and no asset: the item is spent by the cd13a/character-item-action receipt that writes them.
    // GM panel: role.gm_state is GmCommands.RoleGmState (AllFunctionsOpen, openallfunc kept across logins): a flag that
    // opens client functions, no balance and no asset.
    // Job EXP catch-up: role.job_exp_catchup is CharacterService.JobExpCatchUpState: the Job EXP paid to the role, what
    // later job advances still owe it and the rank last paid, no balance and no asset; the EXP comes with its cd13e/grant
    // receipts.
    // Tavern: role.barroom is TavernService.RoleState (Star Power received from Tavern gifts, its week, active days and
    // today's time in a room): counters, no balance and no asset; the gift drink is spent by the event/tavern-gift receipt.
    // Shop gifts: role.gifts is ShopService.GiftState (sent and received gift orders): records, no balance and no asset;
    // the currency is spent by the sender's exchange receipt and the appearance granted by the pick's own receipt.
    // Power: role.emblems EmblemState.Power (PowerService.Record) holds the role's last published power total and its
    // best total (maxHistoryPower), as normal/season parts. Rating counters, no balance and no asset: they only gate
    // Become Stronger stage claims (whose goods come with their own daily-stage receipt) and rank the Hero Registry.
    // Realm of the Gods: dungeons Honors (Final Trial goal states) and BlessingsSeen (acknowledged blessing levels) are
    // markers; a Received honor is only the once-only source of its event/dungeon/honor-award receipt. server_state
    // multi_dungeon_server (DBMultiDungeonServerInfo) holds the realm's first clears: the hall of fame keeps the clearing
    // roles' equipment as a display snapshot, never a holding.
    // Mock Battle: role.timed_battle CasualMmr/CasualRecords/CasualBest/CasualTotal are a rating and statistics, and
    // CasualSettled the once-only markers of each match's event/casual-battle receipt; no balance, no asset.
    // casual_battle_obligation.payout is CasualBattleService.Payout: a frozen match result (rating delta, record,
    // numbers) and the DropPool ids its payment rolls. No goods: they are granted by the role's event/casual-battle
    // receipt that also writes the timed_battle state and marks the row delivered.
    // Battle Merit: role.territory_wars Glory (TerritoryWarsService.GloryState; past seasons in GlorySeasons) holds a season's earned points, the
    // reached rank, claimed rank markers and today's counters. No balance and no asset: the points themselves are
    // currency 15 in the purse, granted by the earner's event/battle-glory receipt with this state; a rank reward is
    // its event/battle-glory-rank receipt, whose claimed marker is that receipt's once-only source. The territory
    // WorldState.GlorySettle lists past season epochs whose standings are still to be mailed: keys, no balance; the
    // season mail's goods are the 1090 letter's attachments, delivered once by its battle-merit-season delivery key.
    // War warehouse: guild WartimeItems/WartimeStocked (GuildService.Wartime) hold the guild's war machines, which only
    // leave the warehouse by being built on a war map; WartimeSlots are operator permissions and WartimePrepay the
    // machines a member reserved for the blueprint they place. No balance and no asset: machines never reach a bag, a
    // purse or a trade.
    // Daily compensation: role.daily DailyState.Compensation lists, per ended day, the goods of the To-Do boxes that day
    // reached and left unclaimed. An obligation, not a holding: the goods are sourced only by the 1058 letter's
    // mail/deliver receipt (ActivityReward), delivered once by its daily-compensation delivery key, which also removes the day.
    // Faction Quest: role.task_runtime FactionDay/FactionPoints (TaskRuntimeState) are the game day and the Faction Quest
    // points counted that day against TerritoryWarzoneTaskPointsLimit. A counter, no balance and no asset.
    // Free cash shop: role.activity_runtime Purchases (NewActivityService.Purchases) counts each top-up or product a role
    // took, in total and on its game day. Counters, no balance and no asset: the goods come with the order's own
    // newactivity receipt that also saves them.
    // Leaderboard rewards: server_state rank_rewards (RankRewardService) names each rewarded board's last settlement
    // time. A key, no balance: the goods are the rank letters' attachments, each delivered once by its delivery key.
    public const string ExpectedFormatManifestHash = "77E95F108A58DBEB382686AF5C92D2F471FDFA040CD939D96667B0E8F626026D";
    public static string FormatManifestHash => State.FormatManifestHash;

    private sealed partial class State
    {
        private static readonly Lazy<string> FormatManifest = new(ComputeFormatManifest);
        internal static string FormatManifestHash => FormatManifest.Value;

        private static string ComputeFormatManifest()
        {
            var entries = new SortedSet<string>(StringComparer.Ordinal);
            var seenTypes = new HashSet<Type>();
            var seenMessages = new HashSet<string>(StringComparer.Ordinal);
            void Message(MessageDescriptor descriptor)
            {
                if (!seenMessages.Add(descriptor.FullName)) return;
                foreach (var field in descriptor.Fields.InFieldNumberOrder())
                {
                    entries.Add($"proto:{descriptor.FullName}:{field.FieldNumber}:{field.Name}:{field.FieldType}:{field.IsRepeated}:{field.IsMap}:{field.IsRequired}");
                    if (field.FieldType == FieldType.Message) Message(field.MessageType);
                    if (field.FieldType == FieldType.Enum)
                        foreach (var value in field.EnumType.Values) entries.Add($"enum:{field.EnumType.FullName}:{value.Number}:{value.Name}");
                }
            }
            void TypeShape(Type type)
            {
                if (!seenTypes.Add(type)) return;
                if (type.IsArray) { TypeShape(type.GetElementType()!); return; }
                if (type.IsGenericType)
                    foreach (var argument in type.GetGenericArguments()) TypeShape(argument);
                if (type.IsEnum)
                {
                    foreach (string name in Enum.GetNames(type)) entries.Add($"enum:{type.FullName}:{name}:{Enum.Format(type, Enum.Parse(type, name), "D")}");
                    return;
                }
                if (type.Assembly != typeof(EconomySnapshotReader).Assembly) return;
                foreach (var p in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    if (p.GetIndexParameters().Length != 0 || p.GetCustomAttribute<JsonIgnoreAttribute>()?.Condition == JsonIgnoreCondition.Always) continue;
                    entries.Add($"json:{type.FullName}:{p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name}:{p.PropertyType.FullName}:{p.GetCustomAttribute<JsonConverterAttribute>()?.ConverterType?.FullName}");
                    TypeShape(p.PropertyType);
                }
                foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public).Where(x => x.IsDefined(typeof(JsonIncludeAttribute))))
                {
                    entries.Add($"field:{type.FullName}:{f.Name}:{f.FieldType.FullName}");
                    TypeShape(f.FieldType);
                }
            }
            foreach (var pair in ProtoDescriptors.OrderBy(x => x.Key, StringComparer.Ordinal))
            { entries.Add($"role-proto:{pair.Key}:{pair.Value.FullName}"); Message(pair.Value); }
            foreach (var pair in JsonRoleTypes.OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                entries.Add($"role-json:{pair.Key}:{pair.Value}");
                var type = typeof(EconomySnapshotReader).Assembly.GetType("Ro3Emu.Server.Game." + pair.Value);
                if (type == null) entries.Add("missing:" + pair.Value); else TypeShape(type);
            }
            entries.Add("role-string-set:task_followup:System.String");
            entries.Add("dungeon_reward_obligation:Ro3Emu.Server.Accounts.DungeonRewardObligation");
            TypeShape(typeof(Ro3Emu.Server.Accounts.DungeonRewardObligation));
            entries.Add("casual_battle_obligation:Ro3Emu.Server.Game.CasualBattleService+Payout");
            TypeShape(typeof(Ro3Emu.Server.Game.CasualBattleService.Payout));
            // These JSON wrappers carry embedded protobuf bytes in addition to their explicit DTO graph.
            Message(Romsg.DBMountsInfo.Descriptor);
            Message(Romsg.DBLotteryData.Descriptor);
            Message(Romsg.DBDailyActivityInfo.Descriptor);
            entries.Add("activity_eve_global:romsg.DBSeasonEveInfo");
            Message(Romsg.DBSeasonEveInfo.Descriptor);
            entries.Add("festival_round:Ro3Emu.Server.Game.FestivalService.Round");
            TypeShape(typeof(Ro3Emu.Server.Game.FestivalService.Round));
            entries.Add("server_state/season_history:Ro3Emu.Server.Game.SeasonService.Season");
            TypeShape(typeof(Ro3Emu.Server.Game.SeasonService.Season));
            entries.Add("server_state/rank_rewards:board id -> yyyyMMddHH settlement");
            entries.Add("server_state/multi_dungeon_server:romsg.DBMultiDungeonServerInfo");
            Message(Romsg.DBMultiDungeonServerInfo.Descriptor);
            return Hash(string.Join("\n", entries));
        }
    }
}
