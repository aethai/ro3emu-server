#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Ro3Emu.ClientHooks
{
    /// <summary>
    /// Chat voice messages (issue #607): the clip format and the game messages that carry it. The original client left
    /// recording, upload and playback to its IM SDK; here the hooks record and play (VoicePtt) and the game server stores
    /// and routes (Ro3Emu.Server ChatService.Voice). Shared by both through a linked source file, so it has no Unity or
    /// server dependency.
    /// <list type="bullet">
    /// <item>A clip is "RO3A" | version u8 | codec u8 | sample rate u16 LE | samples u32 LE, then for IMA ADPCM the first
    /// predictor i16 LE | step index u8 | 0, then two samples a byte (low nibble first). 8 kHz IMA ADPCM is 4000 bytes a
    /// second: a 30 s chat clip is about 120 KB and a 15 s Adventure Group oath about 60 KB, under the client's own
    /// 102400-byte oath cap (LC_AdventureGroupHelper.iMaxVoiceFileSize).</item>
    /// <item>77017 both ways: up version u8 | codec mask u8 once the IM login is done; down enabled u8 | codec u8 |
    /// longest clip in seconds u8 | largest clip in bytes u32 LE | the role id u64 LE. Without that answer the hooks record
    /// nothing.</item>
    /// <item>77018 up: receive id (u16 LE length + UTF-8) | chat type u32 LE | extra param (u16 LE length + UTF-8) | clip.
    /// The server answers on the IM tunnel: cmd 6 to the sender and cmd 10 (NotifyNewAudioMessage) to every recipient,
    /// the sender too, as for text.</item>
    /// <item>77019 up: clip id (u8 length + ASCII); down: status u8 | clip id (u8 length + ASCII) | clip.</item>
    /// </list>
    /// </summary>
    public static class VoiceClip
    {
        public const uint CapabilityMsgId = 77017;
        public const uint UploadMsgId = 77018;
        public const uint FetchMsgId = 77019;

        public const byte ProtocolVersion = 1;
        public const byte CodecImaAdpcm = 1;
        public const int SampleRate = 8000;
        public const int HeaderSize = 12;
        private const int AdpcmStateSize = 4;
        private static readonly byte[] Magic = { (byte)'R', (byte)'O', (byte)'3', (byte)'A' };

        public const byte FetchOk = 0, FetchMissing = 1, FetchForbidden = 2, FetchBusy = 3;

        // ---- clip ----

        /// <summary>16 kHz capture (two samples averaged into one) as an 8 kHz IMA ADPCM clip.</summary>
        public static byte[] EncodeFrom16k(IReadOnlyList<short> pcm16k)
        {
            int samples = pcm16k.Count / 2;
            var pcm = new short[samples];
            for (int i = 0; i < samples; i++) pcm[i] = (short)((pcm16k[2 * i] + pcm16k[2 * i + 1]) / 2);
            return Encode(pcm, SampleRate);
        }

        public static byte[] Encode(short[] pcm, int sampleRate)
        {
            int samples = pcm.Length;
            var clip = new byte[HeaderSize + AdpcmStateSize + (samples + 1) / 2];
            WriteHeader(clip, CodecImaAdpcm, sampleRate, samples);
            int predictor = samples > 0 ? pcm[0] : 0, index = 0;
            clip[HeaderSize] = (byte)predictor;
            clip[HeaderSize + 1] = (byte)(predictor >> 8);
            clip[HeaderSize + 2] = (byte)index;
            for (int i = 0; i < samples; i++)
            {
                int nibble = EncodeSample(pcm[i], ref predictor, ref index);
                int at = HeaderSize + AdpcmStateSize + i / 2;
                clip[at] |= (byte)((i & 1) == 0 ? nibble : nibble << 4);
            }
            return clip;
        }

        /// <summary>The clip's samples, or null when it is not a well-formed clip.</summary>
        public static short[]? Decode(byte[] clip)
        {
            if (!TryReadHeader(clip, out _, out int samples)) return null;
            var pcm = new short[samples];
            int predictor = (short)(clip[HeaderSize] | clip[HeaderSize + 1] << 8), index = Math.Min(88, (int)clip[HeaderSize + 2]);
            for (int i = 0; i < samples; i++)
            {
                int packed = clip[HeaderSize + AdpcmStateSize + i / 2];
                int nibble = (i & 1) == 0 ? packed & 0x0F : packed >> 4;
                pcm[i] = DecodeSample(nibble, ref predictor, ref index);
            }
            return pcm;
        }

        /// <summary>An 8 kHz clip at 16 kHz (each sample followed by the mean of it and the next).</summary>
        public static short[] To16k(short[] pcm8k)
        {
            var pcm = new short[pcm8k.Length * 2];
            for (int i = 0; i < pcm8k.Length; i++)
            {
                int next = i + 1 < pcm8k.Length ? pcm8k[i + 1] : pcm8k[i];
                pcm[2 * i] = pcm8k[i];
                pcm[2 * i + 1] = (short)((pcm8k[i] + next) / 2);
            }
            return pcm;
        }

        /// <summary>A well-formed clip: magic, version, IMA ADPCM, its rate and exactly the bytes its sample count needs.</summary>
        public static bool TryReadHeader(byte[]? clip, out int sampleRate, out int samples)
        {
            sampleRate = 0;
            samples = 0;
            if (clip == null || clip.Length < HeaderSize + AdpcmStateSize) return false;
            for (int i = 0; i < Magic.Length; i++) if (clip[i] != Magic[i]) return false;
            if (clip[4] != ProtocolVersion || clip[5] != CodecImaAdpcm) return false;
            sampleRate = clip[6] | clip[7] << 8;
            long count = clip[8] | (long)clip[9] << 8 | (long)clip[10] << 16 | (long)clip[11] << 24;
            if (sampleRate != SampleRate || count > int.MaxValue / 2) return false;
            samples = (int)count;
            return clip.Length == HeaderSize + AdpcmStateSize + (samples + 1) / 2;
        }

        /// <summary>Whole seconds of a clip, rounded to the nearest (the "%ds" its bubble shows), at least one.</summary>
        public static int Seconds(int samples, int sampleRate) => Math.Max(1, (samples + sampleRate / 2) / sampleRate);

        private static void WriteHeader(byte[] clip, byte codec, int sampleRate, int samples)
        {
            Buffer.BlockCopy(Magic, 0, clip, 0, Magic.Length);
            clip[4] = ProtocolVersion;
            clip[5] = codec;
            clip[6] = (byte)sampleRate;
            clip[7] = (byte)(sampleRate >> 8);
            clip[8] = (byte)samples;
            clip[9] = (byte)(samples >> 8);
            clip[10] = (byte)(samples >> 16);
            clip[11] = (byte)(samples >> 24);
        }

        // IMA ADPCM (the IMA/DVI reference tables).
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };
        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130,
            143, 157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282,
            1411, 1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493,
            10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767,
        };

        private static int EncodeSample(int sample, ref int predictor, ref int index)
        {
            int step = StepTable[index], diff = sample - predictor, nibble = 0;
            if (diff < 0) { nibble = 8; diff = -diff; }
            if (diff >= step) { nibble |= 4; diff -= step; }
            if (diff >= step >> 1) { nibble |= 2; diff -= step >> 1; }
            if (diff >= step >> 2) nibble |= 1;
            DecodeSample(nibble, ref predictor, ref index);
            return nibble;
        }

        private static short DecodeSample(int nibble, ref int predictor, ref int index)
        {
            int step = StepTable[index], diff = step >> 3;
            if ((nibble & 4) != 0) diff += step;
            if ((nibble & 2) != 0) diff += step >> 1;
            if ((nibble & 1) != 0) diff += step >> 2;
            predictor += (nibble & 8) != 0 ? -diff : diff;
            predictor = Math.Max(short.MinValue, Math.Min(short.MaxValue, predictor));
            index = Math.Max(0, Math.Min(88, index + IndexTable[nibble]));
            return (short)predictor;
        }

        // ---- game messages ----

        public static byte[] CapabilityRequest() => new[] { ProtocolVersion, (byte)(1 << CodecImaAdpcm) };

        public static byte[] CapabilityAnswer(bool enabled, int maxSeconds, int maxBytes, long roleId)
        {
            var body = new byte[15];
            body[0] = (byte)(enabled ? 1 : 0);
            body[1] = CodecImaAdpcm;
            body[2] = (byte)Math.Min(255, maxSeconds);
            for (int i = 0; i < 4; i++) body[3 + i] = (byte)(maxBytes >> (8 * i));
            for (int i = 0; i < 8; i++) body[7 + i] = (byte)(roleId >> (8 * i));
            return body;
        }

        public static bool TryReadCapabilityAnswer(byte[]? body, out bool enabled, out int maxSeconds, out int maxBytes, out long roleId)
        {
            enabled = false;
            maxSeconds = maxBytes = 0;
            roleId = 0;
            if (body == null || body.Length < 15 || body[1] != CodecImaAdpcm) return false;
            enabled = body[0] == 1;
            maxSeconds = body[2];
            maxBytes = body[3] | body[4] << 8 | body[5] << 16 | body[6] << 24;
            for (int i = 0; i < 8; i++) roleId |= (long)body[7 + i] << (8 * i);
            return true;
        }

        public static byte[] Upload(string? receiveId, uint chatType, string? extraParam, byte[] clip)
        {
            var receive = Encoding.UTF8.GetBytes(receiveId ?? "");
            var extra = Encoding.UTF8.GetBytes(extraParam ?? "");
            var body = new byte[2 + receive.Length + 4 + 2 + extra.Length + clip.Length];
            int at = 0;
            WriteU16(body, ref at, receive.Length);
            Buffer.BlockCopy(receive, 0, body, at, receive.Length); at += receive.Length;
            body[at++] = (byte)chatType; body[at++] = (byte)(chatType >> 8); body[at++] = (byte)(chatType >> 16); body[at++] = (byte)(chatType >> 24);
            WriteU16(body, ref at, extra.Length);
            Buffer.BlockCopy(extra, 0, body, at, extra.Length); at += extra.Length;
            Buffer.BlockCopy(clip, 0, body, at, clip.Length);
            return body;
        }

        public static bool TryReadUpload(byte[]? body, out string receiveId, out uint chatType, out string extraParam, out byte[] clip)
        {
            receiveId = extraParam = "";
            chatType = 0;
            clip = Array.Empty<byte>();
            int at = 0;
            if (body == null || !TryReadString16(body, ref at, out receiveId) || body.Length - at < 4) return false;
            chatType = (uint)(body[at] | body[at + 1] << 8 | body[at + 2] << 16 | body[at + 3] << 24);
            at += 4;
            if (!TryReadString16(body, ref at, out extraParam)) return false;
            clip = new byte[body.Length - at];
            Buffer.BlockCopy(body, at, clip, 0, clip.Length);
            return true;
        }

        public static byte[]? FetchRequest(string? clipId)
        {
            var id = Encoding.ASCII.GetBytes(clipId ?? "");
            if (id.Length > 255) return null;
            var body = new byte[1 + id.Length];
            body[0] = (byte)id.Length;
            Buffer.BlockCopy(id, 0, body, 1, id.Length);
            return body;
        }

        public static bool TryReadFetchRequest(byte[]? body, out string clipId)
        {
            clipId = "";
            if (body == null || body.Length < 1 || body.Length != 1 + body[0]) return false;
            clipId = Encoding.ASCII.GetString(body, 1, body[0]);
            return true;
        }

        public static byte[] FetchAnswer(byte status, string? clipId, byte[]? clip)
        {
            var id = Encoding.ASCII.GetBytes(clipId ?? "");
            int length = Math.Min(255, id.Length);
            var payload = status == FetchOk && clip != null ? clip : Array.Empty<byte>();
            var body = new byte[2 + length + payload.Length];
            body[0] = status;
            body[1] = (byte)length;
            Buffer.BlockCopy(id, 0, body, 2, length);
            Buffer.BlockCopy(payload, 0, body, 2 + length, payload.Length);
            return body;
        }

        public static bool TryReadFetchAnswer(byte[]? body, out byte status, out string clipId, out byte[] clip)
        {
            status = 0;
            clipId = "";
            clip = Array.Empty<byte>();
            if (body == null || body.Length < 2 || body.Length < 2 + body[1]) return false;
            status = body[0];
            clipId = Encoding.ASCII.GetString(body, 2, body[1]);
            clip = new byte[body.Length - 2 - body[1]];
            Buffer.BlockCopy(body, 2 + body[1], clip, 0, clip.Length);
            return true;
        }

        private static void WriteU16(byte[] body, ref int at, int value)
        {
            body[at++] = (byte)value;
            body[at++] = (byte)(value >> 8);
        }

        private static bool TryReadString16(byte[]? body, ref int at, out string value)
        {
            value = "";
            if (body == null || body.Length - at < 2) return false;
            int length = body[at] | body[at + 1] << 8;
            at += 2;
            if (body.Length - at < length) return false;
            value = Encoding.UTF8.GetString(body, at, length);
            at += length;
            return true;
        }

        // ---- the few Tencent.IM messages the hooks answer themselves (GM_Chat.proto) ----

        /// <summary>The string and varint fields of a flat protobuf message (StartRecordAudioRequest, PlayAudioRequest);
        /// null when it does not parse.</summary>
        public static Dictionary<int, object>? PbFields(byte[]? body)
        {
            var fields = new Dictionary<int, object>();
            if (body == null) return fields;
            int at = 0;
            while (at < body.Length)
            {
                if (!PbReadVarint(body, ref at, out ulong key)) return null;
                int field = (int)(key >> 3), type = (int)(key & 7);
                if (type == 0)
                {
                    if (!PbReadVarint(body, ref at, out ulong value)) return null;
                    fields[field] = value;
                }
                else if (type == 2)
                {
                    if (!PbReadVarint(body, ref at, out ulong length) || length > (ulong)(body.Length - at)) return null;
                    fields[field] = Encoding.UTF8.GetString(body, at, (int)length);
                    at += (int)length;
                }
                else return null;
            }
            return fields;
        }

        private static bool PbReadVarint(byte[] body, ref int at, out ulong value)
        {
            value = 0;
            for (int shift = 0; shift < 64 && at < body.Length; shift += 7)
            {
                byte b = body[at++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
            }
            return false;
        }

        /// <summary>A response whose only field is bool chat_result = 1.</summary>
        public static byte[] ChatResult(bool ok) => ok ? new byte[] { 0x08, 0x01 } : Array.Empty<byte>();

        /// <summary>NotifyNewAudioMessage { msg = AudioChatMessage { message_id 1, sender_id 2, receive_id 3, send_time 4,
        /// audio_file 5, extra_param 6, audio_duration 7 } }.</summary>
        public static byte[] NotifyNewAudio(string? messageId, string? senderId, string? receiveId, ulong sendTime, string? audioFile,
            string? extraParam, uint duration)
        {
            var msg = new List<byte>();
            PbString(msg, 1, messageId);
            PbString(msg, 2, senderId);
            PbString(msg, 3, receiveId);
            PbVarint(msg, 4, sendTime);
            PbString(msg, 5, audioFile);
            PbString(msg, 6, extraParam);
            PbVarint(msg, 7, duration);
            var notify = new List<byte> { 0x0A };
            PbRawVarint(notify, (ulong)msg.Count);
            notify.AddRange(msg);
            return notify.ToArray();
        }

        private static void PbString(List<byte> to, int field, string? value)
        {
            if (string.IsNullOrEmpty(value)) return;
            var bytes = Encoding.UTF8.GetBytes(value);
            to.Add((byte)(field << 3 | 2));
            PbRawVarint(to, (ulong)bytes.Length);
            to.AddRange(bytes);
        }

        private static void PbVarint(List<byte> to, int field, ulong value)
        {
            if (value == 0) return;
            to.Add((byte)(field << 3));
            PbRawVarint(to, value);
        }

        private static void PbRawVarint(List<byte> to, ulong value)
        {
            while (value >= 0x80) { to.Add((byte)(value | 0x80)); value >>= 7; }
            to.Add((byte)value);
        }
    }
}
