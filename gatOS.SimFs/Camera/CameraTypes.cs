namespace gatOS.SimFs.Camera;

/// <summary>
///     The reference frame a camera position, aim offset or velocity is expressed in
///     (plans/CAMERA_CONTROLS_PLAN.md §4.2). Everything the camera surface does reduces to
///     "express a point in a chosen frame and let the host re-resolve it every frame" — which is
///     why the frame travels with the value rather than being baked into it.
/// </summary>
/// <remarks>
///     The distinction that matters most is <see cref="Cce"/> vs <see cref="BodyFixed"/>: an
///     inertial offset from an anchor does <b>not</b> spin with it, a body-fixed one does. "Hold this
///     spot 30 m above the ocean and let the world turn under the shot" is a <i>static</i>
///     <see cref="BodyFixed"/> position — not a keyframed curve chasing a planet's rotation.
/// </remarks>
public enum FrameKind
{
    /// <summary>Absolute ecliptic metres. The identity frame; no anchor needed.</summary>
    Ecl,

    /// <summary>Inertial (ECL-axed) offset from the anchor — does not rotate with it.</summary>
    Cce,

    /// <summary>The anchor's rotating body-fixed frame — rides a planet's spin, or bolts to a hull.</summary>
    BodyFixed,

    /// <summary>The local horizon at the anchor: east / north / up.</summary>
    Enu,

    /// <summary>The anchor's orbital frame: prograde / radial / normal.</summary>
    Lvlh,

    /// <summary>The anchor vessel's body frame in the game's chase-camera convention.</summary>
    Chase,
}

/// <summary>
///     What the camera's "up" vector is taken from once its forward direction is fixed by the aim
///     channel (plans/CAMERA_CONTROLS_PLAN.md §4.1).
/// </summary>
public enum AimUpKind
{
    /// <summary>World up — the ecliptic +Z axis. The stable, horizon-locked default.</summary>
    World,

    /// <summary>The aim target's own up axis, so the shot rolls with the subject.</summary>
    Target,

    /// <summary>The anchor's velocity direction — the "along the track" convention.</summary>
    Velocity,

    /// <summary>No up constraint: roll is whatever <c>pose/roll</c> alone says.</summary>
    Free,
}

/// <summary>
///     Which of the game's camera modes gatOS parks the viewport in
///     (plans/CAMERA_CONTROLS_PLAN.md §4.1). <see cref="Fixed"/> with nothing followed is the
///     ownership park: the game's own camera solver then produces nothing and gatOS is the only writer.
/// </summary>
public enum CameraModeKind
{
    /// <summary>The stock orbit-around-the-target camera.</summary>
    Orbit,

    /// <summary>The stock free-fly camera.</summary>
    Free,

    /// <summary>Map view (a second camera instance — see the plan's hazard §11.1).</summary>
    Map,

    /// <summary>Interior (IVA) view.</summary>
    Iva,

    /// <summary>The fixed camera — the mode gatOS parks in while it owns the viewport.</summary>
    Fixed,
}

/// <summary>What kind of thing a <see cref="TargetRef"/> names.</summary>
public enum TargetKind
{
    /// <summary>Nothing — spelled <c>none</c>. Clears a follow/aim/anchor slot.</summary>
    None,

    /// <summary>A vehicle, spelled <c>vessel:&lt;id&gt;</c>. Kittenauts are vehicles too.</summary>
    Vessel,

    /// <summary>A celestial, spelled <c>body:&lt;id&gt;</c>.</summary>
    Body,

    /// <summary>One part of a vehicle, spelled <c>part:&lt;vessel-id&gt;/&lt;instance-id&gt;</c>.</summary>
    Part,
}

/// <summary>
///     A resolved reference to something the camera can follow, anchor to, or aim at — the one
///     addressing vocabulary shared by <c>camera/follow</c>, <c>pose/anchor</c>,
///     <c>pose/aim_target</c> and the <c>body:</c> tail of <c>pose/geo</c>
///     (plans/CAMERA_CONTROLS_PLAN.md §4.2).
/// </summary>
/// <remarks>
///     <para>
///         The wire spelling is the <i>only</i> spelling: <c>vessel:&lt;id&gt;</c> |
///         <c>body:&lt;id&gt;</c> | <c>part:&lt;vessel-id&gt;/&lt;instance-id&gt;</c> | <c>none</c>.
///         <see cref="TryParse"/> and <see cref="ToString"/> round-trip exactly, which is what lets a
///         read-back leaf be fed straight back into the control that produced it — the resync-after-restart
///         property AGENTS.md §7 requires.
///     </para>
///     <para>
///         This type is game-free: it validates <i>shape</i>, never existence. A reference to a vessel
///         that despawned parses fine and resolves to nothing at the game seam, which is where the
///         ENOENT belongs.
///     </para>
/// </remarks>
/// <param name="Kind">What sort of thing is named.</param>
/// <param name="Id">
///     The vessel or body id (empty for <see cref="TargetKind.None"/>). For a part this is the
///     <i>vessel</i> id; the part is named by <paramref name="PartInstanceId"/>.
/// </param>
/// <param name="PartInstanceId">
///     The part's <c>instance_id</c> (the same identity the welds anchor picker uses), or empty for
///     every non-part kind.
/// </param>
public readonly record struct TargetRef(TargetKind Kind, string Id, string PartInstanceId)
{
    /// <summary>The <c>none</c> reference — an empty follow/aim/anchor slot.</summary>
    public static TargetRef None => new(TargetKind.None, "", "");

    /// <summary>A vessel reference (<c>vessel:&lt;id&gt;</c>).</summary>
    public static TargetRef Vessel(string id) => new(TargetKind.Vessel, id, "");

    /// <summary>A celestial reference (<c>body:&lt;id&gt;</c>).</summary>
    public static TargetRef Body(string id) => new(TargetKind.Body, id, "");

    /// <summary>A part reference (<c>part:&lt;vessel-id&gt;/&lt;instance-id&gt;</c>).</summary>
    public static TargetRef Part(string vesselId, string instanceId)
        => new(TargetKind.Part, vesselId, instanceId);

    /// <summary>Whether this reference names anything at all.</summary>
    public bool HasTarget => Kind != TargetKind.None;

    /// <summary>
    ///     Parses the wire spelling. Ids must obey the <c>/sim</c> sanitized-id charset (SPEC §2.2 —
    ///     see <see cref="CameraRules.IsValidId"/>), so a reference can never smuggle a path separator
    ///     or whitespace into a <c>/sim</c> path or a space-separated read-back line.
    /// </summary>
    /// <param name="token">The token to parse (case-sensitive ids; the <c>kind:</c> prefix is not).</param>
    /// <param name="target">The parsed reference, or <see cref="None"/> on failure.</param>
    /// <returns>True when <paramref name="token"/> is a well-formed reference.</returns>
    public static bool TryParse(string? token, out TargetRef target)
    {
        target = None;
        if (string.IsNullOrEmpty(token))
            return false;
        if (token.Equals("none", StringComparison.OrdinalIgnoreCase))
            return true;

        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;
        var kind = token[..colon];
        var rest = token[(colon + 1)..];

        if (kind.Equals("vessel", StringComparison.OrdinalIgnoreCase))
        {
            if (!CameraRules.IsValidId(rest))
                return false;
            target = Vessel(rest);
            return true;
        }

        if (kind.Equals("body", StringComparison.OrdinalIgnoreCase))
        {
            if (!CameraRules.IsValidId(rest))
                return false;
            target = Body(rest);
            return true;
        }

        if (!kind.Equals("part", StringComparison.OrdinalIgnoreCase))
            return false;

        // part:<vessel-id>/<instance-id> — split on the LAST '/', because a sanitized vessel id can
        // never contain one, but being explicit here keeps the grammar unambiguous forever.
        var slash = rest.LastIndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
            return false;
        var vessel = rest[..slash];
        var instance = rest[(slash + 1)..];
        if (!CameraRules.IsValidId(vessel) || !CameraRules.IsValidId(instance))
            return false;
        target = Part(vessel, instance);
        return true;
    }

    /// <summary>
    ///     The canonical wire spelling — exactly what <see cref="TryParse"/> accepts, so read-back
    ///     round-trips.
    /// </summary>
    public override string ToString()
        => Kind switch
        {
            TargetKind.Vessel => "vessel:" + Id,
            TargetKind.Body => "body:" + Id,
            TargetKind.Part => "part:" + Id + "/" + PartInstanceId,
            _ => "none",
        };
}
