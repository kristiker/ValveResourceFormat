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

    /// <summary>Button spawnflag: fire without physically moving. Common on flush wall panels.</summary>
    private const int SF_BUTTON_DONTMOVE = 1;

    /// <summary>Button spawnflag: stay pressed until used again, rather than returning after <c>wait</c>.</summary>
    private const int SF_BUTTON_TOGGLE = 32;

    /// <summary>Button spawnflag: starts locked, so use bounces off until something unlocks it.</summary>
    private const int SF_BUTTON_LOCKED = 2048;

    /// <summary>Door spawnflag: the player cannot open this door by using it.</summary>
    private const int SF_DOOR_NO_USE = 256;

    private Vector3 travel;

    /// <summary>Gets which class this mover stands in for.</summary>
    public required MoverKind Kind { get; init; }

    /// <inheritdoc/>
    public override bool IsUsable => Kind switch
    {
        MoverKind.Button => true,
        MoverKind.Door => !HasSpawnFlag(SF_DOOR_NO_USE),
        _ => false,
    };

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

        // A "don't move" button still runs the whole press cycle and fires its outputs; it just
        // has nowhere to travel, which is how flush wall panels are built
        if (Kind == MoverKind.Button && HasSpawnFlag(SF_BUTTON_DONTMOVE))
        {
            distance = 0f;
        }

        Locked = Kind == MoverKind.Button && HasSpawnFlag(SF_BUTTON_LOCKED);

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

    /// <summary>
    /// Runs a use press, following the engine's rules for what a second press does.
    /// </summary>
    /// <param name="activator">Whoever pressed it, propagated to the outputs.</param>
    private void Press(EntityInstance? activator)
    {
        if (Locked)
        {
            SendOutput("OnUseLocked", activator);
            return;
        }

        // A press landing mid-stroke is swallowed rather than reversing the move
        if (IsMoving)
        {
            return;
        }

        if (Kind == MoverKind.Door)
        {
            MoveTo(TargetPosition > 0f ? 0f : 1f);
            return;
        }

        if (Position <= 0f)
        {
            SendOutput("OnPressed", activator);
            MoveTo(1f);
            return;
        }

        // Already pressed. A toggle button releases on the second press; anything else is either
        // returning on its own wait or, at wait -1, a one-shot that stays down for good.
        if (HasSpawnFlag(SF_BUTTON_TOGGLE))
        {
            MoveTo(0f);
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
            Press(activator);
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
