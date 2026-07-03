using System.Globalization;
using Brutal.FmodApi;
using gatOS.Logging;
using gatOS.SimFs.Audio;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;
using KSA;
using TimeUnit = Brutal.FmodApi.TimeUnit; // KSA has its own TimeUnit; ours is the FMOD one

namespace gatOS.GameMod.Game.Ksa.Actuators;

/// <summary>
///     Userland audio playback through the game's FMOD Core system (GATOS_CUSTOM_AUDIO_PLAN):
///     executes the <c>audio.play</c>/<c>audio.set</c>/<c>audio.stop</c> commands against
///     <see cref="GameAudio.System"/>, owning the FMOD <c>Sound</c> cache, the live channel table,
///     the per-frame tick (finished-channel pruning, <c>end=</c> enforcement, deferred sound
///     release, status publish) and the unload teardown.
/// </summary>
/// <remarks>
///     <para><b>Game thread only</b> (threading rule 1): every method here runs either in the
///     Frame-phase command drain or the per-frame <c>DriveAudio</c> tick — the same thread that
///     pumps <c>System.Update()</c>. We never call <c>System.Update/Close/Release</c> (the game
///     owns those); we own every <c>Sound</c> we create and release it only once its version was
///     evicted from the store <i>and</i> no channel is still playing it (a release mid-playback
///     audibly cuts the channel).</para>
///     <para>FMOD copies the clip bytes at <c>createSound</c> (<c>Mode.OpenMemory</c>), so store
///     eviction / re-upload never disturbs a playing channel. Clips ≤ 1 MiB decode fully upfront
///     (<c>CreateSample</c>); larger clips use <c>CreateCompressedSample</c> — create stays cheap
///     (well inside the 2 s command timeout) and memory stays ≈ file size, while still allowing
///     concurrent playback of one <c>Sound</c> (a <c>CreateStream</c> sound would not).</para>
/// </remarks>
internal sealed class AudioActuator(AudioStore store, int maxChannels)
{
    /// <summary>Clips at or under this size are fully PCM-decoded at create (instant, tiny).</summary>
    private const int CreateSampleMaxBytes = 1 << 20;

    /// <summary>Status position quantum, ms — keeps the changed-only MQTT field mirror calm.</summary>
    private const long PositionQuantumMs = 100;

    private readonly Dictionary<(string Name, int Version), Sound> _sounds = new();
    private readonly Dictionary<string, ChannelEntry> _channels = new(StringComparer.Ordinal);
    private int _autoId;
    private bool _publishedEmpty = true; // the store starts with an empty snapshot

    /// <summary>Whether the per-frame tick can be skipped entirely (no channels, no cached sounds).</summary>
    internal bool IsEmpty => _channels.Count == 0 && _sounds.Count == 0 && _publishedEmpty;

    /// <summary>Routes one <c>audio.*</c> command (invoked by <see cref="KsaCatalog"/>, game thread).</summary>
    internal CommandResult Execute(SimCommand command) => command.Action switch
    {
        AudioCommands.PlayAction => Play(command),
        AudioCommands.SetAction => Set(command),
        AudioCommands.StopAction => Stop(command),
        _ => new CommandResult(CommandOutcome.Unsupported, $"unknown action '{command.Action}'"),
    };

    // ---- play --------------------------------------------------------------------------------

    [KsaAnchor("GameAudio.System.TryPlaySound(sound, group, paused, out channel); "
            + "GameAudio.GetChannelGroup(ChannelGroupType); Fmod Channel.TrySet{Position,Mode,LoopCount,"
            + "LoopPoints,Volume,Pan,Pitch,Paused}",
        SourceFile = "KSA/GameAudio.cs / Brutal.FmodApi/Fmod.cs", Verified = "2026-07-02",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "The game's own anti-pop idiom: play paused, configure, unpause. Group routing puts the "
            + "channel under the matching game volume slider (the enum groups are siblings of Master).")]
    private CommandResult Play(SimCommand c)
    {
        var name = c.Token ?? "";
        var v = c.Values ?? [];
        if (v.Count != AudioCommands.PlaySlots)
            return new CommandResult(CommandOutcome.Invalid, "audio.play expects the 7-slot values array");

        switch (store.TryGet(name, out var clip))
        {
            case AudioClipLookup.Missing:
                return new CommandResult(CommandOutcome.NotFound, $"no clip '{name}' in /sim/audio/file");
            case AudioClipLookup.Uploading:
                return new CommandResult(CommandOutcome.Busy, $"clip '{name}' is still uploading");
        }

        // Reusing a live id replaces it (the natural "restart the alarm"); otherwise honor the cap.
        var id = c.Aux ?? AutoId();
        if (_channels.Remove(id, out var replaced))
            StopEntry(id, replaced, "replaced");
        else if (_channels.Count >= maxChannels)
            return new CommandResult(CommandOutcome.Busy, $"all {maxChannels} audio channels are in use");

        if (CreateOrGetSound(clip!) is not { } sound)
            return new CommandResult(CommandOutcome.Fault,
                $"FMOD did not accept '{name}' (corrupt or unsupported container?)");

        var lengthMs = sound.TryGetLength(out var rawLength, TimeUnit.Ms) == Result.Ok ? rawLength : 0u;
        var startMs = (uint)v[AudioCommands.PlayStartMs];
        var endMs = v[AudioCommands.PlayEndMs] > 0
            ? Math.Min((uint)v[AudioCommands.PlayEndMs], lengthMs > 0 ? lengthMs : uint.MaxValue)
            : 0u;
        if (endMs > 0 && endMs <= startMs)
            return new CommandResult(CommandOutcome.Invalid, "end must land after start within the clip");
        var loop = v[AudioCommands.PlayLoop] > 0.5;
        var groupOrdinal = (int)v[AudioCommands.PlayGroup];
        var groupType = GroupType(groupOrdinal);

        // The game's own idiom: start paused, configure everything, then unpause (no pops).
        if (GameAudio.System.TryPlaySound(sound, GameAudio.GetChannelGroup(groupType), paused: true,
                out var channel) != Result.Ok)
            return new CommandResult(CommandOutcome.Fault, $"FMOD could not start a channel for '{name}'");

        if (startMs > 0 && channel.TrySetPosition(startMs, TimeUnit.Ms) != Result.Ok)
        {
            channel.TryStop();
            return new CommandResult(CommandOutcome.Invalid, $"start={startMs} is beyond the clip");
        }

        if (loop)
        {
            channel.TrySetMode(Mode.LoopNormal);
            channel.TrySetLoopCount(-1);
            if (endMs > 0)
                channel.TrySetLoopPoints(startMs, TimeUnit.Ms, endMs, TimeUnit.Ms);
        }

        var volume = v[AudioCommands.PlayVol];
        if (volume < 1)
            channel.TrySetVolume((float)volume);
        if (v[AudioCommands.PlayPan] is not 0.0)
            channel.TrySetPan((float)v[AudioCommands.PlayPan]);
        if (v[AudioCommands.PlayPitch] is not 1.0)
            channel.TrySetPitch((float)v[AudioCommands.PlayPitch]);
        channel.TrySetPaused(false);

        _channels[id] = new ChannelEntry
        {
            Channel = channel,
            ClipName = name,
            Version = clip!.Version,
            EndMs = loop ? 0 : endMs, // a looped range never ends; SetLoopPoints owns it
            Loop = loop,
            Volume = volume,
            LengthMs = lengthMs,
            Group = AudioCommands.GroupName(groupOrdinal) ?? "sfx",
        };
        _publishedEmpty = false;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Gets the cached FMOD <c>Sound</c> for this exact clip version, creating it on first
    ///     play. Null when FMOD rejects the bytes (the caller maps it to EIO).
    /// </summary>
    [KsaAnchor("GameAudio.System.TryCreateSound(bytes, Mode.OpenMemory|…, in exInfo{Length}, out sound); "
            + "Sound.TryGetLength/TryRelease",
        SourceFile = "KSA/GameAudio.cs (CreateFmodSound is the in-memory recipe) / Brutal.FmodApi/Fmod.cs",
        Verified = "2026-07-02", GameVersion = "2026.6.9.4750", Risk = ChurnRisk.Low,
        Notes = "FMOD copies the buffer at create and sniffs the container header (mp3/ogg/wav/flac) — "
            + "the store's byte[] is free immediately and the filename extension is irrelevant.")]
    private Sound? CreateOrGetSound(AudioClip clip)
    {
        var key = (clip.Name, clip.Version);
        if (_sounds.TryGetValue(key, out var cached))
            return cached;

        var mode = Mode.OpenMemory | Mode._2d
                   | (clip.Bytes.Length <= CreateSampleMaxBytes ? Mode.CreateSample : Mode.CreateCompressedSample);
        var exInfo = new CreateSoundExInfo { Length = (uint)clip.Bytes.Length };
        if (GameAudio.System.TryCreateSound(clip.Bytes, mode, in exInfo, out var sound) != Result.Ok)
            return null;

        _sounds[key] = sound;
        return sound;
    }

    // ---- set / stop ----------------------------------------------------------------------------

    private CommandResult Set(SimCommand c)
    {
        var target = c.Token ?? "";
        var pairs = c.Values ?? [];
        if (pairs.Count == 0 || pairs.Count % 2 != 0)
            return new CommandResult(CommandOutcome.Invalid, "audio.set expects (key, value) pairs");

        var matched = false;
        foreach (var (_, entry) in Resolve(target))
        {
            matched = true;
            for (var i = 0; i < pairs.Count; i += 2)
                ApplyPair(entry, (int)pairs[i], pairs[i + 1]);
        }

        return matched
            ? CommandResult.Ok
            : new CommandResult(CommandOutcome.NotFound, $"no live channel or clip '{target}' is playing");
    }

    private static void ApplyPair(ChannelEntry entry, int key, double value)
    {
        // Best-effort per setter: a channel finishing this very frame answers with an invalid-handle
        // result, which the next tick prunes — a race, not an error the writer can act on.
        switch (key)
        {
            case AudioCommands.SetVol:
                if (entry.Channel.TrySetVolume((float)value) == Result.Ok)
                    entry.Volume = value;
                break;
            case AudioCommands.SetPan:
                entry.Channel.TrySetPan((float)value);
                break;
            case AudioCommands.SetPitch:
                entry.Channel.TrySetPitch((float)value);
                break;
            case AudioCommands.SetPaused:
                if (entry.Channel.TrySetPaused(value > 0.5) == Result.Ok)
                    entry.Paused = value > 0.5;
                break;
            case AudioCommands.SetSeekMs:
                entry.Channel.TrySetPosition((uint)value, TimeUnit.Ms);
                break;
        }
    }

    private CommandResult Stop(SimCommand c)
    {
        var target = c.Token ?? "";
        if (target == "all")
        {
            foreach (var (id, entry) in _channels)
                StopEntry(id, entry, "stopped");
            _channels.Clear();
            return CommandResult.Ok; // idempotent by contract
        }

        List<string>? stopped = null;
        foreach (var (id, entry) in Resolve(target))
        {
            StopEntry(id, entry, "stopped");
            (stopped ??= []).Add(id);
        }

        if (stopped is null)
            return new CommandResult(CommandOutcome.NotFound, $"no live channel or clip '{target}' is playing");
        foreach (var id in stopped)
            _channels.Remove(id);
        return CommandResult.Ok;
    }

    /// <summary>Exact channel-id match first; else every channel playing the clip of that name.</summary>
    private IEnumerable<(string Id, ChannelEntry Entry)> Resolve(string target)
    {
        if (_channels.TryGetValue(target, out var exact))
        {
            yield return (target, exact);
            yield break;
        }

        foreach (var (id, entry) in _channels)
            if (entry.ClipName == target)
                yield return (id, entry);
    }

    private void StopEntry(string id, ChannelEntry entry, string reason)
    {
        entry.Channel.TryStop(); // already-finished channels answer invalid-handle; that is fine
        EmitFinished(id, entry.ClipName, reason);
    }

    // ---- per-frame tick ------------------------------------------------------------------------

    /// <summary>
    ///     The per-frame drive (game thread, right after the command drain): prunes channels that
    ///     finished, enforces <c>end=</c> (frame-rate precision, ~16 ms — and correct under
    ///     <c>pitch=</c>, which a DSP-clock stop would not be), releases cached <c>Sound</c>s whose
    ///     clip version was evicted once their channels are gone, and publishes the status snapshot.
    /// </summary>
    [KsaAnchor("Fmod Channel.TryIsPlaying/TryGetPosition/TryStop; Sound.TryRelease",
        SourceFile = "Brutal.FmodApi/Fmod.cs", Verified = "2026-07-02", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Low,
        Notes = "A finished channel handle answers non-Ok (FMOD recycles them); that IS the completion "
            + "signal. Sounds release deferred — never while a channel still plays them (audible cut).")]
    internal void Tick()
    {
        if (_channels.Count > 0)
        {
            List<string>? finished = null;
            foreach (var (id, entry) in _channels)
            {
                if (entry.Channel.TryIsPlaying(out var playing) != Result.Ok || !playing)
                {
                    EmitFinished(id, entry.ClipName, "ended");
                    (finished ??= []).Add(id);
                    continue;
                }

                var posKnown = entry.Channel.TryGetPosition(out var positionMs, TimeUnit.Ms) == Result.Ok;
                if (posKnown && entry.EndMs > 0 && positionMs >= entry.EndMs)
                {
                    entry.Channel.TryStop();
                    EmitFinished(id, entry.ClipName, "ended");
                    (finished ??= []).Add(id);
                    continue;
                }

                entry.PositionMs = posKnown ? positionMs : entry.PositionMs;
            }

            if (finished is not null)
                foreach (var id in finished)
                    _channels.Remove(id);
        }

        ReleaseEvictedSounds();
        PublishStatus();
    }

    /// <summary>Stops everything and frees every FMOD sound + the clip store (mod unload).</summary>
    internal void Shutdown()
    {
        foreach (var entry in _channels.Values)
            entry.Channel.TryStop();
        _channels.Clear();
        foreach (var sound in _sounds.Values)
            sound.TryRelease();
        _sounds.Clear();
        store.Clear();
        store.PublishChannels([]);
        _publishedEmpty = true;
    }

    /// <summary>
    ///     Releases cached sounds whose clip version is no longer current in the store (deleted or
    ///     re-uploaded) and which no live channel still plays. FMOD holds its own copy of the bytes,
    ///     so this — not the store eviction — is where the memory actually returns.
    /// </summary>
    private void ReleaseEvictedSounds()
    {
        if (_sounds.Count == 0)
            return;
        List<(string Name, int Version)>? evict = null;
        foreach (var key in _sounds.Keys)
        {
            if (store.CurrentVersion(key.Name) == key.Version)
                continue;
            var inUse = false;
            foreach (var entry in _channels.Values)
                if (entry.ClipName == key.Name && entry.Version == key.Version)
                {
                    inUse = true;
                    break;
                }

            if (!inUse)
                (evict ??= []).Add(key);
        }

        if (evict is null)
            return;
        foreach (var key in evict)
        {
            if (_sounds.Remove(key, out var sound) && sound.TryRelease() != Result.Ok)
                ModLog.Log.Debug($"audio: releasing the sound for '{key.Name}' v{key.Version} failed");
        }
    }

    private void PublishStatus()
    {
        if (_channels.Count == 0)
        {
            if (_publishedEmpty)
                return;
            store.PublishChannels([]);
            _publishedEmpty = true;
            return;
        }

        var rows = new AudioChannelStatus[_channels.Count];
        var i = 0;
        foreach (var (id, entry) in _channels)
            rows[i++] = new AudioChannelStatus(id, entry.ClipName, entry.Paused,
                entry.PositionMs - entry.PositionMs % PositionQuantumMs, entry.LengthMs,
                entry.Volume, entry.Loop, entry.Group);
        Array.Sort(rows, (a, b) => string.CompareOrdinal(a.Id, b.Id));
        store.PublishChannels(rows);
        _publishedEmpty = false;
    }

    private void EmitFinished(string id, string clipName, string reason)
        => store.EmitEvent(new SimEvent(SafeUt(), "audio.finished", null, $"{id} {clipName} {reason}"));

    private string AutoId()
    {
        // '#' is reserved for auto ids, so only a counter wrap could ever collide; probe past it.
        string id;
        do
        {
            id = "#" + (++_autoId).ToString(CultureInfo.InvariantCulture);
        } while (_channels.ContainsKey(id));

        return id;
    }

    private static ChannelGroupType GroupType(int ordinal) => ordinal switch
    {
        1 => ChannelGroupType.Music,
        2 => ChannelGroupType.Ui,
        _ => ChannelGroupType.Sfx,
    };

    private static double SafeUt()
    {
        try
        {
            return Universe.GetElapsedSimTime().Seconds();
        }
        catch
        {
            return 0;
        }
    }

    private sealed class ChannelEntry
    {
        internal Channel Channel;
        internal required string ClipName;
        internal int Version;
        internal uint EndMs;
        internal bool Loop;
        internal bool Paused;
        internal double Volume;
        internal uint LengthMs;
        internal long PositionMs;
        internal required string Group;
    }
}
