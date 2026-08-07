using System.Globalization;
using gatOS.Logging;
using gatOS.NineP.Protocol;
using gatOS.NineP.Vfs;
using gatOS.SimFs.Commands;
using gatOS.SimFs.Snapshots;

namespace gatOS.SimFs.Camera;

/// <summary>
///     What the camera director asks each frame: "is anything driving the camera, and if so what".
/// </summary>
/// <remarks>
///     The one seam between the track machinery (game-free, here) and the director
///     (<c>gatOS.GameMod/Game/Ksa/Camera/</c>). It hands back exactly what
///     <see cref="CameraState.Compose"/> consumes — a pose plus the channels the active shot claims —
///     and never a game type: positions and aim targets stay frame-relative, and resolving a frame to
///     ECL is the director's job.
/// </remarks>
public interface ITrackSampler
{
    /// <summary>
    ///     Evaluates the active shot at <paramref name="tSeconds"/>.
    /// </summary>
    /// <param name="tSeconds">The timeline position to sample, seconds.</param>
    /// <param name="sample">The evaluated pose. Only the fields named by <paramref name="channels"/> are meaningful.</param>
    /// <param name="channels">Exactly the channels the active shot animates — never widened.</param>
    /// <returns>False when nothing should drive the camera this frame.</returns>
    bool TryEvaluate(double tSeconds, out CameraPose sample, out CameraChannelMask channels);
}

/// <summary>
///     One playing camera track: a <see cref="PlaybackClock"/> consumer registered in the same
///     <c>/sim/ctl/schedules/</c> registry as a timed schedule, with <c>kind = camera-track</c>
///     (plans/CAMERA_CONTROLS_PLAN.md §3.4).
/// </summary>
/// <remarks>
///     <para>
///         <b>A consumer of the one timeline primitive, not a peer with its own.</b> Building a second
///         clock would mean two notions of "now", and any drift between them shows up as a camera move
///         sliding against its own cue track. Riding <see cref="PlaybackClock"/> also means this player
///         can join a shared-clock <i>group</i> with a schedule — <c>camera/play … group take-3</c> —
///         so a pause or a scrub on either moves both, which is what makes "the dolly move, the light
///         cues and the slow-mo beat are one take" literally true.
///     </para>
///     <para>
///         <b>It fires no commands</b>, so <see cref="PendingCount"/> and <see cref="Dropped"/> are
///         always 0 and <see cref="LastError"/> is always <c>-</c>: a track produces a pose the director
///         samples, not queued entries. It also never reports
///         <see cref="PlaybackState.Failed"/> — a malformed track is rejected at upload and again at
///         <c>play</c>, so there is no failure mode left to report mid-take. That keeps
///         <c>ScheduleStore.IsFinished</c> honest for this kind: it reduces to
///         <see cref="PlaybackState.Done"/>, which is reached only by an explicit stop or by running
///         past the end, so cap-pressure eviction can never take a player mid-shot.
///     </para>
///     <para>
///         <b>Completing is a hold; stopping is a release.</b> A non-looping track that reaches its end
///         keeps returning its final sample, so the shot does not snap away the instant it lands;
///         <c>camera/stop</c> is what hands the channels back to the live overrides and the baseline.
///     </para>
/// </remarks>
public sealed class CameraPlayback : IPlaybackPlayer, ITrackSampler
{
    /// <summary>The <c>kind</c> leaf value of a camera-track player.</summary>
    public const string CameraTrackKind = "camera-track";

    private readonly CameraStore _camera;
    private readonly ScheduleStore _schedules;

    private volatile bool _stopped;
    private volatile bool _finished;
    private int _lastShotIndex = -1;
    private int _lastLoopCount;

    /// <param name="id">The registry id (already reserved with <c>ScheduleStore.ReserveId</c>).</param>
    /// <param name="group">The shared-clock group name, or <c>""</c>.</param>
    /// <param name="clock">The timeline — this player's own, or the group's shared instance.</param>
    /// <param name="ownsClock">Whether this player must advance <paramref name="clock"/> itself.</param>
    /// <param name="trackName">The <c>/sim/camera/track/</c> entry name, for status and events.</param>
    /// <param name="track">The parsed track.</param>
    /// <param name="camera">The store the <c>camera.*</c> events are emitted into.</param>
    /// <param name="schedules">The registry, for the shared UT stamp.</param>
    public CameraPlayback(
        string id, string group, PlaybackClock clock, bool ownsClock,
        string trackName, Track track, CameraStore camera, ScheduleStore schedules)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(schedules);

        Id = id;
        Group = group;
        Clock = clock;
        OwnsClock = ownsClock;
        TrackName = trackName;
        Track = track;
        _camera = camera;
        _schedules = schedules;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string Kind => CameraTrackKind;

    /// <inheritdoc />
    public string Group { get; }

    /// <inheritdoc />
    public PlaybackClock Clock { get; }

    /// <inheritdoc />
    public bool OwnsClock { get; }

    /// <summary>The name of the track being played (the <c>camera/play</c> read-back).</summary>
    public string TrackName { get; }

    /// <summary>The parsed track this player is driving.</summary>
    public Track Track { get; }

    /// <inheritdoc />
    public double DurationMs => Track.DurationMs;

    /// <inheritdoc />
    /// <remarks>
    ///     Computed, never latched from a tick, so it stays truthful even when the director is not
    ///     sampling (the camera is not owned, the game is paused). The event edges in
    ///     <see cref="Poll"/> are the only thing that needs a call.
    /// </remarks>
    public PlaybackState State
        => _stopped ? PlaybackState.Done
            : !Clock.Started ? PlaybackState.Pending
            : !Clock.Loop && Clock.PositionMs >= DurationMs ? PlaybackState.Done
            : Clock.Paused ? PlaybackState.Paused
            : PlaybackState.Running;

    /// <inheritdoc />
    public int PendingCount => 0;

    /// <inheritdoc />
    public long Dropped => 0;

    /// <inheritdoc />
    public string LastError => "-";

    /// <summary>The index of the shot last reported by <c>camera.shot</c>, or -1 before the first.</summary>
    public int ShotIndex => Volatile.Read(ref _lastShotIndex);

    /// <inheritdoc />
    public void Stop() => _stopped = true;

    /// <inheritdoc />
    public bool TryEvaluate(double tSeconds, out CameraPose sample, out CameraChannelMask channels)
    {
        if (_stopped)
        {
            sample = CameraPose.Default;
            channels = CameraChannelMask.None;
            return false;
        }

        var evaluated = TrackEvaluator.Sample(Track, tSeconds);
        Poll(evaluated);
        sample = evaluated.Pose;
        channels = evaluated.Channels;
        return channels != CameraChannelMask.None;
    }

    /// <summary>
    ///     The form the director should actually call: samples at the player's <i>own</i> clock, so
    ///     there is no second notion of "now" to drift against the registry's.
    /// </summary>
    /// <param name="sample">The evaluated pose.</param>
    /// <param name="channels">Exactly the channels the active shot animates.</param>
    /// <returns>False when nothing should drive the camera this frame.</returns>
    public bool TryEvaluateNow(out CameraPose sample, out CameraChannelMask channels)
        => TryEvaluate(Clock.PositionMs / 1000.0, out sample, out channels);

    /// <summary>
    ///     Game thread: emits the <c>camera.shot</c> / <c>camera.finished</c> edges implied by
    ///     <paramref name="evaluated"/>. Called from <see cref="TryEvaluate"/>; a driver that wants the
    ///     events even while the camera is not being sampled may call it with
    ///     <c>TrackEvaluator.Sample(player.Track, player.Clock.PositionMs / 1000)</c>.
    /// </summary>
    /// <param name="evaluated">This frame's sample.</param>
    public void Poll(in CameraSample evaluated)
    {
        // A wrap re-arms the shot edge, so a looping take announces shot 0 again each cycle rather than
        // falling silent after the first pass.
        var loops = Clock.LoopCount;
        if (loops != _lastLoopCount)
        {
            _lastLoopCount = loops;
            _lastShotIndex = -1;
            _finished = false;
        }

        if (evaluated.ShotIndex != _lastShotIndex)
        {
            _lastShotIndex = evaluated.ShotIndex;
            Emit("camera.shot",
                $"{Id} track={TrackName} shot={evaluated.ShotIndex.ToString(CultureInfo.InvariantCulture)} "
                + $"name={evaluated.ShotName}");
        }

        if (_finished || Clock.Loop || Clock.PositionMs < DurationMs)
            return;
        _finished = true;
        Emit("camera.finished", $"{Id} track={TrackName} kind={CameraTrackKind} reason=complete");
    }

    /// <summary>Game thread: emits the terminal <c>camera.finished</c> for a stop/replace.</summary>
    /// <param name="reason">The documented reason token (<c>stopped</c> or <c>replaced</c>).</param>
    internal void Finish(string reason)
    {
        if (_finished)
            return;
        _finished = true;
        Emit("camera.finished", $"{Id} track={TrackName} kind={CameraTrackKind} reason={reason}");
    }

    private void Emit(string type, string detail)
        => _camera.EmitEvent(new SimEvent(_schedules.UtSeconds, type, null, detail));
}

/// <summary>
///     Owns the one live camera-track player and executes the <c>camera.play</c> / <c>camera.set</c> /
///     <c>camera.stop</c> family — the camera-flavoured spelling of the schedule registry's verbs, and
///     deliberately a thin wrapper over the same registry entry rather than a parallel mechanism
///     (plans/CAMERA_CONTROLS_PLAN.md §3.4).
/// </summary>
/// <remarks>
///     <para>
///         It is also the <c>CameraStore.OnTrackCommitted</c> handler, promoted from a notification to a
///         <b>rejecting validator</b>: a malformed track fails the upload rather than surfacing later as
///         a mystery at <c>play</c> time. A 9p clunk cannot carry an errno, so that rejection reaches an
///         author three ways — the host log, <see cref="LastTrackError"/>, and the EINVAL (with the
///         parse message) that <c>camera/play</c> returns for the offending track. The committed bytes
///         are deliberately left in place so <c>cat</c> still shows what was written.
///     </para>
///     <para>
///         <b>Threading.</b> <see cref="Execute"/>, <see cref="TryEvaluate"/> and <see cref="Clear"/> are
///         game-thread only (the command drain and the director). Commits arrive on transport threads,
///         so the parsed-track cache is lock-guarded; everything else the transports read goes through
///         the registry's own volatile snapshot.
///     </para>
/// </remarks>
public sealed class CameraPlaybackController : ITrackSampler
{
    /// <summary>The registry id a camera track claims when it is free — predictable beats auto-numbered.</summary>
    public const string PreferredId = "camera";

    private readonly CameraStore _camera;
    private readonly ScheduleStore _schedules;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, CachedTrack> _cache = new(StringComparer.Ordinal);

    /// <param name="camera">The track store (and the <c>camera.*</c> event queue).</param>
    /// <param name="schedules">The player registry this controller's player joins.</param>
    /// <param name="hookCommits">
    ///     Whether to install this controller as <see cref="CameraStore.OnTrackCommitted"/>. Only one
    ///     handler can hold that seam; pass false when something else owns it.
    /// </param>
    public CameraPlaybackController(CameraStore camera, ScheduleStore schedules, bool hookCommits = true)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(schedules);
        _camera = camera;
        _schedules = schedules;
        if (hookCommits)
            camera.OnTrackCommitted = OnTrackCommitted;
    }

    /// <summary>The live player, or null when nothing is loaded.</summary>
    public CameraPlayback? Current { get; private set; }

    /// <summary>
    ///     The last upload or playback rejection, or <c>-</c>. The clunk that committed a bad track could
    ///     not carry an errno, so this is where that diagnosis lives until someone tries to play it.
    /// </summary>
    /// <remarks>
    ///     Backed by <see cref="CameraStore.LastError"/> — one definition, so the <c>camera/last_error</c>
    ///     leaf and this property can never disagree, and the leaf stays readable while gatOS does not
    ///     own the camera (which is when tracks are uploaded).
    /// </remarks>
    public string LastTrackError => _camera.LastError;

    /// <inheritdoc />
    public bool TryEvaluate(double tSeconds, out CameraPose sample, out CameraChannelMask channels)
    {
        if (Current is { } player)
            return player.TryEvaluate(tSeconds, out sample, out channels);
        sample = CameraPose.Default;
        channels = CameraChannelMask.None;
        return false;
    }

    /// <summary>
    ///     The form the director should call: samples at the player's own clock (see
    ///     <see cref="CameraPlayback.TryEvaluateNow"/>).
    /// </summary>
    /// <param name="sample">The evaluated pose.</param>
    /// <param name="channels">Exactly the channels the active shot animates.</param>
    /// <returns>False when nothing should drive the camera this frame.</returns>
    public bool TryEvaluateNow(out CameraPose sample, out CameraChannelMask channels)
    {
        if (Current is { } player)
            return player.TryEvaluateNow(out sample, out channels);
        sample = CameraPose.Default;
        channels = CameraChannelMask.None;
        return false;
    }

    /// <summary>
    ///     Game thread: the game-free executor for <c>camera.play</c> / <c>camera.set</c> /
    ///     <c>camera.stop</c>. The catalog routes these here rather than re-implementing them, exactly as
    ///     it routes <c>schedule.*</c> to <c>ScheduleStore.Execute</c> — nothing about playing a track
    ///     touches KSA.
    /// </summary>
    /// <param name="command">The command to apply.</param>
    public CommandResult Execute(SimCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.Action switch
        {
            CameraCommands.PlayAction => Play(command),
            CameraCommands.SetAction => Set(command),
            CameraCommands.StopAction => StopCommand(),
            _ => new CommandResult(CommandOutcome.Unsupported, $"unknown action '{command.Action}'"),
        };
    }

    /// <summary>Game thread: stops and drops the live player, if any (mod unload / camera release).</summary>
    public void Clear()
    {
        Retire("stopped");
        lock (_cacheLock)
        {
            _cache.Clear();
        }

        _camera.LastError = CameraFormat.Absent;
    }

    // ---- camera.play / set / stop -------------------------------------------------------------------

    private CommandResult Play(SimCommand command)
    {
        var name = command.Token;
        if (string.IsNullOrEmpty(name) || !CameraStore.IsValidName(name))
            return Fail(name ?? "", CommandOutcome.Invalid, "camera.play: no track name");

        switch (_camera.TryGet(name, out var raw))
        {
            case CameraTrackLookup.Missing:
                return Fail(name, CommandOutcome.NotFound, $"no camera track '{name}'");
            case CameraTrackLookup.Uploading:
                return Fail(name, CommandOutcome.Busy, $"camera track '{name}' is still uploading");
        }

        if (!TryResolve(raw!, out var track, out var error))
            return Fail(name, CommandOutcome.Invalid, error!);

        var values = command.Values;
        var group = command.Aux ?? "";
        var loop = Present(values, CameraCommands.PlayLoopPresent)
            ? Slot(values, CameraCommands.PlayLoop) != 0
            : track!.Loop;
        var rate = Present(values, CameraCommands.PlayRatePresent)
            ? Slot(values, CameraCommands.PlayRate)
            : 1.0;

        // Replacing is a stop plus a start, so the outgoing take reports why it ended.
        Retire("replaced");

        string id;
        try
        {
            id = _schedules.IsIdLive(PreferredId)
                ? _schedules.ReserveId(null)
                : _schedules.ReserveId(PreferredId);
        }
        catch (VfsErrorException ex)
        {
            return Fail(name, CommandOutcome.Invalid, ex.Message);
        }

        var clock = _schedules.ResolveGroupClock(group, ClockBase.Render, rate, loop, id);
        var ownsClock = group.Length == 0;
        clock.DurationMs = track!.DurationMs;
        if (ownsClock)
        {
            clock.Rate = rate;
            clock.Loop = loop;
            clock.Paused = false;
            clock.Scrub(Present(values, CameraCommands.PlayAtPresent)
                ? Slot(values, CameraCommands.PlayAtSeconds) * 1000.0
                : 0.0);
        }
        else if (Present(values, CameraCommands.PlayAtPresent)
                 || Present(values, CameraCommands.PlayRatePresent)
                 || Present(values, CameraCommands.PlayLoopPresent))
        {
            // The group's timeline belongs to whoever created it (the ScheduleStore rule); a joiner that
            // silently re-timed the take would be far worse than one that says it did not.
            ModLog.Log.Debug($"camera.play: '{name}' joined group '{group}'; its at/rate/loop are "
                             + "ignored in favour of the group's shared clock");
        }

        var player = new CameraPlayback(id, group, clock, ownsClock, name, track, _camera, _schedules);
        _schedules.Register(player);
        clock.Start();
        Current = player;
        // A take that actually started is the proof the track is good, so it clears the diagnosis a
        // failed upload or a failed play left behind. Nothing else clears it: an error that stayed on
        // screen until something worked is far more useful than one that quietly ages out.
        _camera.LastError = CameraFormat.Absent;
        return CommandResult.Ok;
    }

    /// <summary>
    ///     Records a play rejection in <see cref="CameraStore.LastError"/> and returns it. Every
    ///     <c>camera.play</c> failure funnels through here so <c>camera/last_error</c> explains a failed
    ///     <i>play</i> as well as a failed upload — the errno reaches only the caller, and on the
    ///     <c>ctl/timed_batch</c> path there is no caller left to read it.
    /// </summary>
    private CommandResult Fail(string name, CommandOutcome outcome, string message)
    {
        _camera.LastError = name.Length > 0 ? $"{name}: {message}" : message;
        return new CommandResult(outcome, message);
    }

    private CommandResult Set(SimCommand command)
    {
        if (Current is not { } player)
            return new CommandResult(CommandOutcome.NotFound, "camera.set: no track is playing");
        if (command.Values is not { Count: > 0 } values || values.Count % 2 != 0)
            return new CommandResult(CommandOutcome.Invalid, "camera.set takes [key, value] pairs");

        for (var i = 0; i < values.Count; i += 2)
        {
            var value = values[i + 1];
            switch ((int)values[i])
            {
                case CameraCommands.SetT:
                    if (!double.IsFinite(value) || value < 0)
                        return new CommandResult(CommandOutcome.Invalid, "camera.set t takes seconds ≥ 0");
                    player.Clock.Scrub(value * 1000.0);
                    break;

                case CameraCommands.SetRate:
                    if (!double.IsFinite(value)
                        || value < PlaybackClock.MinRate || value > PlaybackClock.MaxRate)
                        return new CommandResult(CommandOutcome.Invalid,
                            $"camera.set rate takes {PlaybackClock.MinRate}..{PlaybackClock.MaxRate}");
                    player.Clock.Rate = value;
                    break;

                case CameraCommands.SetLoop:
                    if (value is not (0 or 1))
                        return new CommandResult(CommandOutcome.Invalid, "camera.set loop takes 0 or 1");
                    player.Clock.Loop = value != 0;
                    break;

                case CameraCommands.SetPaused:
                    if (value is not (0 or 1))
                        return new CommandResult(CommandOutcome.Invalid, "camera.set paused takes 0 or 1");
                    player.Clock.Paused = value != 0;
                    break;

                default:
                    return new CommandResult(CommandOutcome.Invalid,
                        $"camera.set: unknown key {values[i].ToString(CultureInfo.InvariantCulture)}");
            }
        }

        return CommandResult.Ok;
    }

    /// <summary>
    ///     <c>camera.stop</c> is idempotent: stopping when nothing plays is the state the writer asked
    ///     for, and a trigger that fails because it had nothing to do would only make scripts defensive.
    /// </summary>
    private CommandResult StopCommand()
    {
        Retire("stopped");
        return CommandResult.Ok;
    }

    /// <summary>Ends the current take (if any), reports why, and frees its registry slot.</summary>
    private void Retire(string reason)
    {
        if (Current is not { } player)
            return;
        Current = null;
        player.Stop();
        player.Finish(reason);
        _schedules.Unregister(player.Id);
    }

    // ---- the commit-time validator + parsed-track cache ----------------------------------------------

    /// <summary>
    ///     <see cref="CameraStore.OnTrackCommitted"/>: parses and validates on commit, so a malformed
    ///     track fails the upload. Called on the committing thread, outside the store lock.
    /// </summary>
    /// <exception cref="VfsErrorException">EINVAL, with the parse diagnosis.</exception>
    private void OnTrackCommitted(CameraTrack track)
    {
        var ok = TrackParser.TryParse(track.Bytes, _camera.Limits, out var parsed, out var error);
        lock (_cacheLock)
        {
            _cache[track.Name] = new CachedTrack(track.Version, parsed, error);
        }

        if (ok)
            return;

        _camera.LastError = $"{track.Name}: {error}";
        ModLog.Log.Warn($"camera: rejected track '{track.Name}': {error}");

        // An empty commit is the ordinary shape of `truncate -s 0` and of an upload that has not
        // started yet, not an authoring error — recording it is enough. Anything else the author
        // actually wrote gets the errno, on whichever transport can carry one.
        if (track.Bytes.Length > 0)
            throw new VfsErrorException(LinuxErrno.EINVAL, error!);
    }

    /// <summary>The parsed form of a committed track, memoized by version (bytes are immutable per version).</summary>
    private bool TryResolve(CameraTrack raw, out Track? track, out string? error)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(raw.Name, out var cached) && cached.Version == raw.Version)
            {
                track = cached.Track;
                error = cached.Error;
                return track is not null;
            }
        }

        var ok = TrackParser.TryParse(raw.Bytes, _camera.Limits, out track, out error);
        lock (_cacheLock)
        {
            _cache[raw.Name] = new CachedTrack(raw.Version, track, error);
        }

        return ok;
    }

    private static bool Present(IReadOnlyList<double>? values, int slot)
        => values is not null && slot < values.Count && values[slot] != 0;

    private static double Slot(IReadOnlyList<double>? values, int slot)
        => values is not null && slot < values.Count ? values[slot] : 0.0;

    private readonly record struct CachedTrack(int Version, Track? Track, string? Error);
}
