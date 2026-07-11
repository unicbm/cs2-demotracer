using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DemoTracer;

public sealed partial class DemoTracerPlugin
{
    private const string VoiceDtvMagic = "DTRVOICE";
    private const ushort VoiceDtvVersion = 2;
    private const byte VoiceDtvFlagFormat = 0x01;
    private const byte VoiceDtvFlagSampleRate = 0x02;
    private const byte VoiceDtvFlagVoiceLevel = 0x04;
    private const byte VoiceDtvFlagSequenceBytes = 0x08;
    private const byte VoiceDtvFlagSectionNumber = 0x10;
    private const byte VoiceDtvFlagUncompressedSampleOffset = 0x20;
    private const byte VoiceDtvFlagNumPackets = 0x40;
    private const byte VoiceDtvFlagPacketOffsets = 0x80;
    private const int VoiceDataFormatOpus = 2;
    private const int DefaultVoiceSampleRate = 48_000;
    private const int VoiceOpusSamplesPerPacket = 480;
    private const float VoicePlaybackEpsilonSeconds = 0.002f;
    private const float VoiceTimelineGapThresholdSeconds = 0.12f;
    private static readonly byte[] VoiceDtvMagicBytes = Encoding.ASCII.GetBytes(VoiceDtvMagic);

    private VoiceClipPlaybackState? _voiceTestPlayback;
    private int _nextVoiceSectionNumber = 1;
    private bool _voiceAutoEnabled = true;
    private string _loadedVoiceClipPath = string.Empty;
    private int _loadedVoiceRound = -1;
    private int _loadedVoiceRecordingStartTick;
    private int _loadedVoiceLiveStartTick;
    private float _loadedVoiceTickRate;

    [ConsoleCommand("dtr_voice_auto", "dtr_voice_auto [status|on|off]")]
    public void VoiceAutoCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (command.ArgCount >= 2)
        {
            var mode = command.GetArg(1);
            if (mode.Equals("status", StringComparison.OrdinalIgnoreCase))
            {
                command.ReplyToCommand(FormatVoiceAutoStatus());
                return;
            }

            _voiceAutoEnabled = ParseOnOff(mode, _voiceAutoEnabled);
        }

        command.ReplyToCommand(FormatVoiceAutoStatus());
    }

    [ConsoleCommand("dtr_voice_test", "dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]")]
    public void VoiceTestCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!BotControllerNative.CanSendVoice)
        {
            command.ReplyToCommand($"[DTR ERR] voice send unavailable; {BotControllerNative.RuntimeSummary}");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]");
            return;
        }
        if (!TryParseSlotAt(command, 2, out var senderSlot))
            return;

        var sender = Utilities.GetPlayerFromSlot(senderSlot);
        if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
        {
            command.ReplyToCommand(
                $"[DTR ERR] sender slot {senderSlot} is not a safe replay bot target");
            return;
        }
        if (!TryResolveVoiceRecipients(player, command, 3, out var recipients))
            return;
        if (!TryLoadVoiceClip(command.GetArg(1), command.ReplyToCommand, out var clip))
            return;

        StopVoiceTestPlayback("voice_test_replace", printSummary: false);

        var senderClient = senderSlot;
        var speakerXuid = clip.Manifest.SelectedXuid != 0
            ? clip.Manifest.SelectedXuid
            : clip.Frames.First().Xuid;
        var speakers = new Dictionary<ulong, VoiceSpeakerPlayback>
        {
            [speakerXuid] = new(
                senderSlot,
                senderClient,
                speakerXuid,
                AllocateVoiceSectionBase(clip.Frames.Count),
                expectedTeam: null)
        };
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            Server.CurrentTime,
            speakers,
            speakerXuid,
            clip.Frames,
            recipients);

        command.ReplyToCommand(
            $"[DTR OK] voice test started frames={clip.Frames.Count} duration={clip.Manifest.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s " +
            $"sender_slot={senderSlot} sender_client={senderClient} xuid={speakerXuid} recipients={FormatSlotList(recipients)}");
    }

    [ConsoleCommand("dtr_voice_mix", "dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]")]
    public void VoiceMixCommand(CCSPlayerController? player, CommandInfo command)
    {
        if (!CheckAbi(command))
            return;
        if (!BotControllerNative.CanSendVoice)
        {
            command.ReplyToCommand($"[DTR ERR] voice send unavailable; {BotControllerNative.RuntimeSummary}");
            return;
        }
        if (command.ArgCount < 3)
        {
            command.ReplyToCommand("usage: dtr_voice_mix <voice_clip.dtv> <xuid=slot[,xuid=slot...]|loaded> [recipient_slot|all]");
            return;
        }
        if (!TryLoadVoiceClip(command.GetArg(1), command.ReplyToCommand, out var clip))
            return;
        if (!TryBuildVoiceSpeakerMap(command.GetArg(2), clip, command.ReplyToCommand, out var speakers))
            return;
        if (!TryResolveVoiceRecipients(player, command, 3, out var recipients))
            return;

        if (!TryPrepareVoiceMixFrames(clip, speakers, command.ReplyToCommand, out var frames))
            return;

        StopVoiceTestPlayback("voice_mix_replace", printSummary: false);
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            Server.CurrentTime,
            speakers,
            defaultSpeakerXuid: 0,
            frames,
            recipients);

        command.ReplyToCommand(
            $"[DTR OK] voice mix started speakers={speakers.Count} frames={frames.Count}/{clip.Frames.Count} " +
            $"duration={clip.Manifest.DurationSeconds.ToString("F2", CultureInfo.InvariantCulture)}s recipients={FormatSlotList(recipients)}");
    }

    [ConsoleCommand("dtr_voice_stop", "dtr_voice_stop")]
    public void VoiceStopCommand(CCSPlayerController? player, CommandInfo command)
    {
        var stopped = StopVoiceTestPlayback("manual_stop", printSummary: false);
        command.ReplyToCommand(stopped ? "dtr: voice test stopped" : "dtr: no active voice test");
    }

    private void ProcessVoiceTestPlayback()
    {
        var state = _voiceTestPlayback;
        if (state == null)
            return;

        if (!BotControllerNative.CanSendVoice)
        {
            StopVoiceTestPlayback("voice_send_unavailable");
            return;
        }

        foreach (var speaker in state.Speakers.Values)
        {
            var sender = Utilities.GetPlayerFromSlot(speaker.Slot);
            if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
            {
                StopVoiceTestPlayback($"unsafe_sender_slot_{speaker.Slot}");
                return;
            }
            if (speaker.ExpectedTeam.HasValue && sender.Team != speaker.ExpectedTeam.Value)
            {
                StopVoiceTestPlayback($"sender_team_mismatch_slot_{speaker.Slot}");
                return;
            }
        }

        state.PruneRecipients(IsVoiceRecipient);
        if (state.RecipientSlots.Count == 0)
        {
            StopVoiceTestPlayback("no_live_recipients");
            return;
        }

        var elapsed = Math.Max(0.0f, Server.CurrentTime - state.StartTime);
        while (state.NextFrameIndex < state.Frames.Count)
        {
            var frame = state.Frames[state.NextFrameIndex];
            var due = frame.PlaybackSeconds;
            if (due > elapsed + VoicePlaybackEpsilonSeconds)
                break;

            if (!state.TryResolveSpeaker(frame.Xuid, out var speaker))
            {
                state.NextFrameIndex++;
                continue;
            }

            var sectionNumber = speaker.NextSectionNumber;
            var sender = Utilities.GetPlayerFromSlot(speaker.Slot);
            if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
            {
                StopVoiceTestPlayback($"unsafe_sender_slot_{speaker.Slot}");
                return;
            }

            var audibleRecipients = AudibleVoiceRecipientsForSpeaker(
                state.RecipientSlots,
                sender,
                speaker.ExpectedTeam);
            if (audibleRecipients.Count == 0)
            {
                state.NextFrameIndex++;
                continue;
            }

            foreach (var recipientSlot in audibleRecipients)
            {
                var rc = BotControllerNative.SendVoiceFrame(
                    recipientSlot,
                    speaker.Client,
                    speaker.Xuid,
                    frame.Audio,
                    frame.SampleRate,
                    frame.VoiceLevel,
                    frame.SequenceBytes,
                    sectionNumber,
                    frame.UncompressedSampleOffset,
                    frame.NumPackets,
                    frame.PacketOffsets,
                    tick: -1,
                    audibleMask: 1);
                state.SentPackets++;
                if (rc != 0)
                {
                    state.FailedPackets++;
                    state.LastReturnCode = rc;
                    StopVoiceTestPlayback($"voice_send_failed_rc_{rc}");
                    return;
                }
            }

            state.SentFrames++;
            speaker.NextSectionNumber++;
            state.NextFrameIndex++;
        }

        if (state.NextFrameIndex >= state.Frames.Count)
        {
            Server.PrintToConsole(
                $"dtr: voice test finished path=\"{EscapeConsoleString(state.Path)}\" sent_frames={state.SentFrames} " +
                $"sent_packets={state.SentPackets} failed_packets={state.FailedPackets} last_rc={state.LastReturnCode}");
            _voiceTestPlayback = null;
        }
    }

    private bool StopVoiceTestPlayback(string reason, bool printSummary = true)
    {
        var state = _voiceTestPlayback;
        if (state == null)
            return false;

        _voiceTestPlayback = null;
        if (printSummary)
        {
            Server.PrintToConsole(
                $"dtr: voice test stopped reason={reason} path=\"{EscapeConsoleString(state.Path)}\" " +
                $"sent_frames={state.SentFrames} sent_packets={state.SentPackets} failed_packets={state.FailedPackets} last_rc={state.LastReturnCode}");
        }
        return true;
    }

    private int AllocateVoiceSectionBase(int frameCount)
    {
        if (_nextVoiceSectionNumber > int.MaxValue - Math.Max(frameCount, 1) - 32)
            _nextVoiceSectionNumber = 1;

        var sectionBase = _nextVoiceSectionNumber;
        _nextVoiceSectionNumber += Math.Max(frameCount, 1) + 16;
        return sectionBase;
    }

    private bool TryLoadVoiceClip(string clipPath, Action<string> reply, out LoadedVoiceClip clip)
    {
        clip = default;
        try
        {
            var resolvedPath = ResolveReadableManifestPath(clipPath);
            var data = File.ReadAllBytes(resolvedPath);
            if (!LooksLikeVoiceDtvBytes(data))
            {
                reply("[DTR ERR] unsupported voice clip format; expected DTRVOICE v2 .dtv");
                return false;
            }

            var offset = VoiceDtvMagicBytes.Length;
            var version = ReadDtvUInt16(data, ref offset, "version");
            if (version != VoiceDtvVersion)
            {
                reply($"[DTR ERR] unsupported voice clip version={version}; expected={VoiceDtvVersion}");
                return false;
            }
            var flags = ReadDtvUInt16(data, ref offset, "flags");
            if (flags != 0)
            {
                reply($"[DTR ERR] unsupported voice clip flags=0x{flags:X}");
                return false;
            }

            var tickRate = ReadDtvFloat32(data, ref offset, "tick_rate");
            var startTick = ReadDtvInt32(data, ref offset, "start_tick");
            var endTick = ReadDtvInt32(data, ref offset, "end_tick");
            var selectedXuid = ReadDtvUInt64(data, ref offset, "selected_xuid");
            _ = ReadDtvInt32(data, ref offset, "selected_client");
            var speakerCountRaw = ReadDtvUInt32(data, ref offset, "speaker_count");
            var frameCountRaw = ReadDtvUInt32(data, ref offset, "frame_count");
            var audioByteCountRaw = ReadDtvUInt64(data, ref offset, "audio_len");
            if (!float.IsFinite(tickRate) || tickRate <= 0.0f)
            {
                reply("[DTR ERR] voice clip tick_rate must be positive");
                return false;
            }
            if (frameCountRaw == 0)
            {
                reply("[DTR ERR] voice clip contains no frames");
                return false;
            }
            if (speakerCountRaw > int.MaxValue || frameCountRaw > int.MaxValue || audioByteCountRaw > int.MaxValue)
            {
                reply("[DTR ERR] voice clip is too large");
                return false;
            }
            var speakerCount = (int)speakerCountRaw;
            var frameCount = (int)frameCountRaw;

            _ = ReadDtvString(data, ref offset, "demo_stem");
            _ = ReadDtvString(data, ref offset, "demo_sha256");
            var map = ReadDtvString(data, ref offset, "map");

            var speakers = new List<VoiceClipSpeaker>(speakerCount);
            for (var i = 0; i < speakerCount; i++)
            {
                var xuid = ReadDtvUInt64(data, ref offset, $"speakers[{i}].xuid");
                var client = ReadDtvInt32(data, ref offset, $"speakers[{i}].client");
                var speakerFrameCount = ReadDtvUInt32(data, ref offset, $"speakers[{i}].frame_count");
                if (speakerFrameCount > int.MaxValue)
                    throw new InvalidDataException($"speaker {i} frame_count is too large");
                speakers.Add(new VoiceClipSpeaker
                {
                    Xuid = xuid,
                    Client = client,
                    FrameCount = (int)speakerFrameCount
                });
            }

            if (speakers.Count == 0)
            {
                reply("[DTR ERR] voice clip contains no speakers");
                return false;
            }

            var decodedFrames = new List<DtvFrameInfo>(frameCount);
            var relativeTick = 0U;
            for (var i = 0; i < frameCount; i++)
            {
                var tickDelta = ReadDtvUVarint(data, ref offset, $"frames[{i}].tick_delta");
                if (tickDelta > uint.MaxValue - relativeTick)
                    throw new InvalidDataException($"voice frame {i} relative_tick overflow");
                relativeTick += (uint)tickDelta;

                var speakerIndex = ReadDtvUVarint(data, ref offset, $"frames[{i}].speaker_index");
                if (speakerIndex >= (ulong)speakers.Count)
                    throw new InvalidDataException($"voice frame {i} speaker_index={speakerIndex} out of range");

                var audioLengthRaw = ReadDtvUVarint(data, ref offset, $"frames[{i}].audio_len");
                if (audioLengthRaw == 0 || audioLengthRaw > int.MaxValue)
                    throw new InvalidDataException($"voice frame {i} invalid audio_len={audioLengthRaw}");

                var frameFlags = ReadDtvByte(data, ref offset, $"frames[{i}].flags");
                var knownFlags = VoiceDtvFlagFormat |
                                 VoiceDtvFlagSampleRate |
                                 VoiceDtvFlagVoiceLevel |
                                 VoiceDtvFlagSequenceBytes |
                                 VoiceDtvFlagSectionNumber |
                                 VoiceDtvFlagUncompressedSampleOffset |
                                 VoiceDtvFlagNumPackets |
                                 VoiceDtvFlagPacketOffsets;
                if ((frameFlags & ~knownFlags) != 0)
                    throw new InvalidDataException($"voice frame {i} has unsupported flags=0x{frameFlags:X2}");

                var speaker = speakers[(int)speakerIndex];
                var frame = new DtvFrameInfo
                {
                    RelativeTick = relativeTick,
                    Xuid = speaker.Xuid,
                    AudioLength = (int)audioLengthRaw
                };
                if ((frameFlags & VoiceDtvFlagFormat) != 0)
                    frame.Format = checked((int)ReadDtvSVarint(data, ref offset, $"frames[{i}].format"));
                if ((frameFlags & VoiceDtvFlagSampleRate) != 0)
                    frame.SampleRate = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].sample_rate"));
                if ((frameFlags & VoiceDtvFlagVoiceLevel) != 0)
                    frame.VoiceLevel = ReadDtvFloat32(data, ref offset, $"frames[{i}].voice_level");
                if ((frameFlags & VoiceDtvFlagSequenceBytes) != 0)
                    frame.SequenceBytes = checked((int)ReadDtvSVarint(data, ref offset, $"frames[{i}].sequence_bytes"));
                if ((frameFlags & VoiceDtvFlagSectionNumber) != 0)
                    frame.SectionNumber = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].section_number"));
                if ((frameFlags & VoiceDtvFlagUncompressedSampleOffset) != 0)
                    frame.UncompressedSampleOffset = checked((int)ReadDtvUVarint(data, ref offset, $"frames[{i}].uncompressed_sample_offset"));
                if ((frameFlags & VoiceDtvFlagNumPackets) != 0)
                    frame.NumPackets = checked((uint)ReadDtvUVarint(data, ref offset, $"frames[{i}].num_packets"));
                if ((frameFlags & VoiceDtvFlagPacketOffsets) != 0)
                {
                    var packetOffsetCount = ReadDtvUVarint(data, ref offset, $"frames[{i}].packet_offset_count");
                    if (packetOffsetCount > int.MaxValue)
                        throw new InvalidDataException($"voice frame {i} packet_offset_count is too large");
                    frame.PacketOffsets = new uint[(int)packetOffsetCount];
                    for (var packetIndex = 0; packetIndex < frame.PacketOffsets.Length; packetIndex++)
                    {
                        frame.PacketOffsets[packetIndex] = checked((uint)ReadDtvUVarint(
                            data,
                            ref offset,
                            $"frames[{i}].packet_offsets[{packetIndex}]"));
                    }
                }
                decodedFrames.Add(frame);
            }

            var audioByteCount = (int)audioByteCountRaw;
            if (data.Length - offset != audioByteCount)
                throw new InvalidDataException(
                    $"voice audio blob length mismatch expected={audioByteCount} actual={data.Length - offset}");

            var audioOffset = offset;
            for (var i = 0; i < decodedFrames.Count; i++)
            {
                var frame = decodedFrames[i];
                if (audioOffset > data.Length - frame.AudioLength)
                    throw new InvalidDataException($"voice frame {i} audio extends beyond blob");
                frame.Audio = new byte[frame.AudioLength];
                Buffer.BlockCopy(data, audioOffset, frame.Audio, 0, frame.AudioLength);
                audioOffset += frame.AudioLength;
            }

            if (audioOffset != data.Length)
                throw new InvalidDataException("voice frame audio lengths do not consume the full audio blob");

            var manifest = new VoiceClipManifest
            {
                Map = string.IsNullOrWhiteSpace(map) ? null : map,
                TickRate = tickRate,
                SelectedXuid = selectedXuid,
                StartTick = startTick,
                EndTick = endTick,
                DurationSeconds = Math.Max(0, endTick - startTick) / tickRate,
                Speakers = speakers
            };
            if (!CurrentMapMatchesManifest(manifest.Map ?? string.Empty, out var currentMap))
            {
                reply(
                    $"[DTR WARN] map mismatch: server=\"{currentMap}\" voice_clip=\"{manifest.Map}\" path=\"{clipPath}\"");
            }
            if (!TryBuildVoiceFrames(manifest, decodedFrames, reply, out var frames))
                return false;

            clip = new LoadedVoiceClip(resolvedPath, manifest, frames);
            return true;
        }
        catch (Exception ex)
        {
            reply($"[DTR ERR] failed to read voice clip: {ex.Message}");
            return false;
        }
    }

    private static bool TryBuildVoiceFrames(
        VoiceClipManifest manifest,
        IReadOnlyList<DtvFrameInfo> decodedFrames,
        Action<string> reply,
        out List<VoiceClipRuntimeFrame> frames)
    {
        frames = new List<VoiceClipRuntimeFrame>(decodedFrames.Count);
        var nextContinuousSecondsByXuid = new Dictionary<ulong, float>();
        var orderedFrames = decodedFrames
            .Select((frame, index) => (Frame: frame, Index: index))
            .OrderBy(entry => entry.Frame.RelativeTick)
            .ThenBy(entry => entry.Index);

        foreach (var entry in orderedFrames)
        {
            var frame = entry.Frame;
            var i = entry.Index;
            if (frame.Format != VoiceDataFormatOpus)
            {
                reply(
                    $"[DTR ERR] voice frame {i} format={frame.Format}; only Opus format={VoiceDataFormatOpus} is supported");
                return false;
            }

            var audio = frame.Audio;
            if (audio.Length == 0)
            {
                reply($"[DTR ERR] voice frame {i} has empty audio");
                return false;
            }

            var sampleRate = frame.SampleRate;
            if (sampleRate <= 0)
                sampleRate = DefaultVoiceSampleRate;
            var voiceLevel = frame.VoiceLevel;
            if (!TryBuildPacketOffsets(
                    i,
                    audio.Length,
                    frame.PacketOffsets,
                    frame.NumPackets,
                    reply,
                    out var packetOffsets,
                    out var numPackets))
                return false;

            var xuid = frame.Xuid == 0 ? manifest.SelectedXuid : frame.Xuid;
            var demoSeconds = frame.RelativeTick / manifest.TickRate;
            nextContinuousSecondsByXuid.TryGetValue(xuid, out var nextContinuousSeconds);
            var playbackSeconds = demoSeconds - nextContinuousSeconds > VoiceTimelineGapThresholdSeconds
                ? demoSeconds
                : nextContinuousSeconds;
            frames.Add(new VoiceClipRuntimeFrame(
                frame.RelativeTick,
                playbackSeconds,
                xuid,
                audio,
                sampleRate,
                voiceLevel,
                frame.SequenceBytes,
                frame.SectionNumber,
                frame.UncompressedSampleOffset,
                numPackets,
                packetOffsets));
            var packetCountForTiming = Math.Max(1U, numPackets);
            nextContinuousSecondsByXuid[xuid] = playbackSeconds +
                (packetCountForTiming * VoiceOpusSamplesPerPacket) / (float)sampleRate;
        }

        frames.Sort(static (left, right) =>
        {
            var cmp = left.PlaybackSeconds.CompareTo(right.PlaybackSeconds);
            if (cmp != 0)
                return cmp;
            cmp = left.RelativeTick.CompareTo(right.RelativeTick);
            return cmp != 0 ? cmp : left.Xuid.CompareTo(right.Xuid);
        });
        return true;
    }

    private static bool TryBuildPacketOffsets(
        int frameIndex,
        int audioLength,
        IReadOnlyList<uint> rawOffsets,
        uint? rawNumPackets,
        Action<string> reply,
        out uint[] packetOffsets,
        out uint numPackets)
    {
        numPackets = rawNumPackets.GetValueOrDefault(0);
        if (numPackets > 0)
        {
            if (numPackets > int.MaxValue)
            {
                reply($"[DTR ERR] voice frame {frameIndex} num_packets={numPackets} is too large");
                packetOffsets = [];
                return false;
            }
            var requiredPackets = (int)numPackets;
            if (rawOffsets.Count < requiredPackets)
            {
                reply(
                    $"[DTR ERR] voice frame {frameIndex} num_packets={numPackets} but packet_offsets={rawOffsets.Count}");
                packetOffsets = [];
                return false;
            }
            packetOffsets = rawOffsets.Take(requiredPackets).ToArray();
        }
        else
        {
            packetOffsets = rawOffsets.Where(offset => offset != 0).ToArray();
            numPackets = (uint)packetOffsets.Length;
        }

        uint previous = 0;
        for (var i = 0; i < packetOffsets.Length; i++)
        {
            var offset = packetOffsets[i];
            if (offset == 0 || offset <= previous || offset > audioLength)
            {
                reply(
                    $"[DTR ERR] voice frame {frameIndex} has invalid packet_offsets[{i}]={offset} for audio_len={audioLength}");
                return false;
            }
            previous = offset;
        }

        return true;
    }

    private static bool LooksLikeVoiceDtvBytes(byte[] data)
    {
        if (data.Length < VoiceDtvMagicBytes.Length)
            return false;
        for (var i = 0; i < VoiceDtvMagicBytes.Length; i++)
        {
            if (data[i] != VoiceDtvMagicBytes[i])
                return false;
        }
        return true;
    }

    private static byte ReadDtvByte(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, 1, name);
        return data[offset++];
    }

    private static ushort ReadDtvUInt16(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(ushort), name);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
        offset += sizeof(ushort);
        return value;
    }

    private static int ReadDtvInt32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(int), name);
        var value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
        offset += sizeof(int);
        return value;
    }

    private static uint ReadDtvUInt32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(uint), name);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
        offset += sizeof(uint);
        return value;
    }

    private static ulong ReadDtvUInt64(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(ulong), name);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset, sizeof(ulong)));
        offset += sizeof(ulong);
        return value;
    }

    private static float ReadDtvFloat32(byte[] data, ref int offset, string name)
    {
        EnsureDtvAvailable(data, offset, sizeof(float), name);
        var raw = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(float)));
        offset += sizeof(float);
        return BitConverter.Int32BitsToSingle(raw);
    }

    private static string ReadDtvString(byte[] data, ref int offset, string name)
    {
        var lengthRaw = ReadDtvUVarint(data, ref offset, $"{name}.length");
        if (lengthRaw > int.MaxValue)
            throw new InvalidDataException($"{name} is too large");
        var length = (int)lengthRaw;
        EnsureDtvAvailable(data, offset, length, name);
        var value = Encoding.UTF8.GetString(data, offset, length);
        offset += length;
        return value;
    }

    private static ulong ReadDtvUVarint(byte[] data, ref int offset, string name)
    {
        ulong value = 0;
        var shift = 0;
        for (var i = 0; i < 10; i++)
        {
            EnsureDtvAvailable(data, offset, 1, name);
            var b = data[offset++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return value;
            shift += 7;
        }

        throw new InvalidDataException($"{name} has malformed varint encoding");
    }

    private static long ReadDtvSVarint(byte[] data, ref int offset, string name)
    {
        var raw = ReadDtvUVarint(data, ref offset, name);
        return (long)(raw >> 1) ^ -((long)raw & 1);
    }

    private static void EnsureDtvAvailable(byte[] data, int offset, int count, string name)
    {
        if (offset < 0 || count < 0 || offset > data.Length - count)
            throw new InvalidDataException($"truncated voice clip while reading {name}");
    }

    private bool TryBuildVoiceSpeakerMap(
        string mapping,
        LoadedVoiceClip clip,
        Action<string> reply,
        out Dictionary<ulong, VoiceSpeakerPlayback> speakers)
    {
        speakers = new Dictionary<ulong, VoiceSpeakerPlayback>();
        if (mapping.Equals("loaded", StringComparison.OrdinalIgnoreCase) ||
            mapping.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var xuid in UniqueVoiceXuids(clip).OrderBy(value => value))
            {
                var match = _loadedReplays
                    .Where(entry => entry.Value.SteamId == xuid && IsReplaySlotStillSafe(entry.Key))
                    .Select(entry => entry.Key)
                    .OrderBy(slot => slot)
                    .FirstOrDefault(-1);
                if (match >= 0)
                {
                    speakers[xuid] = new VoiceSpeakerPlayback(
                        match,
                        match,
                        xuid,
                        0,
                        _loadedReplays[match].ManifestTeam);
                }
            }
        }
        else
        {
            foreach (var rawPart in mapping.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separator = rawPart.Contains('=') ? '=' : ':';
                var parts = rawPart.Split(separator, 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !ulong.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var xuid) ||
                    !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot))
                {
                    reply($"[DTR ERR] invalid voice speaker mapping \"{rawPart}\"; expected xuid=slot");
                    return false;
                }
                speakers[xuid] = new VoiceSpeakerPlayback(
                    slot,
                    slot,
                    xuid,
                    0,
                    expectedTeam: null);
            }
        }

        if (speakers.Count == 0)
        {
            reply($"[DTR ERR] no voice speaker mapping matched; speakers={DescribeVoiceSpeakers(clip)}");
            return false;
        }

        foreach (var (xuid, speaker) in speakers.OrderBy(entry => entry.Value.Slot))
        {
            var sender = Utilities.GetPlayerFromSlot(speaker.Slot);
            if (sender is not { IsValid: true } || !IsReplayTargetBot(sender))
            {
                reply(
                    $"[DTR ERR] voice speaker xuid={xuid} slot={speaker.Slot} is not a safe replay bot target");
                return false;
            }
        }

        var mappedSpeakers = speakers;
        var unmapped = UniqueVoiceXuids(clip)
            .Where(xuid => !mappedSpeakers.ContainsKey(xuid))
            .Take(5)
            .ToArray();
        if (unmapped.Length > 0)
        {
            reply(
                $"[DTR WARN] unmapped voice speakers will be skipped: {string.Join(",", unmapped)}");
        }

        return true;
    }

    private bool TryPrepareVoiceMixFrames(
        LoadedVoiceClip clip,
        Dictionary<ulong, VoiceSpeakerPlayback> speakers,
        Action<string> reply,
        out List<VoiceClipRuntimeFrame> frames)
    {
        frames = clip.Frames
            .Where(frame => speakers.ContainsKey(frame.Xuid) ||
                            (frame.Xuid == 0 && speakers.ContainsKey(clip.Manifest.SelectedXuid)))
            .ToList();
        if (frames.Count == 0)
        {
            reply("[DTR ERR] no voice frames matched mapped speakers");
            return false;
        }

        foreach (var group in frames.GroupBy(frame => frame.Xuid == 0 ? clip.Manifest.SelectedXuid : frame.Xuid))
        {
            if (speakers.TryGetValue(group.Key, out var speaker))
                speaker.NextSectionNumber = AllocateVoiceSectionBase(group.Count());
        }

        return true;
    }

    private string ConfigureLoadedAutoVoiceClip(
        string manifestPath,
        int round,
        ManifestRound? roundMetadata,
        float manifestTickRate)
    {
        ClearLoadedAutoVoiceClip();
        if (!TryResolveVoiceSidecarForRound(manifestPath, round, out var clipPath))
            return string.Empty;

        _loadedVoiceClipPath = clipPath;
        _loadedVoiceRound = round;
        _loadedVoiceRecordingStartTick = roundMetadata?.RecordingStartTick ?? 0;
        _loadedVoiceLiveStartTick = roundMetadata?.StartTick ?? 0;
        _loadedVoiceTickRate = manifestTickRate > 0.0f
            ? manifestTickRate
            : 0.0f;
        return Path.GetFileName(clipPath);
    }

    private void ClearLoadedAutoVoiceClip()
    {
        _loadedVoiceClipPath = string.Empty;
        _loadedVoiceRound = -1;
        _loadedVoiceRecordingStartTick = 0;
        _loadedVoiceLiveStartTick = 0;
        _loadedVoiceTickRate = 0.0f;
    }

    private string TryStartLoadedAutoVoicePlayback(
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds,
        int startedSlots)
    {
        if (!_voiceAutoEnabled ||
            startedSlots <= 0 ||
            string.IsNullOrWhiteSpace(_loadedVoiceClipPath))
        {
            return string.Empty;
        }

        if (!BotControllerNative.CanSendVoice)
            return $"; voice_auto=unavailable {BotControllerNative.RuntimeSummary}";

        var diagnostics = new List<string>();
        void Collect(string message) => diagnostics.Add(message);

        if (!TryLoadVoiceClip(_loadedVoiceClipPath, Collect, out var clip))
            return $"; voice_auto=load_failed {FirstVoiceDiagnostic(diagnostics)}";
        if (anchor == ReplayStartAnchor.FreezePreroll &&
            _loadedVoiceLiveStartTick > 0 &&
            clip.Manifest.StartTick >= _loadedVoiceLiveStartTick)
        {
            return
                $"; voice_auto=deferred_live file={Path.GetFileName(clip.Path)} " +
                $"clip_start={clip.Manifest.StartTick} live_start={_loadedVoiceLiveStartTick}";
        }
        if (!TryBuildVoiceSpeakerMap("loaded", clip, Collect, out var speakers))
            return $"; voice_auto=map_failed {FirstVoiceDiagnostic(diagnostics)}";
        if (!TryPrepareVoiceMixFrames(clip, speakers, Collect, out var frames))
            return $"; voice_auto=frames_failed {FirstVoiceDiagnostic(diagnostics)}";

        var recipients = ResolveAllVoiceRecipients();
        if (recipients.Count == 0)
            return "; voice_auto=no_human_recipients";

        var (startTime, initialFrameIndex, offsetSeconds) =
            ComputeAutoVoiceStart(clip.Manifest, frames, anchor, freezeTimeSeconds);
        StopVoiceTestPlayback("voice_auto_replace", printSummary: false);
        _voiceTestPlayback = new VoiceClipPlaybackState(
            clip.Path,
            clip.Manifest.TickRate,
            startTime,
            speakers,
            defaultSpeakerXuid: 0,
            frames,
            recipients)
        {
            NextFrameIndex = initialFrameIndex
        };

        var fileName = Path.GetFileName(clip.Path);
        return
            $"; voice_auto=started file={fileName} speakers={speakers.Count} frames={frames.Count}/{clip.Frames.Count} " +
            $"recipients={FormatSlotList(recipients)} anchor={anchor.ToString().ToLowerInvariant()} offset={offsetSeconds.ToString("F2", CultureInfo.InvariantCulture)}s";
    }

    private (float StartTime, int InitialFrameIndex, float OffsetSeconds) ComputeAutoVoiceStart(
        VoiceClipManifest manifest,
        IReadOnlyList<VoiceClipRuntimeFrame> frames,
        ReplayStartAnchor anchor,
        float? freezeTimeSeconds)
    {
        var tickRate = manifest.TickRate > 0.0f
            ? manifest.TickRate
            : _loadedVoiceTickRate > 0.0f
                ? _loadedVoiceTickRate
                : DefaultVoiceSampleRate;
        var clipStartTick = manifest.StartTick;
        var anchorDemoTick = _loadedVoiceLiveStartTick;
        if (anchor == ReplayStartAnchor.FreezePreroll && _loadedVoiceLiveStartTick > 0)
        {
            var prerollSeconds = LoadedReplayVoicePrerollSeconds(freezeTimeSeconds, tickRate);
            var prerollTicks = (int)MathF.Round(prerollSeconds * tickRate);
            anchorDemoTick = Math.Max(_loadedVoiceRecordingStartTick, _loadedVoiceLiveStartTick - prerollTicks);
        }

        if (clipStartTick <= 0 || anchorDemoTick <= 0 || tickRate <= 0.0f)
            return (Server.CurrentTime, 0, 0.0f);

        var offsetSeconds = (anchorDemoTick - clipStartTick) / tickRate;
        var initialFrameIndex = offsetSeconds > 0.0f
            ? FirstVoiceFrameAtOrAfter(frames, offsetSeconds)
            : 0;
        return (Server.CurrentTime - offsetSeconds, initialFrameIndex, offsetSeconds);
    }

    private float LoadedReplayVoicePrerollSeconds(float? freezeTimeSeconds, float fallbackTickRate)
    {
        var maxRecordedPrerollSeconds = 0.0f;
        foreach (var replay in _loadedReplays.Values)
        {
            var tickRate = replay.TickRate > 0.0f ? replay.TickRate : fallbackTickRate;
            if (replay.PlayStartTickIndex == 0 || tickRate <= 0.0f)
                continue;
            maxRecordedPrerollSeconds = Math.Max(
                maxRecordedPrerollSeconds,
                replay.PlayStartTickIndex / tickRate);
        }

        if (freezeTimeSeconds.HasValue && freezeTimeSeconds.Value > 0.0f)
            return Math.Min(freezeTimeSeconds.Value, maxRecordedPrerollSeconds);

        if (_loadedVoiceLiveStartTick > 0 &&
            _loadedVoiceRecordingStartTick > 0 &&
            _loadedVoiceLiveStartTick > _loadedVoiceRecordingStartTick &&
            fallbackTickRate > 0.0f)
        {
            return (_loadedVoiceLiveStartTick - _loadedVoiceRecordingStartTick) / fallbackTickRate;
        }

        return maxRecordedPrerollSeconds;
    }

    private static int FirstVoiceFrameAtOrAfter(IReadOnlyList<VoiceClipRuntimeFrame> frames, float offsetSeconds)
    {
        var threshold = Math.Max(0.0f, offsetSeconds - VoicePlaybackEpsilonSeconds);
        for (var i = 0; i < frames.Count; i++)
        {
            if (frames[i].PlaybackSeconds >= threshold)
                return i;
        }

        return frames.Count;
    }

    private static string FirstVoiceDiagnostic(IReadOnlyList<string> diagnostics)
        => diagnostics.Count == 0 ? string.Empty : diagnostics[0];

    private static bool TryResolveVoiceSidecarForRound(
        string manifestPath,
        int round,
        out string clipPath)
    {
        clipPath = string.Empty;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in CandidateVoiceSidecarDirectories(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                continue;

            var root = Path.GetFullPath(directory);
            foreach (var fileName in ExactVoiceSidecarFileNames(round))
            {
                var candidate = Path.Combine(root, fileName);
                if (!seen.Add(candidate) || !File.Exists(candidate))
                    continue;
                if (LooksLikeVoiceDtvClip(candidate))
                {
                    clipPath = candidate;
                    return true;
                }
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(root, "*.dtv", SearchOption.TopDirectoryOnly)
                    .Where(path => seen.Add(path))
                    .Where(path => VoiceSidecarFileNameMatchesRound(Path.GetFileName(path), round))
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Take(32)
                    .ToArray();
            }
            catch
            {
                continue;
            }

            foreach (var candidate in matches)
            {
                if (LooksLikeVoiceDtvClip(candidate))
                {
                    clipPath = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> CandidateVoiceSidecarDirectories(string manifestPath)
    {
        var manifestDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (!string.IsNullOrWhiteSpace(manifestDir))
        {
            yield return Path.Combine(manifestDir, "voice");
            yield return manifestDir;
        }

        foreach (var gameDir in CandidateGameDirectories())
        {
            yield return Path.Combine(gameDir, "voice");
            yield return gameDir;
        }
    }

    private static IEnumerable<string> ExactVoiceSidecarFileNames(int round)
    {
        var plain = round.ToString(CultureInfo.InvariantCulture);
        var padded = round.ToString("D2", CultureInfo.InvariantCulture);
        yield return $"round{padded}.dtv";
        yield return $"round{plain}.dtv";
        yield return $"round{padded}_all.dtv";
        yield return $"round{plain}_all.dtv";
        yield return $"voice_round{padded}.dtv";
        yield return $"voice_round{plain}.dtv";
        yield return $"voice_round{padded}_all.dtv";
        yield return $"voice_round{plain}_all.dtv";
        yield return $"demotracer_voice_round{padded}.dtv";
        yield return $"demotracer_voice_round{plain}.dtv";
        yield return $"demotracer_voice_round{padded}_all.dtv";
        yield return $"demotracer_voice_round{plain}_all.dtv";
    }

    private static bool VoiceSidecarFileNameMatchesRound(string fileName, int round)
    {
        if (round < 0)
            return false;
        var stem = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        var pattern = $@"(^|[^0-9])round0*{round.ToString(CultureInfo.InvariantCulture)}([^0-9]|$)";
        return Regex.IsMatch(stem, pattern, RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeVoiceDtvClip(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length < VoiceDtvMagicBytes.Length)
                return false;
            Span<byte> magic = stackalloc byte[VoiceDtvMagicBytes.Length];
            return stream.Read(magic) == VoiceDtvMagicBytes.Length &&
                   magic.SequenceEqual(VoiceDtvMagicBytes);
        }
        catch
        {
            return false;
        }
    }

    private static List<int> ResolveAllVoiceRecipients()
        => Utilities.GetPlayers()
            .Where(IsVoiceRecipient)
            .Select(candidate => candidate.Slot)
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

    private string FormatVoiceAutoStatus()
    {
        var loaded = string.IsNullOrWhiteSpace(_loadedVoiceClipPath)
            ? "none"
            : $"round={_loadedVoiceRound} file={Path.GetFileName(_loadedVoiceClipPath)}";
        var active = _voiceTestPlayback == null
            ? "none"
            : $"file={Path.GetFileName(_voiceTestPlayback.Path)} speakers={_voiceTestPlayback.Speakers.Count} sent={_voiceTestPlayback.SentFrames}/{_voiceTestPlayback.Frames.Count}";
        return $"[DTR OK] voice_auto={FormatOnOff(_voiceAutoEnabled)} loaded={loaded} active={active}";
    }

    private string FormatVoiceAutoStatusInline()
    {
        var loaded = string.IsNullOrWhiteSpace(_loadedVoiceClipPath)
            ? "none"
            : $"{_loadedVoiceRound}:{Path.GetFileName(_loadedVoiceClipPath)}";
        var active = _voiceTestPlayback == null
            ? "none"
            : $"{Path.GetFileName(_voiceTestPlayback.Path)}:{_voiceTestPlayback.SentFrames}/{_voiceTestPlayback.Frames.Count}";
        return $"voice_auto={FormatOnOff(_voiceAutoEnabled)} voice_loaded={loaded} voice_active={active}";
    }

    private static IEnumerable<ulong> UniqueVoiceXuids(LoadedVoiceClip clip)
        => clip.Frames
            .Select(frame => frame.Xuid == 0 ? clip.Manifest.SelectedXuid : frame.Xuid)
            .Where(xuid => xuid != 0)
            .Distinct();

    private static string DescribeVoiceSpeakers(LoadedVoiceClip clip)
    {
        var speakers = clip.Manifest.Speakers.Count > 0
            ? clip.Manifest.Speakers.Select(speaker => $"{speaker.Xuid}:client{speaker.Client}:frames{speaker.FrameCount}")
            : clip.Frames
                .GroupBy(frame => frame.Xuid)
                .OrderByDescending(group => group.Count())
                .Select(group => $"{group.Key}:frames{group.Count()}");
        return string.Join(",", speakers.Take(12));
    }

    private bool TryResolveVoiceRecipients(
        CCSPlayerController? caller,
        CommandInfo command,
        int argIndex,
        out List<int> recipients)
    {
        recipients = new List<int>();
        if (command.ArgCount <= argIndex)
        {
            if (caller is { IsValid: true } liveCaller && IsVoiceRecipient(liveCaller))
            {
                recipients.Add(liveCaller.Slot);
                return true;
            }

            command.ReplyToCommand("usage: dtr_voice_test <voice_clip.dtv> <sender_slot> [recipient_slot|all]");
            return false;
        }

        var arg = command.GetArg(argIndex);
        if (arg.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            recipients = Utilities.GetPlayers()
                .Where(IsVoiceRecipient)
                .Select(candidate => candidate.Slot)
                .Distinct()
                .OrderBy(slot => slot)
                .ToList();
            if (recipients.Count == 0)
            {
                command.ReplyToCommand("[DTR ERR] no live human recipients");
                return false;
            }
            return true;
        }

        if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) ||
            slot < 0 ||
            slot >= MaxPlayerSlots)
        {
            command.ReplyToCommand($"dtr: recipient slot must be an integer from 0 to {MaxPlayerSlots - 1}, or all");
            return false;
        }

        var recipient = Utilities.GetPlayerFromSlot(slot);
        if (!IsVoiceRecipient(recipient))
        {
            command.ReplyToCommand($"[DTR ERR] recipient slot {slot} is not a live human client");
            return false;
        }

        recipients.Add(slot);
        return true;
    }

    private static bool IsVoiceRecipient(CCSPlayerController? player)
        => player is { IsValid: true } && !player.IsHLTV && !player.IsBot;

    private static List<int> AudibleVoiceRecipientsForSpeaker(
        IReadOnlyList<int> recipientSlots,
        CCSPlayerController sender,
        CsTeam? expectedTeam)
        => recipientSlots
            .Where(slot => CanVoiceRecipientHearSpeaker(
                Utilities.GetPlayerFromSlot(slot),
                sender,
                expectedTeam))
            .Distinct()
            .OrderBy(slot => slot)
            .ToList();

    private static bool CanVoiceRecipientHearSpeaker(
        CCSPlayerController? recipient,
        CCSPlayerController sender,
        CsTeam? expectedTeam)
    {
        if (recipient is not { IsValid: true } || recipient.IsHLTV || recipient.IsBot)
            return false;

        if (IsObserverVoiceRecipient(recipient))
            return true;

        if (!IsTeamVoiceParticipant(recipient))
            return false;

        var speakerTeam = expectedTeam ?? sender.Team;
        if (!IsTeamVoiceParticipant(speakerTeam))
            return false;

        return recipient.Team == speakerTeam;
    }

    private static bool IsObserverVoiceRecipient(CCSPlayerController player)
        => player.Team == CsTeam.Spectator;

    private static bool IsTeamVoiceParticipant(CCSPlayerController player)
        => IsTeamVoiceParticipant(player.Team);

    private static bool IsTeamVoiceParticipant(CsTeam team)
        => team is CsTeam.Terrorist or CsTeam.CounterTerrorist;

    private static string FormatSlotList(IReadOnlyList<int> slots)
        => slots.Count == 0 ? "none" : string.Join(",", slots);

    private readonly record struct LoadedVoiceClip(
        string Path,
        VoiceClipManifest Manifest,
        List<VoiceClipRuntimeFrame> Frames);

    private sealed class VoiceClipPlaybackState(
        string path,
        float tickRate,
        float startTime,
        Dictionary<ulong, VoiceSpeakerPlayback> speakers,
        ulong defaultSpeakerXuid,
        List<VoiceClipRuntimeFrame> frames,
        List<int> recipientSlots)
    {
        public string Path { get; } = path;
        public float TickRate { get; } = tickRate;
        public float StartTime { get; } = startTime;
        public Dictionary<ulong, VoiceSpeakerPlayback> Speakers { get; } = speakers;
        public ulong DefaultSpeakerXuid { get; } = defaultSpeakerXuid;
        public List<VoiceClipRuntimeFrame> Frames { get; } = frames;
        public List<int> RecipientSlots { get; private set; } = recipientSlots;
        public int NextFrameIndex { get; set; }
        public int SentFrames { get; set; }
        public int SentPackets { get; set; }
        public int FailedPackets { get; set; }
        public int LastReturnCode { get; set; }

        public void PruneRecipients(Func<CCSPlayerController?, bool> predicate)
        {
            RecipientSlots = RecipientSlots
                .Where(slot => predicate(Utilities.GetPlayerFromSlot(slot)))
                .ToList();
        }

        public bool TryResolveSpeaker(ulong xuid, out VoiceSpeakerPlayback speaker)
        {
            if (xuid != 0 && Speakers.TryGetValue(xuid, out speaker!))
                return true;
            if (DefaultSpeakerXuid != 0 && Speakers.TryGetValue(DefaultSpeakerXuid, out speaker!))
                return true;
            speaker = null!;
            return false;
        }
    }

    private sealed class VoiceSpeakerPlayback(
        int slot,
        int client,
        ulong xuid,
        int nextSectionNumber,
        CsTeam? expectedTeam)
    {
        public int Slot { get; } = slot;
        public int Client { get; } = client;
        public ulong Xuid { get; } = xuid;
        public int NextSectionNumber { get; set; } = nextSectionNumber;
        public CsTeam? ExpectedTeam { get; } = expectedTeam;
    }

    private sealed class VoiceClipRuntimeFrame(
        uint relativeTick,
        float playbackSeconds,
        ulong xuid,
        byte[] audio,
        int sampleRate,
        float voiceLevel,
        int sequenceBytes,
        int sectionNumber,
        int uncompressedSampleOffset,
        uint numPackets,
        uint[] packetOffsets)
    {
        public uint RelativeTick { get; } = relativeTick;
        public float PlaybackSeconds { get; } = playbackSeconds;
        public ulong Xuid { get; } = xuid;
        public byte[] Audio { get; } = audio;
        public int SampleRate { get; } = sampleRate;
        public float VoiceLevel { get; } = voiceLevel;
        public int SequenceBytes { get; } = sequenceBytes;
        public int SectionNumber { get; } = sectionNumber;
        public int UncompressedSampleOffset { get; } = uncompressedSampleOffset;
        public uint NumPackets { get; } = numPackets;
        public uint[] PacketOffsets { get; } = packetOffsets;
    }

    private sealed class VoiceClipManifest
    {
        public string? Map { get; init; }

        public float TickRate { get; init; }

        public ulong SelectedXuid { get; init; }

        public int StartTick { get; init; }

        public int EndTick { get; init; }

        public float DurationSeconds { get; init; }

        public List<VoiceClipSpeaker> Speakers { get; init; } = new();
    }

    private sealed class VoiceClipSpeaker
    {
        public ulong Xuid { get; init; }

        public int Client { get; init; }

        public int FrameCount { get; init; }
    }

    private sealed class DtvFrameInfo
    {
        public uint RelativeTick { get; init; }

        public ulong Xuid { get; init; }

        public int Format { get; set; } = VoiceDataFormatOpus;

        public int SampleRate { get; set; } = DefaultVoiceSampleRate;

        public float VoiceLevel { get; set; } = float.NaN;

        public int SequenceBytes { get; set; } = -1;

        public int SectionNumber { get; set; } = -1;

        public int UncompressedSampleOffset { get; set; } = -1;

        public uint? NumPackets { get; set; }

        public uint[] PacketOffsets { get; set; } = Array.Empty<uint>();

        public int AudioLength { get; init; }

        public byte[] Audio { get; set; } = Array.Empty<byte>();
    }
}
