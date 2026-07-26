namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_push</c>. While the player is inside, the volume imposes <c>speed</c> units per second
/// along <c>pushdir</c> as base velocity — the boosters and wind tunnels of movement maps. With the
/// once-only spawnflag it instead applies a single impulse on entry, which is how launch pads work.
/// </summary>
public sealed class TriggerPush : TriggerEntity
{
    /// <summary>Spawnflag: apply one impulse on entry instead of a continuous push.</summary>
    private const int SF_PUSH_ONCE = 128;

    private Vector3 pushVelocity;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        // An unset pushdir is straight up, matching the engine's default of a zero QAngle pitched up
        var direction = GetWorldDirection("pushdir", Vector3.UnitZ);

        pushVelocity = direction * GetFloat("speed", 100f);
    }

    /// <summary>Gets the velocity this volume imposes, in world space.</summary>
    public Vector3 PushVelocity => pushVelocity;

    /// <inheritdoc/>
    protected override void OnStartTouch(EntityInstance? toucher)
    {
        base.OnStartTouch(toucher);

        if (HasSpawnFlag(SF_PUSH_ONCE) && toucher is PlayerEntity player)
        {
            player.Controller.Velocity += pushVelocity;
        }
    }

    /// <inheritdoc/>
    protected override void OnTouch(EntityInstance? toucher)
    {
        if (!HasSpawnFlag(SF_PUSH_ONCE) && toucher is PlayerEntity player)
        {
            player.Controller.AddBaseVelocity(pushVelocity);
        }
    }
}
