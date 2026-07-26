using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_teleport</c>. Moves whoever enters to the entity named by <c>target</c>, keeping their
/// speed — which is what makes it usable as a stage link on movement maps.
/// </summary>
public sealed class TriggerTeleport : TriggerEntity
{
    /// <summary>Spawnflag: keep the toucher's view angles instead of adopting the destination's.</summary>
    private const int SF_TELEPORT_PRESERVE_ANGLES = 32;

    private EntityInstance? destination;
    private EntityInstance? landmark;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        destination = World.FindByName(Data.GetStringProperty("target"));

        var landmarkName = Data.GetStringProperty("landmark");

        if (!string.IsNullOrEmpty(landmarkName))
        {
            landmark = World.FindByName(landmarkName);
        }
    }

    /// <inheritdoc/>
    protected override void OnStartTouch(EntityInstance? toucher)
    {
        base.OnStartTouch(toucher);

        if (destination == null || toucher is not PlayerEntity player)
        {
            return;
        }

        var controller = player.Controller;

        if (landmark != null)
        {
            TeleportRelativeToLandmark(controller, landmark);
            return;
        }

        // The destination marks where the feet go; lifting a unit clear of the floor keeps the
        // hull from arriving embedded in it. Velocity carries over untouched, so a teleporter
        // placed mid-ramp hands the player back onto the next stage at speed.
        var feetPosition = destination.SpawnOrigin + new Vector3(0, 0, 1f);
        var yaw = HasSpawnFlag(SF_TELEPORT_PRESERVE_ANGLES) ? (float?)null : destination.SpawnAngles.Y;

        controller.Teleport(feetPosition, yaw, controller.Velocity);
    }

    /// <summary>
    /// Carries the toucher's offset, heading and velocity through the rotation between landmark and
    /// destination, so a pair of them reads as one continuous space.
    /// </summary>
    private void TeleportRelativeToLandmark(PlayerEntity.IPlayerController controller, EntityInstance from)
    {
        var rotationDegrees = destination!.SpawnAngles.Y - from.SpawnAngles.Y;
        var turn = Matrix4x4.CreateRotationZ(float.DegreesToRadians(rotationDegrees));

        var feet = controller.HullCenter - new Vector3(0, 0, controller.HullHalfExtents.Z);
        var offset = feet - from.SpawnOrigin;

        controller.Teleport(
            destination.SpawnOrigin + Vector3.Transform(offset, turn),
            controller.ViewYawDegrees + rotationDegrees,
            Vector3.Transform(controller.Velocity, turn));
    }
}
