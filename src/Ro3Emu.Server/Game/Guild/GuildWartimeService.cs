using Romsg;
using Ro3Emu.Server.Accounts;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Economy;
using Ro3Emu.Server.Game.Ownership;
using IWarMachineSite = Ro3Emu.Server.Game.GuildService.IWarMachineSite;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Guild wartime store and machines, vehicles, market, activity/dividends, item actions/store, league elites,
/// convene/rally, and broad email. Extracted from <see cref="GuildService"/>.
/// </summary>
public sealed partial class GuildWartimeService(GuildService guilds, AwardService awards, SceneService scene, TeamService teams)
{
    private const int Function = GuildService.Function;
    private const uint BaseChange = GuildService.BaseChange;
    private const uint MarketBooth = GuildService.MarketBooth, MarketItems = GuildService.MarketItems,
        MarketList = GuildService.MarketList, MarketUnlist = GuildService.MarketUnlist,
        MarketBuy = GuildService.MarketBuy, MarketSellRecords = GuildService.MarketSellRecords,
        MarketBuyRecords = GuildService.MarketBuyRecords, MarketUpdate = GuildService.MarketUpdate,
        MarketSellRecordNtf = 83907;

    private GameData data => guilds.GameData;
    private GuildStore store => guilds.Store;
    private RoleStore roles => guilds.Roles;
    private CharacterService character => guilds.Character;
    private FunctionService functions => guilds.Functions;
    private ChatService chat => guilds.Chat;
    private TimeProvider clock => guilds.Clock;
    private OwnerRegistry ownerRegistry => guilds.OwnerRegistry;
    private GameLoop loop => guilds.Loop;
    private MailService mail => guilds.Mail;
    private ILogger log => guilds.Log;

    private long NowMs() => guilds.NowMs();
    private long NowSec() => guilds.NowSec();
    private GameSession? Online(long id) => guilds.Online(id);
    private GuildStore.State? Own(GameSession session) => guilds.Own(session);
    private bool HasRight(GuildStore.State g, long actor, int right) => guilds.HasRight(g, actor, right);
    private DBRoleGuildInfo Personal(long roleId) => guilds.Personal(roleId);
    private Task SyncRole(GameSession session) => guilds.SyncRole(session);
    private static GuildResource Resource(GuildStore.State g) => GuildService.Resource(g);
    private bool Sensitive(string text) => guilds.Sensitive(text);
    private Task PushBase(GuildStore.State g, GuildBaseInfoChangeNtf change) => guilds.PushBase(g, change);
    private ReservationKey[] OwnGuildKeys(GameSession s) => guilds.OwnGuildKeys(s);
    private Task RunGuildAsync(GameSession? actor, IEnumerable<long> guildIds, IEnumerable<long> roleIds, Func<Task> command,
        bool maintenance = false, bool disband = false, bool characterGate = false) =>
        guilds.RunGuildAsync(actor, guildIds, roleIds, command, maintenance, disband, characterGate);
    private Task<bool> SaveGuildAsync(GuildStore.State guild, IEnumerable<long>? removed = null, EconomyEnvelope? economy = null) =>
        guilds.SaveGuildAsync(guild, removed, economy);
    private Task SavePersonalAsync(GameSession session, DBRoleGuildInfo view) => guilds.SavePersonalAsync(session, view);
    private void Journal(GuildStore.State g, int cfgId, long[] players, params LanguageParam[] values) =>
        guilds.Journal(g, cfgId, players, values);
    private static LanguageParam Str(string value) => GuildService.Str(value);
    private static LanguageParam Int(long value) => GuildService.Int(value);
    private const int EvPurchase = GuildService.EvPurchase;
    private const int GuildMaterialItem = GuildService.GuildMaterialItem;
    private const int ErrInvitationInvalid = GuildService.ErrInvitationInvalid;
    private long NextToken() => guilds.NextToken();
}
