namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// A brush that turns through a bounded sweep about one axis: <c>func_door_rotating</c> and
/// <c>momentary_rot_button</c>. Movement maps use the latter as the spinner that carries a player
/// around, wiring its end-of-sweep output back to its own start so it never stops.
/// </summary>
public sealed class RotatingMover : MoverEntity
{
    /// <summary>Spawnflag: turn about the entity's roll axis rather than its yaw axis.</summary>
    private const int SF_ROTATE_ROLL_AXIS = 64;

    /// <summary>Spawnflag: turn about the entity's pitch axis rather than its yaw axis.</summary>
    private const int SF_ROTATE_PITCH_AXIS = 128;

    private Vector3 sweep;

    /// <summary>Gets the full sweep as an offset applied to the entity's compiled angles.</summary>
    public Vector3 Sweep => sweep;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        // The engine adds the sweep to one component of the entity's angles rather than spinning
        // about a world axis, so the axis is picked in pitch/yaw/roll space
        var axis = HasSpawnFlag(SF_ROTATE_ROLL_AXIS)
            ? new Vector3(0, 0, 1)
            : HasSpawnFlag(SF_ROTATE_PITCH_AXIS)
                ? new Vector3(1, 0, 0)
                : new Vector3(0, 1, 0);

        var degrees = GetFloat("distance", 90f);

        sweep = axis * degrees;

        var speed = MathF.Abs(GetFloat("speed", 100f));
        var travel = MathF.Abs(degrees);

        MoveRate = travel > 0f && speed > 0f ? speed / travel : 0f;

        SnapTo(GetFloat("startposition"));
    }

    /// <inheritdoc/>
    protected override Matrix4x4 PoseAt(float position)
        => BuildTransform(LocalOrigin, LocalAngles + (sweep * position));
}
