using Google.Protobuf;
using Ro3Emu.Bot;
using Ro3Emu.ClientHooks;
using Ro3Emu.Server.Game;
using Tencent.IM;

namespace Ro3Emu.Tests;

/// <summary>Chat voice messages (issue #607): the clip format, the upload's routing and the fetch's access.</summary>
public sealed class ChatVoiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string World = "@TGS#_1_1";

    /// <summary>A tone of this many seconds at 16 kHz, as the hooks capture it.</summary>
    private static short[] Tone(double seconds)
    {
        var pcm = new short[(int)(seconds * VoiceAudioRate)];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)(8000 * Math.Sin(2 * Math.PI * 440 * i / VoiceAudioRate));
        return pcm;
    }

    private const int VoiceAudioRate = 16000;

    [Fact]
    public void AClipKeepsItsSoundAndItsLength()
    {
        var tone = Tone(2);
        byte[] clip = VoiceClip.EncodeFrom16k(tone);
        Assert.True(VoiceClip.TryReadHeader(clip, out int rate, out int samples));
        Assert.Equal(VoiceClip.SampleRate, rate);
        Assert.Equal(tone.Length / 2, samples);
        Assert.Equal(2, VoiceClip.Seconds(samples, rate));
        Assert.True(clip.Length < 2 * 4000 + 100); // 4 bits a sample at 8 kHz
        var decoded = VoiceClip.Decode(clip)!;
        // IMA ADPCM follows the tone closely once its step has adapted.
        double error = 0;
        for (int i = 200; i < decoded.Length; i++) error += Math.Abs(decoded[i] - (tone[2 * i] + tone[2 * i + 1]) / 2);
        Assert.True(error / (decoded.Length - 200) < 600, $"mean error {error / (decoded.Length - 200)}");
        Assert.Equal(decoded.Length * 2, VoiceClip.To16k(decoded).Length);
    }

    [Fact]
    public void AClipWhoseLengthDisagreesWithItsHeaderIsRefused()
    {
        byte[] clip = VoiceClip.EncodeFrom16k(Tone(1));
        Assert.False(VoiceClip.TryReadHeader([.. clip, 0], out _, out _));
        Assert.False(VoiceClip.TryReadHeader(clip[..^1], out _, out _));
        clip[0] = (byte)'X';
        Assert.False(VoiceClip.TryReadHeader(clip, out _, out _));
    }

    [Fact]
    public void TheFramesReadBackWhatWasWritten()
    {
        byte[] clip = VoiceClip.EncodeFrom16k(Tone(1));
        Assert.True(VoiceClip.TryReadUpload(VoiceClip.Upload("2_7", 0, "{\"x\":1}", clip), out string receive, out uint type, out string extra, out byte[] back));
        Assert.Equal(("2_7", 0u, "{\"x\":1}"), (receive, type, extra));
        Assert.Equal(clip, back);
        Assert.True(VoiceClip.TryReadFetchAnswer(VoiceClip.FetchAnswer(VoiceClip.FetchOk, "v1_ab", clip), out byte status, out string id, out back));
        Assert.Equal((VoiceClip.FetchOk, "v1_ab"), (status, id));
        Assert.Equal(clip, back);
        Assert.True(VoiceClip.TryReadCapabilityAnswer(VoiceClip.CapabilityAnswer(true, 30, 125000, 1234567890123), out bool on, out int seconds,
            out int bytes, out long role));
        Assert.Equal((true, 30, 125000, 1234567890123L), (on, seconds, bytes, role));
        var notify = NotifyNewAudioMessage.Parser.ParseFrom(VoiceClip.NotifyNewAudio("7", "11", "11", 99, "C:\\a.ro3a", "", 3));
        Assert.Equal(("7", "11", "11", 99ul, "C:\\a.ro3a", 3u), (notify.Msg.MessageId, notify.Msg.SenderId, notify.Msg.ReceiveId, notify.Msg.SendTime,
            notify.Msg.AudioFile, notify.Msg.AudioDuration));
        var fields = VoiceClip.PbFields(new StartRecordAudioRequest { ReceiveId = "2_7", ChatType = 1, ExtraParam = "e" }.ToByteArray())!;
        Assert.Equal(("2_7", 1ul, "e"), ((string)fields[1], (ulong)fields[2], (string)fields[3]));
    }

    private static async Task<long> CapableAsync(BotClient bot)
    {
        using var answer = bot.Inbox.Expect(e => e.IsGame(ChatService.VoiceCapability));
        await bot.SendGameBytesAsync(ChatService.VoiceCapability, VoiceClip.CapabilityRequest());
        Assert.True(VoiceClip.TryReadCapabilityAnswer((await answer.WaitAsync(Wait, CancellationToken.None)).Body, out bool on, out int seconds, out _, out long role));
        Assert.True(on);
        Assert.Equal(30, seconds); // PublicParamConfig VoiceDurationLimit
        return role;
    }

    private static AudioChatMessage? Pushed(BotEvent e) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8 && BitConverter.ToUInt32(e.Body, 0) == 10
        ? NotifyNewAudioMessage.Parser.ParseFrom(e.Body.AsSpan(8)).Msg : null;

    private static int? SendError(BotEvent e) => e.IsGame(ChatService.TunnelDown) && e.Body.Length >= 8 && BitConverter.ToUInt32(e.Body, 0) == 6
        ? BitConverter.ToInt32(e.Body, 4) : null;

    private static async Task<(byte Status, byte[] Clip)> FetchAsync(BotClient bot, string id)
    {
        using var answer = bot.Inbox.Expect(e => e.IsGame(ChatService.VoiceFetch));
        await bot.SendGameBytesAsync(ChatService.VoiceFetch, VoiceClip.FetchRequest(id)!);
        VoiceClip.TryReadFetchAnswer((await answer.WaitAsync(Wait, CancellationToken.None)).Body, out byte status, out _, out byte[] clip);
        return (status, clip);
    }

    [Fact]
    public async Task AClipReachesTheChannelAndOnlyItsListenersMayFetchIt()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var carol = await host.InWorldAsync("Carol");
        await alice.JoinChatAsync(World);
        await bob.JoinChatAsync(World);
        await CapableAsync(alice);
        await CapableAsync(bob);
        await CapableAsync(carol);

        byte[] clip = VoiceClip.EncodeFrom16k(Tone(3));
        await alice.SendGameBytesAsync(ChatService.VoiceUpload, VoiceClip.Upload(World, 2, "{\"templateId\":24}", clip));
        var heard = Pushed(await bob.Inbox.WaitAsync(e => Pushed(e) != null, Wait))!;
        var own = Pushed(await alice.Inbox.WaitAsync(e => Pushed(e) != null, Wait))!; // the sender's own bubble comes as a push too
        Assert.Equal(heard.AudioFile, own.AudioFile);
        Assert.Equal(3u, heard.AudioDuration);
        Assert.Equal("{\"templateId\":24}", heard.ExtraParam);
        Assert.False(heard.IsFromServer); // it would bypass the channel's auto-play setting
        Assert.StartsWith("v1_", heard.AudioFile, StringComparison.Ordinal);

        var fetched = await FetchAsync(bob, heard.AudioFile);
        Assert.Equal(VoiceClip.FetchOk, fetched.Status);
        Assert.Equal(clip, fetched.Clip);
        Assert.Equal(VoiceClip.FetchForbidden, (await FetchAsync(carol, heard.AudioFile)).Status); // not in the channel when it was sent
        Assert.Equal(VoiceClip.FetchMissing, (await FetchAsync(bob, "v1_00000000000000000000000000000000")).Status);
    }

    [Fact]
    public async Task AWhisperClipReachesItsTargetOnly()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await using var bob = await host.InWorldAsync("Bob");
        await using var carol = await host.InWorldAsync("Carol");
        await carol.JoinChatAsync(World);
        long bobId = await CapableAsync(bob);
        await CapableAsync(alice);

        await alice.SendGameBytesAsync(ChatService.VoiceUpload, VoiceClip.Upload(bobId.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, "",
            VoiceClip.EncodeFrom16k(Tone(1.5))));
        var whisper = Pushed(await bob.Inbox.WaitAsync(e => Pushed(e) != null, Wait))!;
        Assert.Equal(bobId.ToString(System.Globalization.CultureInfo.InvariantCulture), whisper.ReceiveId);
        Assert.Equal(VoiceClip.FetchForbidden, (await FetchAsync(carol, whisper.AudioFile)).Status);
        Assert.DoesNotContain(carol.Inbox.Snapshot(), e => Pushed(e) != null);
    }

    [Fact]
    public async Task AClipTooShortOrMalformedIsRefusedWithTheSendFailure()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        await alice.JoinChatAsync(World);
        await CapableAsync(alice);
        await alice.SendGameBytesAsync(ChatService.VoiceUpload, VoiceClip.Upload(World, 2, "", VoiceClip.EncodeFrom16k(Tone(0.5))));
        Assert.Equal(1, SendError(await alice.Inbox.WaitAsync(e => SendError(e) != null, Wait)));
        await alice.SendGameBytesAsync(ChatService.VoiceUpload, VoiceClip.Upload(World, 2, "", [1, 2, 3]));
        Assert.True(SpinWait.SpinUntil(() => alice.Inbox.Snapshot().Count(e => SendError(e) == 1) == 2, Wait));
        Assert.DoesNotContain(alice.Inbox.Snapshot(), e => Pushed(e) != null);
    }

    [Fact]
    public async Task AClientWithoutTheHooksIsToldItsSendFailed()
    {
        await using var host = await SyntheticHost.StartAsync();
        await using var alice = await host.InWorldAsync("Alice");
        var frame = new byte[8];
        BitConverter.TryWriteBytes(frame, 6u); // STOP_RECORD_AND_SEND_AUDIO, the SDK's job in the original client
        await alice.SendGameBytesAsync(ChatService.TunnelUp, frame);
        Assert.Equal(1, SendError(await alice.Inbox.WaitAsync(e => SendError(e) != null, Wait)));
    }
}
