using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Micro;

namespace Ro3Emu.Server;

/// <summary>Chat, friends, parties, mail, tavern, achievements, voice, ranks, and social GM commands.</summary>
internal static class SocialComposition
{
    public static void AddSocial(IServiceCollection services, ServerOptions options)
    {
        void AddLifecycle<T>() where T : class, IRoleLifecycle =>
            services.AddSingleton<IRoleLifecycle>(sp => sp.GetRequiredService<T>());

        services.AddSingleton<EmojiService>();
        services.AddSingleton<PersonalService>();
        services.AddSingleton<SocialOutfitService>();
        services.AddSingleton<AchievementService>();
        services.AddSingleton<TeamService>();
        services.AddSingleton<ICombatRule>(sp => sp.GetRequiredService<TeamService>());
        services.AddSingleton<Game.MailService>();
        services.AddSingleton<TavernService>();
        services.AddSingleton<FriendExtraService>();
        services.AddSingleton<ChatService>();
        services.AddSingleton<ChatFloodGuard>();
        services.AddSingleton<FeedbackService>();
        services.AddSingleton<Voice.VoiceTicketRegistry>();
        services.AddSingleton<Voice.VoiceRooms>();
        // The voice relay PoC only opens a second listener when a port is configured for it.
        if (options.Voice.Port > 0) services.AddHostedService<Voice.VoiceGateServer>();

        services.AddSingleton<SocialOwnerCache>();
        services.AddSingleton<IMicroService, UserDataService>();
        services.AddSingleton<FriendStore>();
        services.AddSingleton<FriendService>();
        services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<FriendService>());
        services.AddSingleton<IMicroService, PartyService>();
        services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<Game.MailService>());
        services.AddSingleton<RankService>();
        services.AddSingleton<IMicroService>(sp => sp.GetRequiredService<RankService>());

        AddLifecycle<ChatService>();
        AddLifecycle<EmojiService>();
        AddLifecycle<PersonalService>();
        AddLifecycle<SocialOutfitService>();
        AddLifecycle<AchievementService>();
        services.AddSingleton<IRoleLifecycle, FriendRoleLifecycle>();
        AddLifecycle<Game.MailService>();
        AddLifecycle<TavernService>();
    }

    public static void WireChatChannelRule(WebApplication app)
    {
        // Team chat channels (ChatService cannot take TeamService: TeamService -> ChatService).
        app.Services.GetRequiredService<ChatService>().TeamChannelAllowed = app.Services.GetRequiredService<TeamService>().ChannelAllowed;
    }

    public static void WireTeamService(WebApplication app)
    {
        app.Services.GetRequiredService<TeamService>(); // party card ticker
    }

    public static void WireSocialTaskAndAchievementHooks(WebApplication app)
    {
        var questTasks = app.Services.GetRequiredService<TaskService>();
        app.Services.GetRequiredService<PersonalService>().Upvoted.Subscribe(questTasks.OnProfileLiked); // 106902
        app.Services.GetRequiredService<ChatService>().TextSent.Subscribe(questTasks.OnChatSent); // 103020
        // The Tavern's achievements: 106007 counts the daily quest's deliveries, 106008 reads the Star Power.
        var achievements = app.Services.GetRequiredService<AchievementService>();
        var tavern = app.Services.GetRequiredService<TavernService>();
        questTasks.QuestDelivered.Subscribe(
            (s, task, _) => achievements.OnTaskCompletions(s, task, 1),
            name: "SocialComposition.AchievementTaskDelivered");
        tavern.TavernQuestsAdded.Subscribe(
            (s, count) => achievements.OnTaskCompletions(s, tavern.TaskId(), count),
            name: "SocialComposition.AchievementTavernQuests");
        tavern.PopularityChanged.Subscribe(achievements.OnStateChanged);
        var social = app.Services.GetRequiredService<SocialOwnerCache>();
        questTasks.LoadFriendCount = async s => s.Role is { } role
            ? (await social.LoadSocialAsync([role.Id])).GetValueOrDefault(role.Id)?.Friends.LongLength : null; // 103019
        app.Services.GetRequiredService<FriendService>().RelationChanged.Add(questTasks.OnRelationChanged);
        // Connect quests (TaskService.Connect.cs): party-mates, the raid boss clears and 116's intimacy.
        questTasks.PartyMembers = app.Services.GetRequiredService<TeamService>().MembersOf;
        DungeonComposition.WireDungeonConnectHook(app);
        questTasks.GainIntimacy = app.Services.GetRequiredService<FriendExtraService>().GainIntimacy;
        questTasks.QuestDelivered.Subscribe(questTasks.OnConnectDelivered);
    }

    public static void RegisterSocialGm(WebApplication app)
    {
        var gm = app.Services.GetRequiredService<GmCommands>();
        var data = app.Services.GetRequiredService<GameData>();
        var mail = app.Services.GetRequiredService<Game.MailService>();
        var rewards = app.Services.GetRequiredService<RewardService>();
        var tavern = app.Services.GetRequiredService<TavernService>();

        // Local GM system-mail smoke path; trailing numeric pair is an optional plain attachment.
        gm.Register("mail", "<title> [item count]", async (s, args) =>
        {
            if (args.Length == 0) throw new ArgumentException("mail title [item count]");
            bool attached = args.Length >= 3 && int.TryParse(args[^2], out _) && int.TryParse(args[^1], out _);
            string title = string.Join(' ', attached ? args[..^2] : args);
            var goods = attached ? new[] { new Game.MailService.Attachment(GmCommands.I(args[^2]), GmCommands.I(args[^1])) } : [];
            string id = await mail.SendRawAsync(s.Role!.Id, title, title, goods);
            await GmCommands.ReplyAsync(s, $"Mail {id} delivered.");
        });

        gm.RegisterOp(GmCommands.OpSendMailPerson, async (s, args) =>
        {
            int category = GmCommands.Arg(args, 2) is { Length: > 0 } type ? GmCommands.I(type) : 1;
            if (category is not (1 or 3)) throw new ArgumentException("mail type 1 system or 3 guild");
            var goods = MailGoods(data, rewards, s, GmCommands.Arg(args, 5), GmCommands.Arg(args, 6));
            string id = await mail.SendRawAsync(s.Role!.Id, args[0], GmCommands.Arg(args, 1), goods,
                expiresAt: gm.ParseMailExpiry(GmCommands.Arg(args, 4)), category: category);
            return $"Mail {id} delivered" + (goods.Count > 0 ? $" with {gm.FormatGoods(goods.Select(g => (g.Entry, g.Count)))}." : ".");
        });

        gm.RegisterOp(GmCommands.OpSendMailSystemByCfgId, async (_, args) =>
        {
            int template = GmCommands.I(args[0]);
            var expiresAt = gm.ParseMailExpiry(GmCommands.Arg(args, 1));
            if (data.Row("MailConfig", template) is null) return $"No MailConfig {template}.";
            var roles = gm.OnlineRoles();
            try
            {
                foreach (var role in roles) await mail.SendTemplateAsync(role.Role!.Id, template, expiresAt);
            }
            catch (AwardResolutionException e)
            {
                return $"MailConfig {template}: {e.Message}.";
            }
            return $"MailConfig {template} delivered to {roles.Count} online roles.";
        });

        gm.RegisterOp(GmCommands.OpSendMailSystemByCustom, async (s, args) =>
        {
            var expiresAt = gm.ParseMailExpiry(GmCommands.Arg(args, 4));
            MailGoods(data, rewards, s, GmCommands.Arg(args, 5), GmCommands.Arg(args, 6)); // refuse a bad reward before anyone gets mail
            var roles = gm.OnlineRoles();
            foreach (var role in roles)
                await mail.SendRawAsync(role.Role!.Id, args[0], GmCommands.Arg(args, 1),
                    MailGoods(data, rewards, role, GmCommands.Arg(args, 5), GmCommands.Arg(args, 6)), expiresAt: expiresAt);
            return $"Mail delivered to {roles.Count} online roles.";
        });

        gm.RegisterOp(GmCommands.OpPrintBarroom, (s, args) => tavern.GmOpAsync(s, GmCommands.OpPrintBarroom, args));
        var barroomOps = Enumerable.Range(GmCommands.OpSetBarroomInfo, GmCommands.OpAddBarroomToDayStayTimes - GmCommands.OpSetBarroomInfo + 1);
        gm.RegisterOps(barroomOps, tavern.GmOpAsync);
    }

    private static List<Game.MailService.Attachment> MailGoods(GameData data, RewardService rewards, GameSession recipient, string kind, string value)
    {
        if (kind.Length == 0 || value.Length == 0) return [];
        switch (GmCommands.I(kind))
        {
            case 0:
                if (data.Row("DropPoolConfig", GmCommands.L(value)) is null) throw new ArgumentException($"no drop pool {value}");
                return [.. rewards.RollPoolBound(GmCommands.L(value), recipient.Progress!).Select(g => new Game.MailService.Attachment(g.Entry, g.Count, g.Bind))];
            case 1:
                var parts = value.Split(':', ';', '|', '*');
                int entry = GmCommands.I(parts[0]), count = parts.Length > 1 ? GmCommands.I(parts[1]) : 1;
                if (data.Row("ItemConfig", entry) is null || count <= 0) throw new ArgumentException($"no item {value}");
                return [new Game.MailService.Attachment(entry, count)];
            case 2:
                if (data.Row("AwardConfig", GmCommands.L(value)) is null) throw new ArgumentException($"no award {value}");
                return [.. rewards.RollAwardBound(GmCommands.L(value), recipient).Select(g => new Game.MailService.Attachment(g.Entry, g.Count, g.Bind))];
            default:
                throw new ArgumentException("reward kind 0 drop pool, 1 item, 2 award");
        }
    }
}
