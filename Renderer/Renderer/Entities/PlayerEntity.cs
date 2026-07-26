namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// The player's stand-in inside the entity system. Triggers name it as the I/O activator and
/// <c>!player</c> resolves to it; the movement controller supplies the actual state through
/// <see cref="Controller"/>.
/// </summary>
public sealed class PlayerEntity : EntityInstance
{
    /// <summary>
    /// The movement state a trigger volume can read and change.
    /// </summary>
    public interface IPlayerController
    {
        /// <summary>Gets the centre of the player's collision hull in world space.</summary>
        Vector3 HullCenter { get; }

        /// <summary>Gets the half-extents of the player's collision hull.</summary>
        Vector3 HullHalfExtents { get; }

        /// <summary>Gets or sets the player's velocity in units per second.</summary>
        Vector3 Velocity { get; set; }

        /// <summary>Gets the player's current view yaw in degrees.</summary>
        float ViewYawDegrees { get; }

        /// <summary>
        /// Moves the player, optionally reorienting and re-aiming them.
        /// </summary>
        /// <param name="feetPosition">Where the bottom of the hull should end up.</param>
        /// <param name="yawDegrees">New view yaw, or <see langword="null"/> to keep the current one.</param>
        /// <param name="velocity">New velocity, or <see langword="null"/> to keep the current one.</param>
        void Teleport(Vector3 feetPosition, float? yawDegrees, Vector3? velocity);

        /// <summary>
        /// Adds to the velocity the world imposes on the player this tick — the push a
        /// <c>trigger_push</c> applies while the player is inside it. Cleared every tick.
        /// </summary>
        /// <param name="velocity">The velocity to add.</param>
        void AddBaseVelocity(Vector3 velocity);
    }

    /// <summary>Gets the movement controller backing this entity.</summary>
    public required IPlayerController Controller { get; init; }

    /// <summary>Gets the centre of the player's collision hull.</summary>
    public Vector3 HullCenter => Controller.HullCenter;

    /// <summary>Gets the half-extents of the player's collision hull.</summary>
    public Vector3 HullHalfExtents => Controller.HullHalfExtents;
}
