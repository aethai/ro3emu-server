using Google.Protobuf;
using Microsoft.Extensions.Options;
using Phonest.Proto;
using Ro3Emu.Server.Net;

namespace Ro3Emu.Server.Micro;

/// <summary>
/// The micro-service gate ("ms gate", server list gate_address). Rpc indexes follow the client's
/// IClientGateMicroServiceStub / IGateClientMicroService (Phonest.Common.Micro.Internal).
/// </summary>
public sealed partial class MicroGateServer(IOptions<ServerOptions> options, GateCrypto crypto, MicroServiceRouter router, MicroSessions sessions, ILogger<MicroGateServer> log, Diagnostics.ServerMetrics? metrics = null, Game.GameLoop? loop = null)
    : GateListener(options.Value.ListenAddress(), options.Value.MicroGatePort, log, metrics)
{
    // client -> gate
    private const ushort RequestEncryptToken = 1;
    private const ushort ConfirmEncryptKey = 2;
    private const ushort ClientHeartbeat = 6;
    private const ushort ReplyHeartbeat = 16;
    private const ushort MicroRequest = 33;

    // gate -> client
    private const ushort ResponseEncryptToken = 10;
    private const ushort ConfirmEncryptKeyAck = 11;
    private const ushort MicroResponse = 34;

    protected override string Name => "MicroGate";

    protected override bool IsSignedIn(PhonestChannel channel) => (channel.Session as MicroSession)?.Account != null;

    private long _nextSessionId;
    private readonly LogThrottle _closedLog = new();

    protected override ValueTask OnDisconnectedAsync(PhonestChannel channel)
    {
        if (channel.Session is MicroSession session) sessions.Remove(session);
        return ValueTask.CompletedTask;
    }

    protected override async ValueTask OnFrameAsync(PhonestChannel channel, ushort index, ReadOnlyMemory<byte> body)
    {
        if (loop != null && loop.Phase != Game.GameLoopPhase.Accepting) throw new OperationCanceledException("Micro gate is quiescing.");
        var session = channel.Session as MicroSession
            ?? (MicroSession)(channel.Session = new MicroSession(channel, Interlocked.Increment(ref _nextSessionId)));
        if (index is not (ClientHeartbeat or ReplyHeartbeat) && !WithinBudget(session))
        {
            if (session.Account == null) throw Close(channel, "went over its request budget before signing in");
            if (!session.OverBudgetLogged)
            {
                session.OverBudgetLogged = true;
                LogOverBudget(Log, session.Account);
            }
            if (index == MicroRequest && Parse(body) is { Rid: not 0 } refused)
                await channel.SendAsync(MicroResponse, new MsResponse
                {
                    Rid = refused.Rid,
                    Params = ByteString.CopyFrom(MessagePack.MessagePackSerializer.Serialize(MicroServiceRouter.Fail("too many requests"))),
                });
            return;
        }
        switch (index)
        {
            case RequestEncryptToken:
                session.HandshakeToken = GateCrypto.NewHandshakeToken();
                await channel.SendAsync(ResponseEncryptToken, new EncryptToken { Token = session.HandshakeToken });
                break;

            case ConfirmEncryptKey:
                byte[] sessionKey;
                try { sessionKey = crypto.OpenSessionKey(EncryptKeyString.Parser.ParseFrom(body.Span), session.HandshakeToken); }
                catch (Exception e) when (e is InvalidProtocolBufferException or System.Security.Cryptography.CryptographicException)
                {
                    throw Close(channel, "failed the key exchange: " + MicroServiceRouter.Clip(e.Message));
                }
                channel.EnableConvertor(sessionKey);
                await channel.SendAsync(ConfirmEncryptKeyAck, new Phonest.Proto.Void());
                Log.LogInformation("MicroGate: {Remote} encrypted session up", channel.Remote);
                break;

            case ClientHeartbeat:
            case ReplyHeartbeat:
                break;

            case MicroRequest:
                // The client sends its first request after ConfirmEncryptKeyAck; a plaintext one is not the client.
                if (!channel.Encrypted) throw Close(channel, "sent a request before the encrypted session was up");
                var request = Parse(body) ?? throw Close(channel, "sent a request that does not parse");
                var result = await router.InvokeAsync(session, request.Service, request.Method, request.Params.Memory);
                if (request.Rid != 0)
                {
                    await channel.SendAsync(MicroResponse, new MsResponse { Rid = request.Rid, Params = ByteString.CopyFrom(result) });
                }
                break;

            default:
                if (!session.UnknownIndexLogged)
                {
                    session.UnknownIndexLogged = true;
                    LogUnknownIndex(Log, channel.Remote, index, body.Length);
                }
                break;
        }
    }

    /// <summary>Takes one frame from the connection's budget (AntiCheatOptions.MicroRequests, MicroRequestsBeforeSignIn).</summary>
    private bool WithinBudget(MicroSession session)
    {
        var settings = options.Value.AntiCheat;
        var bucket = session.Account == null
            ? session.BudgetBeforeSignIn ??= Game.RateBucket.Parse(settings.MicroRequestsBeforeSignIn)
            : session.Budget ??= Game.RateBucket.Parse(settings.MicroRequests);
        // An unset or unreadable budget means none, like AntiCheatOptions.MessagesPerSecond 0.
        return bucket == null || bucket.TryTake(Environment.TickCount64);
    }

    private static MsRequest? Parse(ReadOnlyMemory<byte> body)
    {
        try { return MsRequest.Parser.ParseFrom(body.Span); }
        catch (InvalidProtocolBufferException) { return null; }
    }

    /// <summary>Ends the connection with one Warning line (no stack, at most one line per 10 s for the whole gate).</summary>
    private OperationCanceledException Close(PhonestChannel channel, string why)
    {
        if (_closedLog.Allow(out long suppressed))
            LogClosed(Log, channel.Remote, why, suppressed);
        return new OperationCanceledException("Micro gate closed the connection.");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MicroGate: {Account} went over its request budget; refused calls are not logged")]
    private static partial void LogOverBudget(ILogger logger, string? account);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MicroGate: {Remote} sent unknown rpc index {Index} ({Length} bytes); later ones not logged")]
    private static partial void LogUnknownIndex(ILogger logger, string remote, ushort index, int length);

    [LoggerMessage(Level = LogLevel.Warning, Message = "MicroGate: {Remote} {Why}; closing ({Suppressed} more since the last such line)")]
    private static partial void LogClosed(ILogger logger, string remote, string why, long suppressed);
}

/// <summary>Per-connection state on the micro gate.</summary>
public sealed class MicroSession(PhonestChannel channel, long id)
{
    public PhonestChannel Channel { get; } = channel;

    /// <summary>Unique on its gate: an unverified session's own reservation key.</summary>
    public long Id { get; } = id;

    public long HandshakeToken { get; set; }

    /// <summary>The verified account; set only by <see cref="MicroSessions.SignIn"/> (tests may set it).</summary>
    public string? Account { get; internal set; }

    /// <summary>A call was refused before sign-in (MicroServiceRouter); only the first refusal is logged.</summary>
    internal bool RefusalLogged { get; set; }

    /// <summary>The frame budgets (MicroGateServer.WithinBudget), used only by this connection's frame reader.</summary>
    internal Game.RateBucket? BudgetBeforeSignIn { get; set; }
    internal Game.RateBucket? Budget { get; set; }

    /// <summary>Only the first over-budget refusal and the first unknown rpc index of a connection are logged.</summary>
    internal bool OverBudgetLogged { get; set; }
    internal bool UnknownIndexLogged { get; set; }
}
