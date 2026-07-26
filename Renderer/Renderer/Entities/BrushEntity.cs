namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// An entity backed by its own compiled brush model: the solid world geometry the player can stand
/// on and collide with, and the base every mover and trigger volume builds on.
/// </summary>
public abstract class BrushEntity : EntityInstance
{
    private Matrix4x4 previousTransform = Matrix4x4.Identity;
    private Matrix4x4 previousTransformInverse = Matrix4x4.Identity;
    private Matrix4x4 currentTransform = Matrix4x4.Identity;

    /// <summary>Gets or sets the collision shape. Null when the entity compiled without physics.</summary>
    public EntityCollider? Collider { get; set; }

    /// <summary>
    /// Gets a value indicating whether the player collides with this brush right now. Triggers are
    /// never solid; a <c>func_brush</c> follows its solidity setting and enabled state.
    /// </summary>
    public virtual bool IsSolid => Enabled;

    /// <summary>
    /// Gets a value indicating whether the brush moved since the previous tick, so a player standing
    /// on it needs carrying.
    /// </summary>
    public bool Moved { get; private set; }

    /// <summary>Gets the entity's current world transform.</summary>
    public Matrix4x4 CurrentTransform => currentTransform;

    /// <summary>
    /// Establishes the spawn pose. Movers call this from their own <see cref="EntityInstance.Spawn"/>
    /// after computing their travel limits.
    /// </summary>
    public override void Spawn()
    {
        SetTransform(SpawnTransform);
        previousTransform = currentTransform;
        Matrix4x4.Invert(previousTransform, out previousTransformInverse);
        Moved = false;
    }

    /// <summary>
    /// Moves the brush, updating its collider and renderables together.
    /// </summary>
    /// <param name="worldTransform">The new world transform.</param>
    protected void SetTransform(Matrix4x4 worldTransform)
    {
        currentTransform = worldTransform;

        if (Collider != null)
        {
            Collider.Transform = worldTransform;
        }

        foreach (var node in VisualNodes)
        {
            // A node attached to a parent entity is driven by that parent instead; writing the
            // mover's own pose over it would fight the attachment every frame
            if (node.Parent == null)
            {
                node.Transform = worldTransform;
            }
        }
    }

    /// <summary>
    /// Latches the pose the brush had at the start of this tick, so <see cref="CarryPoint"/> can
    /// report how far a rider moved. Called by the world once per tick before thinking.
    /// </summary>
    public void BeginTick()
    {
        previousTransform = currentTransform;
        Matrix4x4.Invert(previousTransform, out previousTransformInverse);
        Moved = false;
    }

    /// <summary>
    /// Records that this tick's think produced motion.
    /// </summary>
    protected void MarkMoved() => Moved = true;

    /// <summary>
    /// Maps a world point riding on this brush from where it sat at the start of the tick to where
    /// the brush has since carried it. Returns the point unchanged when the brush did not move.
    /// </summary>
    /// <param name="worldPoint">The rider's position before the brush moved.</param>
    /// <returns>The carried position.</returns>
    public Vector3 CarryPoint(Vector3 worldPoint)
    {
        if (!Moved)
        {
            return worldPoint;
        }

        return Vector3.Transform(Vector3.Transform(worldPoint, previousTransformInverse), currentTransform);
    }

    /// <summary>
    /// The yaw in degrees this brush has turned a rider through over the tick. Only the yaw is
    /// applied to the player, matching the engine's handling of riders on rotating platforms.
    /// </summary>
    /// <returns>The yaw delta in degrees, zero when the brush did not rotate.</returns>
    public float CarryYawDegrees()
    {
        if (!Moved)
        {
            return 0f;
        }

        var delta = previousTransformInverse * currentTransform;
        var forward = Vector3.TransformNormal(Vector3.UnitX, delta);

        return float.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X));
    }

    /// <summary>
    /// Converts a QAngle-valued keyvalue such as <c>movedir</c> or <c>pushdir</c> into a world-space
    /// unit direction, oriented by the entity's own spawn rotation.
    /// </summary>
    /// <remarks>
    /// The direction is read as local to the entity and composed with its <c>angles</c>. Source 1
    /// had no separate <c>movedir</c> key — the direction lived in <c>angles</c>, which the mover
    /// then zeroed — so the two only became independent keys once a brush could be rotated without
    /// changing where it travels. Entities compiled with zero <c>angles</c>, which is nearly all of
    /// them, are unaffected either way.
    /// </remarks>
    /// <param name="key">The keyvalue name.</param>
    /// <param name="fallback">Direction used when the key is absent.</param>
    /// <returns>The world-space direction.</returns>
    protected Vector3 GetWorldDirection(string key, Vector3 fallback)
    {
        if (!Data.ContainsKey(key))
        {
            return fallback;
        }

        var local = EntityTransformHelper.QAngleToForwardDirection(Data.GetVector3Property(key));
        var world = Vector3.TransformNormal(local, EntityTransformHelper.CreateRotationMatrixFromEulerAngles(SpawnAngles));

        return world.LengthSquared() > 0f ? Vector3.Normalize(world) : fallback;
    }

    /// <summary>
    /// Gets the size of the brush along its own axes, used by <c>func_door</c> to derive its travel
    /// from the geometry. Zero when the entity has no collision shape.
    /// </summary>
    protected Vector3 LocalSize => Collider?.LocalBounds.Size ?? Vector3.Zero;
}
