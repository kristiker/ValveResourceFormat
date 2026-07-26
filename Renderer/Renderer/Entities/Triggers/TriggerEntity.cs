using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// A trigger volume. Never solid: instead its brush hull is overlap-tested against the player's
/// collision box every tick, and the transitions in and out fire <c>OnStartTouch</c> and
/// <c>OnEndTouch</c>.
/// </summary>
public abstract class TriggerEntity : BrushEntity
{
    /// <summary>Gets a value indicating whether the player is inside the volume right now.</summary>
    public bool Touching { get; private set; }

    /// <summary>Triggers are pass-through, so they never take part in movement traces.</summary>
    public sealed override bool IsSolid => false;

    /// <summary>Gets or sets the name of a filter entity that decides whether a toucher counts.</summary>
    public string? FilterName { get; set; }

    private FilterEntity? filter;
    private bool filterResolved;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        FilterName ??= Data.GetStringProperty("filtername");

        // "startdisabled" is the compiled spelling; the engine also accepts StartDisabled
        if (GetBool("startdisabled"))
        {
            Enabled = false;
        }
    }

    /// <summary>
    /// Re-evaluates the overlap against a hull and fires the touch outputs for any transition.
    /// </summary>
    /// <param name="center">Centre of the toucher's collision box.</param>
    /// <param name="halfExtents">Half-extents of the toucher's collision box.</param>
    /// <param name="toucher">The entity doing the touching, used as the I/O activator.</param>
    public void UpdateTouch(Vector3 center, Vector3 halfExtents, EntityInstance? toucher)
    {
        var inside = Enabled
            && Collider != null
            && PassesFilter(toucher)
            && Collider.Overlaps(center, halfExtents);

        if (!inside)
        {
            if (Touching)
            {
                Touching = false;
                OnEndTouch(toucher);
            }

            return;
        }

        if (!Touching)
        {
            Touching = true;
            OnStartTouch(toucher);
        }

        OnTouch(toucher);
    }

    /// <summary>Called on the tick the toucher enters the volume.</summary>
    /// <param name="toucher">The touching entity.</param>
    protected virtual void OnStartTouch(EntityInstance? toucher)
        => SendOutput("OnStartTouch", toucher);

    /// <summary>Called every tick the toucher is inside, including the entry tick.</summary>
    /// <param name="toucher">The touching entity.</param>
    protected virtual void OnTouch(EntityInstance? toucher)
    {
    }

    /// <summary>Called on the tick the toucher leaves the volume.</summary>
    /// <param name="toucher">The touching entity.</param>
    protected virtual void OnEndTouch(EntityInstance? toucher)
        => SendOutput("OnEndTouch", toucher);

    private bool PassesFilter(EntityInstance? toucher)
    {
        if (string.IsNullOrEmpty(FilterName))
        {
            return true;
        }

        if (!filterResolved)
        {
            filterResolved = true;
            filter = World?.FindByName(FilterName) as FilterEntity;
        }

        return filter == null || filter.Passes(toucher);
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Disable") && Touching)
        {
            // Leaving the volume by being switched off still counts as an end-touch
            Touching = false;
            OnEndTouch(activator);
        }

        return base.AcceptInput(input, parameter, activator, caller);
    }
}
