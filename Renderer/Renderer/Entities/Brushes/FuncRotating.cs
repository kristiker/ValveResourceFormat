namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_rotating</c>. Turns continuously about one axis rather than between two poses, spinning
/// up to and down from <c>maxspeed</c> at a rate set by <c>fanfriction</c>.
/// </summary>
public sealed class FuncRotating : BrushEntity
{
    /// <summary>Spawnflag: start spinning at map load.</summary>
    private const int SF_ROTATING_START_ON = 1;

    /// <summary>Spawnflag: spin the opposite way.</summary>
    private const int SF_ROTATING_BACKWARDS = 2;

    /// <summary>Spawnflag: turn about the entity's roll axis rather than its yaw axis.</summary>
    private const int SF_ROTATING_ROLL_AXIS = 4;

    /// <summary>Spawnflag: turn about the entity's pitch axis rather than its yaw axis.</summary>
    private const int SF_ROTATING_PITCH_AXIS = 8;

    private Vector3 axis;
    private float maxSpeed;
    private float rampRate;
    private float angle;

    /// <summary>Gets the current spin rate in degrees per second, signed by direction.</summary>
    public float CurrentSpeed { get; private set; }

    /// <summary>Gets the spin rate the entity is heading toward.</summary>
    public float TargetSpeed { get; private set; }

    /// <inheritdoc/>
    public override bool WantsThink => true;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        axis = HasSpawnFlag(SF_ROTATING_ROLL_AXIS)
            ? new Vector3(0, 0, 1)
            : HasSpawnFlag(SF_ROTATING_PITCH_AXIS)
                ? new Vector3(1, 0, 0)
                : new Vector3(0, 1, 0);

        maxSpeed = MathF.Abs(GetFloat("maxspeed", 100f));

        if (HasSpawnFlag(SF_ROTATING_BACKWARDS))
        {
            maxSpeed = -maxSpeed;
        }

        // fanfriction is a percentage; it sets how long spin-up and spin-down take
        var friction = Math.Clamp(GetFloat("fanfriction", 20f), 1f, 100f) / 100f;
        rampRate = MathF.Abs(maxSpeed) * friction;

        if (HasSpawnFlag(SF_ROTATING_START_ON))
        {
            TargetSpeed = maxSpeed;
            CurrentSpeed = maxSpeed;
        }
    }

    /// <inheritdoc/>
    public override void Think(float deltaTime)
    {
        if (CurrentSpeed != TargetSpeed)
        {
            var step = rampRate * deltaTime;
            var remaining = TargetSpeed - CurrentSpeed;

            CurrentSpeed = MathF.Abs(remaining) <= step ? TargetSpeed : CurrentSpeed + MathF.CopySign(step, remaining);
        }

        if (CurrentSpeed == 0f)
        {
            return;
        }

        angle = (angle + (CurrentSpeed * deltaTime)) % 360f;

        SetTransform(BuildTransform(LocalOrigin, LocalAngles + (axis * angle)));
        MarkMoved();
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Start"))
        {
            TargetSpeed = maxSpeed;
            return true;
        }

        if (InputIs(input, "Stop"))
        {
            TargetSpeed = 0f;
            return true;
        }

        if (InputIs(input, "StopAtStartPos"))
        {
            TargetSpeed = 0f;
            return true;
        }

        if (InputIs(input, "Toggle"))
        {
            TargetSpeed = TargetSpeed == 0f ? maxSpeed : 0f;
            return true;
        }

        if (InputIs(input, "Reverse"))
        {
            maxSpeed = -maxSpeed;
            TargetSpeed = TargetSpeed == 0f ? 0f : maxSpeed;
            return true;
        }

        if (InputIs(input, "SetSpeed")
            && float.TryParse(parameter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed))
        {
            TargetSpeed = MathF.CopySign(speed, maxSpeed);
            return true;
        }

        return !InputIs(input, "Toggle") && base.AcceptInput(input, parameter, activator, caller);
    }
}
