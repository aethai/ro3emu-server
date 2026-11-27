using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using HappyEngine.Base;
using NetworkLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Chat voice messages (issue #607; formats in VoiceClip). The chat Lua sends the IM commands the SDK used to carry
    /// out: 5 start recording, 6 stop and send, 7 cancel, 8 play, 9 stop playing (LC_ChatServer). ServerImAdapter hands
    /// them here once the server has answered 77017, and they are answered the way LC_DAL_Chat reads them:
    /// <list type="bullet">
    /// <item>5 records the microphone (VoiceAudio, the team mic paused meanwhile) and answers at once (its handler does
    /// nothing).</item>
    /// <item>6 encodes the clip and uploads it (77018); the server answers 6 and pushes 10, the sender included. A failure
    /// answers 6 with an error code, which shows tip 34048. A clip to oneself is an Adventure Group oath: the Lua reads the
    /// file audio_file names (Assemble_ProcessListWidgetCtrl OnVoiceReceived), so it is written to disk and its 10 comes
    /// from here, with that path.</item>
    /// <item>7 drops the recording.</item>
    /// <item>8 plays a file path (an oath) or a clip id (fetched on 77019 once, then kept); its answer is the Lua's
    /// "playing ended" (OnPlayAudioResponse stops the bubble's animation and starts the next auto-play), so it goes out
    /// when the clip has played out. A newer 8 or a 9 ends the old one without an answer: the Lua has already moved
    /// on.</item>
    /// </list>
    /// Main thread throughout, except the capture callback and the network receive, which only queue.
    /// </summary>
    public static class VoicePtt
    {
        private const uint StartRecord = 5, StopAndSend = 6, Cancel = 7, Play = 8, StopPlaying = 9, NotifyNewAudio = 10;
        private const uint PrivateChat = 1;
        private const int SendFailed = 1;
        private const int CacheClips = 32;
        /// <summary>A fetch or a playback that has not ended by then ends anyway, so a bubble never stays "playing".</summary>
        private const long SlackMs = 3000;
        /// <summary>What the output device still holds when the mixer has handed out the last sample (4 buffers of 20 ms).</summary>
        private const long DeviceTailMs = 100;
        /// <summary>The playback device closes once no clip has played this long (auto-play asks for the next at once).</summary>
        private const long IdleCloseMs = 1500;
        /// <summary>The shortest clip: the chat's timer counts a second from the press, the microphone delivers a little later.</summary>
        private const int MinimumSamples16k = VoiceAudio.SampleRate * 4 / 5;

        private static bool _asked, _ready, _enabled;
        private static int _maxSeconds;
        private static long _selfId;

        private static readonly ConcurrentQueue<Action> Posted = new ConcurrentQueue<Action>();
        private static readonly List<(uint Cmd, int Error, byte[] Body)> Answers = new List<(uint, int, byte[])>();

        // Recording.
        private static readonly object CaptureLock = new object();
        private static List<short> _recorded;
        private static string _receiveId, _extra;
        private static uint _chatType;

        // Playback.
        private static string _playing;
        private static bool _answerPending, _started, _deviceHeld;
        private static long _endsAtMs, _playedOutMs, _idleSinceMs;
        private static readonly Dictionary<string, byte[]> Cache = new Dictionary<string, byte[]>();
        private static readonly LinkedList<string> CacheOrder = new LinkedList<string>();
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        /// <summary>After the IM login, once the game connection's receive hook is in place: ask the server whether it
        /// takes voice messages (once per connection; ServerImAdapter resets on a new connection).</summary>
        public static void AskServer()
        {
            if (_asked) return;
            _asked = Send(VoiceClip.CapabilityMsgId, VoiceClip.CapabilityRequest());
        }

        /// <summary>Network thread.</summary>
        public static void OnCapability(byte[] body) => Posted.Enqueue(() =>
        {
            if (!VoiceClip.TryReadCapabilityAnswer(body, out bool enabled, out int seconds, out _, out long roleId)) return;
            _ready = true;
            _enabled = enabled;
            _maxSeconds = seconds;
            _selfId = roleId;
            Debug.Log("[Ro3Emu.Chat] voice messages " + (enabled ? "on, up to " + seconds + " s" : "off on this server"));
        });

        /// <summary>Network thread.</summary>
        public static void OnFetched(byte[] body) => Posted.Enqueue(() =>
        {
            if (!VoiceClip.TryReadFetchAnswer(body, out byte status, out string id, out byte[] clip)) return;
            if (status == VoiceClip.FetchOk && VoiceClip.TryReadHeader(clip, out _, out _)) Remember(id, clip);
            if (id != _playing || !_answerPending || _started) return;
            if (status != VoiceClip.FetchOk || !StartPlaying(clip)) EndPlaying(false);
        });

        /// <summary>Whether this command is handled here; then its answers come through <see cref="Pump"/>.</summary>
        public static bool Handle(uint cmd, byte[] body)
        {
            if (!_ready || cmd < StartRecord || cmd > StopPlaying) return false;
            try
            {
                switch (cmd)
                {
                    case StartRecord: StartRecording(body); break;
                    case StopAndSend: StopAndUpload(); break;
                    case Cancel: StopRecording(); Answer(Cancel, 0, VoiceClip.ChatResult(true)); break;
                    case Play: PlayRequested(body); break;
                    case StopPlaying: Stop(); Answer(StopPlaying, 0, VoiceClip.ChatResult(true)); break;
                }
            }
            catch (Exception e)
            {
                Debug.LogError("[Ro3Emu.Chat] voice cmd " + cmd + " failed: " + e);
                if (cmd == StopAndSend) Answer(StopAndSend, SendFailed, new byte[0]);
                if (cmd == Play) EndPlaying(false);
            }
            return true;
        }

        /// <summary>Main thread, every frame: queued work, a clip that played out, and the answers for the Lua.</summary>
        public static void Pump(Action<uint, int, byte[]> deliver)
        {
            while (Posted.TryDequeue(out var work)) work();
            long now = Clock.ElapsedMilliseconds;
            if (_answerPending && _started && !VoiceAudio.ClipPlaying && _playedOutMs == 0) _playedOutMs = now + DeviceTailMs;
            if (_answerPending && (_playedOutMs > 0 && now >= _playedOutMs || _endsAtMs > 0 && now > _endsAtMs)) EndPlaying(_started);
            if (_started || _answerPending) VoiceSettings.Poll(now); // the game's volume, outside team voice too
            if (_deviceHeld && !_answerPending && now - _idleSinceMs > IdleCloseMs)
            {
                _deviceHeld = false;
                VoiceAudio.StopClip();
            }
            foreach (var (cmd, error, body) in Answers) deliver(cmd, error, body);
            Answers.Clear();
        }

        /// <summary>The game connection closed: nothing recorded or played survives it, and the next one asks again.</summary>
        public static void Reset()
        {
            StopRecording();
            Stop();
            _asked = _ready = _enabled = false;
            _deviceHeld = false;
            VoiceAudio.StopClip();
            Cache.Clear();
            CacheOrder.Clear();
            while (Posted.TryDequeue(out _)) { }
            Answers.Clear();
        }

        // ---- recording ----

        private static void StartRecording(byte[] body)
        {
            var fields = VoiceClip.PbFields(body) ?? new Dictionary<int, object>();
            _receiveId = fields.TryGetValue(1, out var receive) ? receive as string ?? "" : "";
            _chatType = fields.TryGetValue(2, out var type) && type is ulong t ? (uint)t : 0;
            _extra = fields.TryGetValue(3, out var extra) ? extra as string ?? "" : "";
            lock (CaptureLock) _recorded = new List<short>(VoiceAudio.SampleRate * Math.Max(1, _maxSeconds));
            // An Adventure Group oath stays on this machine (OathToSelf), so it records whether or not the server takes clips.
            bool allowed = _enabled || IsOath();
            if (!allowed || !VoiceAudio.StartClipCapture(OnFrame))
            {
                lock (CaptureLock) _recorded = null;
                Debug.LogWarning("[Ro3Emu.Chat] voice message not recorded: " + (allowed ? "no microphone" : "off on this server"));
            }
            Answer(StartRecord, 0, VoiceClip.ChatResult(true));
        }

        private static bool IsOath() => _chatType == PrivateChat && _receiveId == _selfId.ToString();

        private static int _peak;

        /// <summary>Capture thread: the frame comes with the mic slider's gain applied (VoiceAudio.Capture); the automatic
        /// gain lifts quiet mics as it does for team voice. The clip stops growing at the server's longest clip.</summary>
        private static void OnFrame(short[] pcm)
        {
            int peak = VoiceAudio.Agc.Process(pcm);
            lock (CaptureLock)
            {
                if (_recorded == null || _recorded.Count >= VoiceAudio.SampleRate * (_maxSeconds + 1)) return;
                _peak = Math.Max(_peak, peak);
                _recorded.AddRange(pcm);
            }
        }

        private static List<short> StopRecording()
        {
            VoiceAudio.StopClipCapture();
            lock (CaptureLock)
            {
                var recorded = _recorded;
                _recorded = null;
                return recorded;
            }
        }

        private static int TakePeak()
        {
            lock (CaptureLock)
            {
                int peak = _peak;
                _peak = 0;
                return peak;
            }
        }

        private static void StopAndUpload()
        {
            var recorded = StopRecording();
            int peak = TakePeak();
            if (recorded == null || recorded.Count < MinimumSamples16k)
            {
                Answer(StopAndSend, SendFailed, new byte[0]);
                return;
            }
            byte[] clip = VoiceClip.EncodeFrom16k(recorded);
            VoiceClip.TryReadHeader(clip, out int rate, out int samples);
            Debug.Log("[Ro3Emu.Chat] voice message recorded: " + (samples * 10 / rate / 10.0) + " s, loudest " + peak + ", " + clip.Length + " bytes");
            if (IsOath())
            {
                OathToSelf(clip, (uint)VoiceClip.Seconds(samples, rate));
                return;
            }
            if (!Send(VoiceClip.UploadMsgId, VoiceClip.Upload(_receiveId, _chatType, _extra, clip))) Answer(StopAndSend, SendFailed, new byte[0]);
        }

        /// <summary>An Adventure Group oath: a file the Lua can read and keep, announced to the Lua alone.</summary>
        private static void OathToSelf(byte[] clip, uint seconds)
        {
            string dir = Path.Combine(Application.temporaryCachePath, "ro3emu-voice");
            Directory.CreateDirectory(dir);
            // The Lua copies the oath it was given at once (GetVoiceDeclarationPath); older ones are no longer read.
            foreach (string old in Directory.GetFiles(dir, "oath-*.ro3a"))
                try { File.Delete(old); } catch (IOException) { }
            string path = Path.Combine(dir, "oath-" + DateTime.UtcNow.Ticks + ".ro3a");
            File.WriteAllBytes(path, clip);
            string self = _selfId.ToString();
            Answer(StopAndSend, 0, VoiceClip.ChatResult(true));
            Answer(NotifyNewAudio, 0, VoiceClip.NotifyNewAudio(DateTime.UtcNow.Ticks.ToString(), self, self,
                (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), path, "", seconds));
        }

        // ---- playback ----

        private static void PlayRequested(byte[] body)
        {
            var fields = VoiceClip.PbFields(body) ?? new Dictionary<int, object>();
            string file = fields.TryGetValue(1, out var value) ? value as string ?? "" : "";
            Stop();
            if (string.IsNullOrEmpty(file)) { Answer(Play, SendFailed, VoiceClip.ChatResult(false)); return; }
            _playing = file;
            _answerPending = true;
            _endsAtMs = _playedOutMs = 0;
            if (Cache.TryGetValue(file, out var cached)) { if (!StartPlaying(cached)) EndPlaying(false); return; }
            if (file.EndsWith(".ro3a", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                || Path.IsPathRooted(file))
            {
                byte[] local = File.Exists(file) ? File.ReadAllBytes(file) : null;
                if (local == null || !StartPlaying(local)) EndPlaying(false);
                return;
            }
            var request = VoiceClip.FetchRequest(file);
            if (request == null) { EndPlaying(false); return; }
            _endsAtMs = Clock.ElapsedMilliseconds + SlackMs * 3; // the fetch itself may take a moment
            if (!Send(VoiceClip.FetchMsgId, request)) EndPlaying(false);
        }

        private static bool StartPlaying(byte[] clip)
        {
            var pcm = VoiceClip.Decode(clip);
            if (pcm == null || !VoiceAudio.PlayClip(VoiceClip.To16k(pcm))) return false;
            _started = _deviceHeld = true;
            Debug.Log("[Ro3Emu.Chat] voice message playing: " + _playing + ", " + (pcm.Length * 10 / VoiceClip.SampleRate / 10.0) + " s");
            _endsAtMs = Clock.ElapsedMilliseconds + pcm.Length * 1000L / VoiceClip.SampleRate + SlackMs;
            return true;
        }

        /// <summary>The answer to the play request in hand (its end, or its failure).</summary>
        private static void EndPlaying(bool played)
        {
            if (!_answerPending) return;
            Debug.Log("[Ro3Emu.Chat] voice message " + (played ? "played out: " : "not played: ") + _playing);
            _answerPending = _started = false;
            _playing = null;
            _endsAtMs = _playedOutMs = 0;
            _idleSinceMs = Clock.ElapsedMilliseconds;
            VoiceAudio.ClearClip();
            Answer(Play, played ? 0 : SendFailed, VoiceClip.ChatResult(played));
        }

        /// <summary>Stops what plays without answering for it.</summary>
        private static void Stop()
        {
            _answerPending = _started = false;
            _playing = null;
            _endsAtMs = _playedOutMs = 0;
            _idleSinceMs = Clock.ElapsedMilliseconds;
            VoiceAudio.ClearClip();
        }

        private static void Remember(string id, byte[] clip)
        {
            if (Cache.ContainsKey(id)) return;
            Cache[id] = clip;
            CacheOrder.AddLast(id);
            while (CacheOrder.Count > CacheClips) { Cache.Remove(CacheOrder.First.Value); CacheOrder.RemoveFirst(); }
        }

        private static void Answer(uint cmd, int error, byte[] body) => Answers.Add((cmd, error, body));

        /// <summary>False when there is no game connection to send on.</summary>
        private static bool Send(uint msgId, byte[] body)
        {
            var network = SingletonMonoBehaviour<NW_CBBNetworkManager>.Instance;
            if (network == null || network.Obf_a == null) return false;
            network.Obf_jBd(msgId, body);
            return true;
        }
    }
}
