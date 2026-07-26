using ValveResourceFormat.Renderer.Entities;

namespace ValveResourceFormat.Renderer.Input;

/// <summary>
/// The player's side of the entity simulation: collision against brush movers, being carried by the
/// one underfoot, the push volumes impose, and the trigger touch pass that closes the frame.
/// </summary>
public partial class PlayerMovement
{
    /// <summary>
    /// Exposes the movement state to trigger volumes without widening <see cref="PlayerMovement"/>'s
    /// own surface: the controller can move and re-aim the player, which nothing else may.
    /// </summary>
    private sealed class Controller(PlayerMovement movement) : PlayerEntity.IPlayerController
    {
        public Vector3 HullCenter => movement.TracePosition;

        public Vector3 HullHalfExtents => movement.HullHalfExtents;

        public Vector3 Velocity
        {
            get => movement.Velocity;
            set => movement.Velocity = value;
        }

        public float ViewYawDegrees => float.RadiansToDegrees(movement.ActiveCamera?.Yaw ?? 0f);

        public void Teleport(Vector3 feetPosition, float? yawDegrees, Vector3? velocity)
            => movement.TeleportTo(feetPosition, yawDegrees, velocity);

        public void AddBaseVelocity(Vector3 velocity) => movement.AddBaseVelocity(velocity);
    }

    private EntityWorld? EntityWorld => Input.EntityWorld;

    /// <summary>
    /// The brush entity that produced the most recent trace hit. Read straight after a trace to
    /// learn what was hit; <see cref="TraceBBox"/> preserves it across its margin-restore probe.
    /// </summary>
    private BrushEntity? LastTraceEntity;

    /// <summary>The brush the player is standing on, so a moving one can carry them.</summary>
    private BrushEntity? GroundEntity;

    /// <summary>
    /// Velocity the world imposes this frame, accumulated by the push volumes the player is inside.
    /// It moves the player without ever entering <see cref="Velocity"/>, so leaving the volume drops
    /// the push instantly — the engine's base velocity.
    /// </summary>
    private Vector3 BaseVelocity;

    private Camera? ActiveCamera;

    /// <summary>Gets the entity the player is represented by inside the entity I/O system.</summary>
    public PlayerEntity? Entity { get; private set; }

    private void TeleportTo(Vector3 feetPosition, float? yawDegrees, Vector3? velocity)
    {
        TracePosition = feetPosition + new Vector3(0, 0, HullHalfExtents.Z);
        TracePositionSmooth = TracePosition;

        if (velocity.HasValue)
        {
            Velocity = velocity.Value;
        }

        if (yawDegrees.HasValue && ActiveCamera != null)
        {
            ActiveCamera.Yaw = float.DegreesToRadians(yawDegrees.Value);
            PreviousYaw = ActiveCamera.Yaw;
        }

        // The hull has jumped, so nothing about the old position may leak into the new one: no
        // step glide, no stuck-restore, and no carrying by whatever was underfoot
        Effects.ClearStepOffset();
        HasValidPosition = false;
        GroundEntity = null;
        BaseVelocity = Vector3.Zero;
    }

    private void AddBaseVelocity(Vector3 velocity)
    {
        BaseVelocity += velocity;

        // An upward push has to break ground contact or the ground snap would cancel it out
        if (velocity.Z > 0f && OnGround)
        {
            OnGround = false;
            GroundEntity = null;
        }
    }

    /// <summary>
    /// Registers the player with the entity world, so triggers can name it as their activator.
    /// Safe to call every frame; it only acts when the world changes.
    /// </summary>
    private void EnsurePlayerEntityRegistered()
    {
        var world = EntityWorld;

        if (world == null)
        {
            Entity = null;
            return;
        }

        if (Entity != null && ReferenceEquals(world.Player, Entity))
        {
            return;
        }

        Entity = EntityFactory.CreatePlayer(new Controller(this));
        world.Player = Entity;
    }

    /// <summary>
    /// Moves the player along with the brush they are standing on. Applied before the frame's own
    /// movement so the rest of the tick sees the player already at the carried position.
    /// </summary>
    private void CarryByGroundEntity(ref Vector3 position)
    {
        if (GroundEntity is not { Moved: true })
        {
            return;
        }

        var carried = GroundEntity.CarryPoint(position);

        if (carried != position)
        {
            position = carried;
            HasValidPosition = false; // the stuck-restore position belongs to the old pose
        }

        // Riders turn with a spinning platform, which is what makes a surf spinner readable
        var yawDelta = GroundEntity.CarryYawDegrees();

        if (yawDelta != 0f && ActiveCamera != null)
        {
            ActiveCamera.Yaw += float.DegreesToRadians(yawDelta);
            PreviousYaw += float.DegreesToRadians(yawDelta);
        }
    }

    /// <summary>
    /// Slides the player along the push their volumes impose, clipping against geometry but never
    /// touching <see cref="Velocity"/> — the push must vanish the moment they leave the volume.
    /// </summary>
    private Vector3 ApplyBaseVelocity(Vector3 position, float deltaTime, Vector3 halfExtents)
    {
        if (BaseVelocity == Vector3.Zero)
        {
            return position;
        }

        var remaining = BaseVelocity * deltaTime;

        for (var bump = 0; bump < 4; bump++)
        {
            var distance = remaining.Length();

            if (distance * distance < UntraceableDistanceSquared)
            {
                break;
            }

            var trace = TraceBBox(position, position + remaining, halfExtents);

            if (!trace.Hit)
            {
                return position + remaining;
            }

            position = trace.HitPosition;
            remaining *= 1f - Math.Clamp(trace.Distance / distance, 0f, 1f);

            // Plain projection: the push is a displacement, not a velocity, so it gets none of
            // the walkable-slope speed preservation the movement clip applies
            remaining -= trace.HitNormal * Vector3.Dot(remaining, trace.HitNormal);
        }

        return position;
    }

    /// <summary>
    /// Runs the trigger overlap pass for the frame and clears the push accumulated for it. Touch is
    /// tested after movement has settled, so a volume entered and left within one frame still fires.
    /// </summary>
    private void UpdateTriggerTouch()
    {
        BaseVelocity = Vector3.Zero;

        EntityWorld?.UpdateTriggerTouch(TracePosition, HullHalfExtents);
    }

    /// <summary>
    /// Sweeps the solid brush entities and folds the closest hit into <paramref name="result"/>,
    /// recording which entity it belonged to.
    /// </summary>
    private void TraceEntityBrushes(Vector3 from, Vector3 to, Vector3 halfExtents, bool detectStartSolid, ref Rubikon.TraceResult result)
    {
        var world = EntityWorld;

        if (world == null)
        {
            return;
        }

        foreach (var brush in world.BrushEntities)
        {
            if (!brush.IsSolid || brush.Collider is not { } collider || !collider.MightHit(from, to, halfExtents))
            {
                continue;
            }

            var hit = collider.TraceAABB(from, to, halfExtents, detectStartSolid);

            if (result.MinimizeWith(hit))
            {
                LastTraceEntity = brush;
                continue;
            }

            // MinimizeWith keeps the nearer hit, and a flush contact with world geometry is
            // already at distance zero — so being embedded in a brush that moved into the player
            // would otherwise be hidden behind it. Being stuck outranks merely touching.
            if (detectStartSolid && hit.StartSolid && !result.StartSolid)
            {
                result = hit;
                LastTraceEntity = brush;
            }
        }
    }
}
