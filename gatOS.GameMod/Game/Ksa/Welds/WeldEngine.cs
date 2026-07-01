using Brutal.Numerics;
using gatOS.GameMod.Game.Ksa;
using KSA;

namespace gatOS.GameMod.Game.Ksa.Welds;

/// <summary>
///     The stateless weld math, ported verbatim from the sibling <c>unscience</c> mod's
///     <c>WeldEngine</c> (the unscience instance-extension calls <c>.NormalizedOrZero()</c>/
///     <c>.Transform()</c> are rewritten to the KSA static forms <see cref="doubleQuat.NormalizeOrZero"/>
///     / <see cref="double3.Transform"/>, which gatOS already uses elsewhere).
/// </summary>
internal static class WeldEngine
{
    /// <summary>
    ///     Teleports the source vessel to track the anchor. Returns false when the weld should be
    ///     dropped (parent-body mismatch). All KSA calls are anchored for the churn playbook.
    /// </summary>
    [KsaAnchor("Vehicle.{GetPositionCci,GetVelocityCci,GetBody2Cci,BodyRates,CenterOfMassAsmb,Parent,Orbit,"
            + "Teleport,UpdatePerFrameData}; Orbit.OrbitLineColor; IParentBody.GetCci2Cce; "
            + "Orbit.CreateFromStateCci(IParentBody,SimTime,double3,double3,byte4); "
            + "Universe.GetJobSimStep(double).NextTime; Program.GetPlayerDeltaTime; "
            + "Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}",
        SourceFile = "KSA/Vehicle.cs / KSA/Orbit.cs / KSA/Universe.cs / KSA/Part.cs", Verified = "2026-06-28",
        GameVersion = "2026.6.9.4750", Risk = ChurnRisk.High,
        Notes = "The welds per-tick teleport. Stamps the orbit with the NEXT sim-step time (not "
            + "GetElapsedSimTime) so the source body time aligns with the queued solver tick.")]
    public static bool UpdateWeld(WeldEntry entry)
    {
        var source = entry.Source;
        var target = entry.Target;
        if (source.Parent != target.Parent)
            return false; // parent body mismatch — drop the weld

        if (!entry.Enabled)
            return true;

        double3 tgtPosCci = target.GetPositionCci();
        double3 tgtVelCci = target.GetVelocityCci();
        doubleQuat tgtBody2Cci = target.GetBody2Cci();

        // Guard against NaN target state — the target vehicle may be mid-physics-blowup.
        if (!IsFinite(tgtPosCci) || !IsFinite(tgtVelCci))
            return true;

        tgtBody2Cci = doubleQuat.NormalizeOrZero(tgtBody2Cci);
        if (tgtBody2Cci == default)
            return true;

        // Anchor to the live part pose when set (tracks robotics/fuel-burn CoM drift), else the CoM.
        double3 anchorPosCci;
        doubleQuat anchorBody2Cci;
        if (entry.TargetPart is { } part)
        {
            double3 partOffset = part.PositionVehicleAsmb - target.CenterOfMassAsmb;
            anchorPosCci = tgtPosCci + double3.Transform(partOffset, tgtBody2Cci);
            anchorBody2Cci = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(part.Asmb2VehicleAsmb, tgtBody2Cci));
            if (anchorBody2Cci == default)
                anchorBody2Cci = tgtBody2Cci;
        }
        else
        {
            anchorPosCci = tgtPosCci;
            anchorBody2Cci = tgtBody2Cci;
        }

        double3 newSrcPosCci = anchorPosCci + double3.Transform(entry.Position, anchorBody2Cci);
        double3 newSrcVelCci = tgtVelCci;

        doubleQuat cci2Cce = source.Parent.GetCci2Cce();
        doubleQuat newSrcBody2Cce;
        double3 newBodyRates;
        if (entry.LockRotation)
        {
            doubleQuat newSrcBody2Cci = doubleQuat.Concatenate(entry.Orientation, anchorBody2Cci);
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(newSrcBody2Cci, cci2Cce));
            newBodyRates = target.BodyRates;
        }
        else
        {
            doubleQuat srcBody2Cci = doubleQuat.NormalizeOrZero(source.GetBody2Cci());
            newSrcBody2Cce = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(srcBody2Cci, cci2Cce));
            newBodyRates = source.BodyRates;
            if (!IsFinite(newBodyRates))
                newBodyRates = new double3(0, 0, 0);
        }

        // Stamp the orbit with the time the just-completed worker tick advanced to, NOT
        // GetElapsedSimTime() (the PREVIOUS tick's end). Teleport sets body.Time = orbit StateTime;
        // using the next-tick time keeps the source aligned with the queued solver origin (see the
        // unscience notes) — otherwise the worker logs a "SnapToLeader body/origin time" mismatch.
        SimTime tickEndTime = Universe.GetJobSimStep(Program.GetPlayerDeltaTime()).NextTime;
        Orbit newOrbit = Orbit.CreateFromStateCci(
            source.Parent, tickEndTime, newSrcPosCci, newSrcVelCci, source.Orbit.OrbitLineColor);
        source.Teleport(newOrbit, newSrcBody2Cce, newBodyRates);
        source.UpdatePerFrameData();
        return true;
    }

    /// <summary>
    ///     Captures the source's CURRENT pose relative to the anchor (the inverse of the update math),
    ///     for <c>weld_here</c>. Returns the anchor-frame position offset, the authoritative orientation
    ///     quaternion, and its Euler form (display only).
    /// </summary>
    [KsaAnchor("Vehicle.{GetPositionCci,GetBody2Cci,CenterOfMassAsmb}; Part.{PositionVehicleAsmb,Asmb2VehicleAsmb}",
        SourceFile = "KSA/Vehicle.cs / KSA/Part.cs", Verified = "2026-06-28", GameVersion = "2026.6.9.4750",
        Risk = ChurnRisk.Medium, Notes = "weld_here capture — inverse of UpdateWeld's anchor transform.")]
    public static (double3 Position, doubleQuat Orientation, double3 RotationDeg) CapturePose(
        Vehicle source, Vehicle target, Part? anchorPart)
    {
        doubleQuat tgtBody2Cci = doubleQuat.NormalizeOrZero(target.GetBody2Cci());
        double3 anchorPosCci;
        doubleQuat anchorBody2Cci;
        if (anchorPart is { } part)
        {
            double3 partOffset = part.PositionVehicleAsmb - target.CenterOfMassAsmb;
            anchorPosCci = target.GetPositionCci() + double3.Transform(partOffset, tgtBody2Cci);
            anchorBody2Cci = doubleQuat.NormalizeOrZero(doubleQuat.Concatenate(part.Asmb2VehicleAsmb, tgtBody2Cci));
            if (anchorBody2Cci == default)
                anchorBody2Cci = tgtBody2Cci;
        }
        else
        {
            anchorPosCci = target.GetPositionCci();
            anchorBody2Cci = tgtBody2Cci;
        }

        // Position: express the world delta in the anchor frame (inverse rotation = conjugate).
        double3 worldDelta = source.GetPositionCci() - anchorPosCci;
        double3 position = double3.Transform(worldDelta, doubleQuat.Conjugate(anchorBody2Cci));

        // Orientation: solve Concatenate(delta, anchor) == src  ⇒  delta = Concatenate(src, conj(anchor)).
        doubleQuat srcBody2Cci = doubleQuat.NormalizeOrZero(source.GetBody2Cci());
        doubleQuat orientation = doubleQuat.NormalizeOrZero(
            doubleQuat.Concatenate(srcBody2Cci, doubleQuat.Conjugate(anchorBody2Cci)));
        return (position, orientation, QuatToEulerDegrees(orientation));
    }

    /// <summary>Euler (degrees) → quaternion, ZYX intrinsic — verbatim from unscience (<c>doubleQuat(x,y,z,w)</c>).</summary>
    public static doubleQuat EulerDegreesToQuat(double pitchDeg, double yawDeg, double rollDeg)
    {
        double pitchRad = pitchDeg * (Math.PI / 180.0);
        double yawRad = yawDeg * (Math.PI / 180.0);
        double rollRad = rollDeg * (Math.PI / 180.0);

        double cp = Math.Cos(pitchRad / 2), sp = Math.Sin(pitchRad / 2);
        double cy = Math.Cos(yawRad / 2), sy = Math.Sin(yawRad / 2);
        double cr = Math.Cos(rollRad / 2), sr = Math.Sin(rollRad / 2);

        var qPitch = new doubleQuat(sp, 0, 0, cp);
        var qYaw = new doubleQuat(0, sy, 0, cy);
        var qRoll = new doubleQuat(0, 0, sr, cr);
        return doubleQuat.Concatenate(doubleQuat.Concatenate(qYaw, qPitch), qRoll);
    }

    /// <summary>
    ///     Best-effort inverse of <see cref="EulerDegreesToQuat"/> (the composition is
    ///     <c>R = Rz(roll)·Rx(pitch)·Ry(yaw)</c>), for the <c>/sim</c> read-back of a captured weld. The
    ///     stored quaternion is authoritative; this is display only, so a gimbal-lock edge case is harmless.
    /// </summary>
    public static double3 QuatToEulerDegrees(doubleQuat q)
    {
        double x = q.X, y = q.Y, z = q.Z, w = q.W;
        double pitch = Math.Asin(Math.Clamp(2.0 * (y * z + w * x), -1.0, 1.0));
        double yaw = Math.Atan2(2.0 * (w * y - x * z), 1.0 - 2.0 * (x * x + y * y));
        double roll = Math.Atan2(2.0 * (w * z - x * y), 1.0 - 2.0 * (x * x + z * z));
        const double radToDeg = 180.0 / Math.PI;
        return new double3(pitch * radToDeg, yaw * radToDeg, roll * radToDeg);
    }

    /// <summary>
    ///     Orders welds so a target is processed before anything anchored to it (chained welds). Returns
    ///     the input order unchanged if a cycle is detected. Ported from unscience.
    /// </summary>
    public static List<WeldEntry> TopologicalSort(List<WeldEntry> welds)
    {
        var inDegree = new Dictionary<WeldEntry, int>();
        var adj = new Dictionary<WeldEntry, List<WeldEntry>>();
        foreach (var w in welds)
        {
            inDegree[w] = 0;
            adj[w] = [];
        }

        foreach (var x in welds)
        foreach (var y in welds)
            if (ReferenceEquals(x.Source, y.Target))
            {
                adj[x].Add(y);
                inDegree[y]++;
            }

        var queue = new Queue<WeldEntry>();
        foreach (var w in welds)
            if (inDegree[w] == 0)
                queue.Enqueue(w);

        var sorted = new List<WeldEntry>(welds.Count);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            sorted.Add(current);
            foreach (var neighbor in adj[current])
                if (--inDegree[neighbor] == 0)
                    queue.Enqueue(neighbor);
        }

        return sorted.Count == welds.Count ? sorted : [..welds];
    }

    private static bool IsFinite(double3 v) => double.IsFinite(v.X) && double.IsFinite(v.Y) && double.IsFinite(v.Z);
}
