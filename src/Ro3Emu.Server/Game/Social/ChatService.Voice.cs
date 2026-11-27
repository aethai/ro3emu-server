using System.Globalization;
using System.Security.Cryptography;
using Google.Protobuf;
using Ro3Emu.ClientHooks;
using Tencent.IM;

namespace Ro3Emu.Server.Game;

/// <summary>
/// Chat voice messages (VoiceClip has the formats). The client's Lua asks its IM SDK to record (cmd 5), stop and send
/// (6), cancel (7), play (8) and stop playing (9), and takes new clips from push 10 (LC_ChatServer, LC_DAL_Chat). The
/// patched client's hooks do the recording and playing themselves and announce it with 77017; a clip then comes up
/// whole on 77018, reaches the players a text line to the same channel would reach (cmd 10, the sender too: the
/// client's cmd 6 handler adds no bubble), and is kept in memory for those players to fetch on 77019 when they play it.
/// A client without those hooks still sends cmd 5-9 through the tunnel: its send fails visibly (LegacyVoiceAsync).
/// </summary>
public sealed partial class ChatService
{
    public const uint VoiceCapability = VoiceClip.CapabilityMsgId;
    public const uint VoiceUpload = VoiceClip.UploadMsgId;
    public const uint VoiceFetch = VoiceClip.FetchMsgId;
    /// <summary>VoiceDurationLimit when the config lacks it (PublicParamConfig ships 30).</summary>
    private const int DefaultVoiceSeconds = 30;
    /// <summary>The send response's error code: any value above 0 makes LC_DAL_Chat show tip 34048 (the send failed).</summary>
    private const int VoiceSendFailed = 1;

    private sealed record VoiceClipEntry(string Id, string Account, long Sender, HashSet<long> Listeners, byte[] Clip, long ExpiresMs);

    private readonly object _voiceSync = new();
    private readonly LinkedList<VoiceClipEntry> _voiceOrder = new();
    private readonly Dictionary<string, LinkedListNode<VoiceClipEntry>> _voiceClips = [];
    private long _voiceBytes;

    /// <summary>The longest clip in whole seconds; a recording stops at it on the client, so one second more is accepted.</summary>
    private int VoiceSeconds => (int)(Data.Config.PublicParams.Of(_data).VoiceDurationLimit is > 0 and var limit ? limit : DefaultVoiceSeconds);

    private int VoiceMaxBytes => VoiceClip.HeaderSize + 4 + (VoiceSeconds + 1) * VoiceClip.SampleRate / 2;

    private long VoiceNowMs => _clock.GetUtcNow().ToUnixTimeMilliseconds();

    internal async Task VoiceCapabilityAsync(GameSession session, uint _, ReadOnlyMemory<byte> body)
    {
        if (session.Role is not { } role) return;
        session.Social.ChatVoice = true;
        bool enabled = _options.Value.ChatVoice.Enabled;
        await session.SendGameRawAsync(VoiceCapability, VoiceClip.CapabilityAnswer(enabled, VoiceSeconds, VoiceMaxBytes, role.Id));
    }

    private sealed record VoiceUploadRequest(string ReceiveId, uint ChatType, string Extra, byte[] Clip, int Rate, int Samples);

    /// <summary>An uploaded clip: checked like a text line to the same channel, stored, and pushed.</summary>
    internal async Task VoiceUploadAsync(GameSession sender, uint _, ReadOnlyMemory<byte> body)
    {
        if (sender.Role is not { } role || sender.Account is not { } account) return;
        Task Refuse() => DownRawAsync(sender, (uint)Cmd.StopRecordAndSendAudio, VoiceSendFailed, []);
        if (ReadVoiceUpload(sender, body, out var refusal) is not { } upload)
        {
            LogVoiceRefused(_log, role.Name, refusal);
            await Refuse();
            return;
        }
        bool local = upload.ChatType != ChatTypePrivate && MapChannel(upload.ReceiveId);
        long runtime = sender.WorldScene, generation = sender.SceneGeneration;
        if (!GuildAllowed(sender, upload.ReceiveId) || local && (sender.SceneLoading || sender.Handoff != null)
            || await VoiceRecipientsAsync(sender, role.Id, upload) is not { } recipients)
        {
            await Refuse();
            return;
        }
        if (await FloodRefusedAsync(sender, upload.ReceiveId, upload.ChatType, "", Refuse)) return;
        string id = StoreVoiceClip(account, role.Id, recipients, upload.Clip);
        var message = new AudioChatMessage
        {
            MessageId = Interlocked.Increment(ref _nextMessageId).ToString(CultureInfo.InvariantCulture),
            SenderId = role.Id.ToString(CultureInfo.InvariantCulture),
            ReceiveId = upload.ReceiveId,
            SendTime = (ulong)_clock.GetUtcNow().ToUnixTimeSeconds(),
            AudioFile = id,
            ExtraParam = upload.Extra,
            AudioDuration = (uint)VoiceClip.Seconds(upload.Samples, upload.Rate),
        };
        LogVoiceSent(_log, role.Name, upload.ReceiveId, recipients.Count, message.AudioDuration);
        await DownAsync(sender, Cmd.StopRecordAndSendAudio, new StopRecordAndSendAudioResponse { ChatResult = true, Msg = message });
        var push = new NotifyNewAudioMessage { Msg = message };
        foreach (var to in recipients)
        {
            if (local && (sender.SceneGeneration != generation || sender.Handoff != null
                || to.SceneLoading || to.Handoff != null || to.WorldScene != runtime)) continue;
            try { await DownAsync(to, Cmd.NotifyNewAudio, push); }
            catch (Exception e) when (e is IOException or System.Net.Sockets.SocketException or ObjectDisposedException) { }
        }
    }

    /// <summary>The upload, or null with why it is refused before anything is stored.</summary>
    private VoiceUploadRequest? ReadVoiceUpload(GameSession sender, ReadOnlyMemory<byte> body, out string refusal)
    {
        var options = _options.Value.ChatVoice;
        refusal = "";
        if (!options.Enabled) refusal = "voice messages are off";
        else if (body.Length > VoiceMaxBytes + 1024) refusal = $"{body.Length} bytes";
        else if (!VoiceClip.TryReadUpload(body.ToArray(), out string receiveId, out uint chatType, out string extra, out byte[] clip)) refusal = "malformed upload";
        else if (!VoiceClip.TryReadHeader(clip, out int rate, out int samples)) refusal = "malformed clip";
        // The chat counts a second from the press; the microphone starts a little after it (VoicePtt).
        else if (samples < rate * 4 / 5 || samples > (VoiceSeconds + 1) * rate) refusal = $"{samples} samples";
        else if (receiveId.Length == 0 || receiveId.Length > 128 || extra.Length > _options.Value.AntiCheat.ChatMaxLength * 8) refusal = "bad channel or extra";
        else if (!_gm.Allowed(sender) && !(sender.Social.VoiceClips ??= RateBucket.Parse(options.Clips) ?? new RateBucket(0.1, 4)).TryTake(VoiceNowMs))
            refusal = "over the clip rate";
        else return new VoiceUploadRequest(receiveId, chatType, extra, clip, rate, samples);
        return null;
    }

    /// <summary>Who hears the clip, as for a text line; a whisper to a player who is offline or blocked the sender has
    /// nobody to reach and is refused (the client drops a blocked sender's clips too, LC_DAL_Chat).</summary>
    private async Task<List<GameSession>?> VoiceRecipientsAsync(GameSession sender, long senderRole, VoiceUploadRequest upload)
    {
        if (Recipients(sender, upload.ReceiveId, upload.ChatType) is not { } recipients) return null;
        if (upload.ChatType != ChatTypePrivate) return recipients;
        var target = recipients.FirstOrDefault(p => p != sender);
        if (target?.Role is not { } to || await _friends.BlocksAsync(to.Id, senderRole)) return null;
        return recipients;
    }

    /// <summary>A clip for these listeners under a random id (not guessable, so not enumerable), oldest out past the cap.</summary>
    private string StoreVoiceClip(string account, long sender, List<GameSession> recipients, byte[] clip)
    {
        string id = "v1_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var listeners = recipients.Select(p => p.Role?.Id ?? 0).Where(r => r != 0).Append(sender).ToHashSet();
        var options = _options.Value.ChatVoice;
        long now = VoiceNowMs, cap = Math.Max(1, options.StoreMegabytes) * 1024L * 1024;
        lock (_voiceSync)
        {
            DropVoiceClips(now, cap - clip.Length);
            // One account's clips past its share go oldest first, so a spammer churns only its own.
            var own = _voiceOrder.Where(e => e.Account == account).Select(e => e.Id).ToList();
            foreach (string old in own.Take(Math.Max(0, own.Count - Math.Max(1, options.AccountClips) + 1)))
                if (_voiceClips.Remove(old, out var node0))
                {
                    _voiceOrder.Remove(node0);
                    _voiceBytes -= node0.Value.Clip.Length;
                }
            var node = _voiceOrder.AddLast(new VoiceClipEntry(id, account, sender, listeners, clip, now + options.ClipMinutes * 60_000L));
            _voiceClips[id] = node;
            _voiceBytes += clip.Length;
        }
        return id;
    }

    /// <summary>Expired clips, then the oldest until the rest fit in <paramref name="room"/> bytes (under _voiceSync).</summary>
    private void DropVoiceClips(long now, long room)
    {
        while (_voiceOrder.First is { } oldest && (oldest.Value.ExpiresMs <= now || _voiceBytes > room))
        {
            _voiceOrder.RemoveFirst();
            _voiceClips.Remove(oldest.Value.Id);
            _voiceBytes -= oldest.Value.Clip.Length;
        }
    }

    /// <summary>A clip for a player it reached; another gets Forbidden, an expired or unknown one Missing.</summary>
    internal async Task VoiceFetchAsync(GameSession session, uint _, ReadOnlyMemory<byte> body)
    {
        if (session.Role is not { } role || !VoiceClip.TryReadFetchRequest(body.ToArray(), out string id)) return;
        session.Social.VoiceFetches ??= RateBucket.Parse(_options.Value.ChatVoice.Fetches) ?? new RateBucket(1, 10);
        if (!session.Social.VoiceFetches.TryTake(VoiceNowMs))
        {
            await session.SendGameRawAsync(VoiceFetch, VoiceClip.FetchAnswer(VoiceClip.FetchBusy, id, null));
            return;
        }
        VoiceClipEntry? entry;
        lock (_voiceSync)
        {
            DropVoiceClips(VoiceNowMs, long.MaxValue);
            entry = _voiceClips.TryGetValue(id, out var node) ? node.Value : null;
        }
        byte status = entry == null ? VoiceClip.FetchMissing : entry.Listeners.Contains(role.Id) ? VoiceClip.FetchOk : VoiceClip.FetchForbidden;
        await session.SendGameRawAsync(VoiceFetch, VoiceClip.FetchAnswer(status, id, entry?.Clip));
    }

    /// <summary>
    /// Cmd 5-9 through the tunnel come from a client without the recording hooks: there is nothing to send, so stop and
    /// send fails (tip 34048) and play answers at once, which ends the bubble's play animation (OnPlayAudioResponse).
    /// </summary>
    private static async Task LegacyVoiceAsync(GameSession session, Cmd cmd)
    {
        switch (cmd)
        {
            case Cmd.StopRecordAndSendAudio:
                await DownRawAsync(session, (uint)cmd, VoiceSendFailed, []);
                break;
            case Cmd.PlayAudio:
                await DownRawAsync(session, (uint)cmd, VoiceSendFailed, new PlayAudioResponse { ChatResult = false }.ToByteArray());
                break;
            default:
                await DownRawAsync(session, (uint)cmd, 0, []);
                break;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice clip from {Name} refused: {Reason}")]
    private static partial void LogVoiceRefused(ILogger logger, string name, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Voice {Name} -> {Target} ({Count} recipient(s)): {Seconds} s")]
    private static partial void LogVoiceSent(ILogger logger, string name, string target, int count, uint seconds);
}
