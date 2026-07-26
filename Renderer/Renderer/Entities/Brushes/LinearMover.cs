namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// A brush that slides along a fixed direction: <c>func_door</c>, <c>func_movelinear</c> and
/// <c>func_button</c>. Doors and buttons derive their stroke from their own size along
/// <c>movedir</c> less the <c>lip</c>, so they just clear their frame; a movelinear takes
/// <c>movedistance</c> outright.
/// </summary>
public sealed class LinearMover : MoverEntity
{
    /// <summary>Which class this mover is standing in for, which decides how its stroke and inputs work.</summary>
    public enum MoverKind
    {
        /// <summary><c>func_door</c>: stroke from geometry, optional auto-close.</summary>
        Door,

        /// <summary><c>func_movelinear</c>: stroke from <c>movedistance</c>.</summary>
        MoveLinear,

        /// <summary><c>func_button</c>: stroke from geometry, pressed rather than opened.</summary>
        Button,
    }

    private Vector3 travel;

    /// <summary>Gets which class this mover stands in for.</summary>
    public required MoverKind Kind { get; init; }

    /// <summary>Gets a value indicating whether the stroke comes from the brush's own size.</summary>
    private bool StrokeFromGeometry => Kind is MoverKind.Door or MoverKind.Button;

    /// <summary>Gets or sets a value indicating whether the entity refuses to be pressed or opened.</summary>
    public bool Locked { get; set; }

    /// <summary>Gets the full stroke as a world-space offset from the closed pose.</summary>
    public Vector3 Travel => travel;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        var direction = GetWorldDirection("movedir", Vector3.UnitZ);
        var distance = StrokeFromGeometry ? DoorTravelDistance(direction) : GetFloat("movedistance");

        travel = direction * distance;

        var speed = GetFloat("speed", 100f);
        MoveRate = distance > 0f && speed > 0f ? speed / distance : 0f;

        // A door records its open/closed start as a flag, a movelinear as a fraction
        var start = Kind == MoverKind.MoveLinear
            ? GetFloat("startposition")
            : GetInt("spawnpos") != 0 ? 1f : 0f;

        SnapTo(start);
    }

    /// <summary>
    /// The stroke the engine gives a door: its own extent along the move direction, less the lip it
    /// leaves poking out of the frame.
    /// </summary>
    private float DoorTravelDistance(Vector3 direction)
    {
        var size = LocalSize;

        var extent = MathF.Abs(direction.X * size.X)
            + MathF.Abs(direction.Y * size.Y)
            + MathF.Abs(direction.Z * size.Z);

        return MathF.Max(0f, extent - GetFloat("lip"));
    }

    /// <inheritdoc/>
    protected override Matrix4x4 PoseAt(float position)
        => BuildTransform(LocalOrigin, LocalAngles) * Matrix4x4.CreateTranslation(travel * position);

    /// <inheritdoc/>
    protected override void OnArrived(float position)
    {
        base.OnArrived(position);

        if (Kind == MoverKind.Button)
        {
            SendOutput(position >= 1f ? "OnIn" : "OnOut", null);
        }

        if (Kind == MoverKind.MoveLinear || position < 1f)
        {
            return;
        }

        // A door or button with a non-negative wait returns by itself; -1 means it stays put
        var wait = GetFloat("wait", -1f);

        if (wait >= 0f)
        {
            World.QueueInputOn(this, "Close", delay: wait);
        }
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Lock"))
        {
            Locked = true;
            return true;
        }

        if (InputIs(input, "Unlock"))
        {
            Locked = false;
            return true;
        }

        if (InputIs(input, "Press") || InputIs(input, "Use"))
        {
            if (Locked)
            {
                SendOutput("OnUseLocked", activator);
                return true;
            }

            SendOutput("OnPressed", activator);
            MoveTo(1f);
            return true;
        }

        if (Locked && (InputIs(input, "Open") || InputIs(input, "Toggle")))
        {
            SendOutput("OnUseLocked", activator);
            return true;
        }

        return base.AcceptInput(input, parameter, activator, caller);
    }
}
