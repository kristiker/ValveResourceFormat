namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>trigger_hurt</c>. There is no health model here, so the volume reports the damage it would
/// deal through <see cref="Hurt"/> and fires the outputs a map wires to it. On movement maps these
/// are the pits, and the host decides what "died" means — usually a respawn.
/// </summary>
public sealed class TriggerHurt : TriggerEntity
{
    private float nextHurtTime;

    /// <summary>Raised each time the volume would deal damage, with the damage amount.</summary>
    public event EventHandler<float>? Hurt;

    /// <summary>Gets a value indicating whether the damage is lethal in one application.</summary>
    public bool IsLethal => GetFloat("damage") >= 100f;

    /// <inheritdoc/>
    protected override void OnTouch(EntityInstance? toucher)
    {
        if (World.Time < nextHurtTime)
        {
            return;
        }

        // The engine ticks damage at half-second intervals rather than per frame
        nextHurtTime = World.Time + 0.5f;

        SendOutput("OnHurt", toucher);

        if (toucher is PlayerEntity)
        {
            SendOutput("OnHurtPlayer", toucher);
            Hurt?.Invoke(this, GetFloat("damage"));
        }
    }
}
