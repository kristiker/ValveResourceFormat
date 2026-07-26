namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// A brush that travels between two fixed poses — a door sliding open, a platform running its
/// stroke, a button turning. Travel is tracked as a fraction from 0 (closed) to 1 (open) so sliding
/// and rotating movers share the same state machine, timing and outputs; subclasses only supply the
/// pose for a given fraction.
/// </summary>
public abstract class MoverEntity : BrushEntity
{
    /// <summary>Gets how far along its travel the mover currently is, from 0 to 1.</summary>
    public float Position { get; private set; }

    /// <summary>Gets the position the mover is travelling toward.</summary>
    public float TargetPosition { get; private set; }

    /// <summary>
    /// Gets or sets travel speed as a fraction of the full stroke per second. Zero or less makes
    /// every move instant.
    /// </summary>
    protected float MoveRate { get; set; }

    /// <summary>Gets a value indicating whether the mover is between its two poses.</summary>
    public bool IsMoving => Position != TargetPosition;

    /// <inheritdoc/>
    public override bool WantsThink => true;

    /// <summary>
    /// The world transform for a given travel fraction.
    /// </summary>
    /// <param name="position">Travel fraction from 0 to 1.</param>
    /// <returns>The world transform at that point of the stroke.</returns>
    protected abstract Matrix4x4 PoseAt(float position);

    /// <summary>
    /// Places the mover at a travel fraction without moving through the intervening poses.
    /// </summary>
    /// <param name="position">Travel fraction from 0 to 1.</param>
    protected void SnapTo(float position)
    {
        Position = Math.Clamp(position, 0f, 1f);
        TargetPosition = Position;
        SetTransform(PoseAt(Position));
    }

    /// <summary>
    /// Starts the mover travelling toward a fraction of its stroke.
    /// </summary>
    /// <param name="position">Travel fraction from 0 to 1.</param>
    protected void MoveTo(float position)
    {
        var clamped = Math.Clamp(position, 0f, 1f);

        if (clamped == TargetPosition)
        {
            return;
        }

        TargetPosition = clamped;

        if (MoveRate <= 0f)
        {
            ArriveAt(clamped);
            return;
        }

        OnStartedMoving(clamped);
    }

    /// <inheritdoc/>
    public override void Think(float deltaTime)
    {
        if (!IsMoving)
        {
            OnIdleThink(deltaTime);
            return;
        }

        var step = MoveRate * deltaTime;
        var remaining = TargetPosition - Position;

        if (MathF.Abs(remaining) <= step)
        {
            ArriveAt(TargetPosition);
            return;
        }

        Position += MathF.CopySign(step, remaining);
        SetTransform(PoseAt(Position));
        MarkMoved();
    }

    private void ArriveAt(float position)
    {
        Position = position;
        TargetPosition = position;
        SetTransform(PoseAt(Position));
        MarkMoved();
        OnArrived(position);
    }

    /// <summary>
    /// Called when the mover reaches the end of a move. The base implementation fires
    /// <c>OnFullyOpen</c> and <c>OnFullyClosed</c> at the ends of the stroke.
    /// </summary>
    /// <param name="position">The travel fraction reached.</param>
    protected virtual void OnArrived(float position)
    {
        if (position >= 1f)
        {
            SendOutput("OnFullyOpen", null);
        }
        else if (position <= 0f)
        {
            SendOutput("OnFullyClosed", null);
        }
    }

    /// <summary>Called when a move begins. The base implementation fires <c>OnOpen</c> and <c>OnClose</c>.</summary>
    /// <param name="target">The travel fraction being moved to.</param>
    protected virtual void OnStartedMoving(float target)
        => SendOutput(target > Position ? "OnOpen" : "OnClose", null);

    /// <summary>Called on ticks where the mover is at rest, for subclasses with timed behaviour.</summary>
    /// <param name="deltaTime">Seconds since the last tick.</param>
    protected virtual void OnIdleThink(float deltaTime)
    {
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Open"))
        {
            MoveTo(1f);
            return true;
        }

        if (InputIs(input, "Close"))
        {
            MoveTo(0f);
            return true;
        }

        if (InputIs(input, "Toggle"))
        {
            // Mid-stroke a toggle reverses, matching the engine
            MoveTo(TargetPosition > 0f ? 0f : 1f);
            return true;
        }

        if (InputIs(input, "Reverse"))
        {
            MoveTo(TargetPosition > 0f ? 0f : 1f);
            return true;
        }

        if (InputIs(input, "SetPosition") && TryParsePosition(parameter, out var target))
        {
            MoveTo(target);
            return true;
        }

        if (InputIs(input, "SetPositionImmediately") && TryParsePosition(parameter, out var immediate))
        {
            SnapTo(immediate);
            return true;
        }

        // Toggle is a movement input here, so the enable/disable handling in the base class
        // must not also claim it
        return !InputIs(input, "Toggle") && base.AcceptInput(input, parameter, activator, caller);
    }

    private static bool TryParsePosition(string parameter, out float position)
        => float.TryParse(parameter, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out position);
}
