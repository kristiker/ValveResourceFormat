using System.Globalization;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>logic_auto</c>. Fires its startup outputs once, on the first tick after the map has spawned,
/// which is where movement maps put their initial door and trigger states.
/// </summary>
public sealed class LogicAuto : EntityInstance
{
    private bool fired;

    /// <inheritdoc/>
    public override bool WantsThink => !fired;

    /// <inheritdoc/>
    public override void Think(float deltaTime)
    {
        if (fired)
        {
            return;
        }

        fired = true;

        SendOutput("OnMapSpawn", null);
        SendOutput("OnNewGame", null);
        SendOutput("OnMapTransition", null);
    }
}

/// <summary>
/// <c>logic_relay</c>. Passes a <c>Trigger</c> input straight through to <c>OnTrigger</c>, so a map
/// can fan one event out to many targets from a single place.
/// </summary>
public sealed class LogicRelay : EntityInstance
{
    /// <summary>Spawnflag: only ever fire once.</summary>
    private const int SF_RELAY_FIRE_ONCE = 1;

    /// <inheritdoc/>
    public override void Spawn()
    {
        if (GetBool("startdisabled"))
        {
            Enabled = false;
        }
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "Trigger"))
        {
            if (!Enabled)
            {
                return true;
            }

            SendOutput("OnTrigger", activator);

            if (HasSpawnFlag(SF_RELAY_FIRE_ONCE))
            {
                Enabled = false;
            }

            return true;
        }

        return base.AcceptInput(input, parameter, activator, caller);
    }
}

/// <summary>
/// <c>logic_timer</c>. Fires <c>OnTimer</c> on a repeating interval while enabled.
/// </summary>
public sealed class LogicTimer : EntityInstance
{
    private float nextFireTime;

    /// <inheritdoc/>
    public override bool WantsThink => true;

    /// <inheritdoc/>
    public override void Spawn()
    {
        if (GetBool("startdisabled"))
        {
            Enabled = false;
        }

        nextFireTime = World.Time + GetFloat("initialdelay");
    }

    /// <inheritdoc/>
    public override void Think(float deltaTime)
    {
        var interval = GetFloat("refiretime");

        if (!Enabled || interval <= 0f || World.Time < nextFireTime)
        {
            return;
        }

        nextFireTime = World.Time + interval;
        SendOutput("OnTimer", null);
    }

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "FireTimer"))
        {
            SendOutput("OnTimer", activator);
            return true;
        }

        if (InputIs(input, "ResetTimer"))
        {
            nextFireTime = World.Time + GetFloat("refiretime");
            return true;
        }

        if (InputIs(input, "Enable"))
        {
            nextFireTime = World.Time + GetFloat("refiretime");
        }

        return base.AcceptInput(input, parameter, activator, caller);
    }
}

/// <summary>
/// <c>logic_case</c>. Routes an input to one of sixteen numbered outputs, either by matching the
/// parameter against the <c>case01</c>..<c>case16</c> keyvalues or by picking at random.
/// </summary>
public sealed class LogicCase : EntityInstance
{
    private readonly Random random = new();

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (InputIs(input, "InValue"))
        {
            for (var i = 1; i <= 16; i++)
            {
                var value = Data.GetStringProperty(CaseKey(i));

                if (!string.IsNullOrEmpty(value) && string.Equals(value, parameter, StringComparison.OrdinalIgnoreCase))
                {
                    SendOutput(OnCaseKey(i), activator);
                    return true;
                }
            }

            SendOutput("OnDefault", activator);
            return true;
        }

        if (InputIs(input, "PickRandom") || InputIs(input, "PickRandomShuffle"))
        {
            var defined = new List<int>();

            for (var i = 1; i <= 16; i++)
            {
                if (Connections.Exists(c => string.Equals(c.OutputName, OnCaseKey(i), StringComparison.OrdinalIgnoreCase)))
                {
                    defined.Add(i);
                }
            }

            if (defined.Count > 0)
            {
                SendOutput(OnCaseKey(defined[random.Next(defined.Count)]), activator);
            }

            return true;
        }

        return base.AcceptInput(input, parameter, activator, caller);
    }

    private static string CaseKey(int index) => string.Create(CultureInfo.InvariantCulture, $"case{index:00}");

    private static string OnCaseKey(int index) => string.Create(CultureInfo.InvariantCulture, $"OnCase{index:00}");
}

/// <summary>
/// A filter entity, named by a trigger's <c>filtername</c> to decide whether a toucher counts.
/// Only name matching is modelled; every other filter class passes everything through.
/// </summary>
public sealed class FilterEntity : EntityInstance
{
    /// <summary>Gets a value indicating whether the filter inverts its result.</summary>
    private bool Negated => GetBool("negated");

    /// <summary>
    /// Tests an entity against the filter.
    /// </summary>
    /// <param name="candidate">The entity being tested, usually the player.</param>
    /// <returns><see langword="true"/> when the entity passes.</returns>
    public bool Passes(EntityInstance? candidate)
    {
        if (!string.Equals(Classname, "filter_activator_name", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var wanted = Data.GetStringProperty("filtername");

        if (string.IsNullOrEmpty(wanted))
        {
            return true;
        }

        var matched = candidate?.TargetName != null
            && string.Equals(candidate.TargetName, wanted, StringComparison.OrdinalIgnoreCase);

        return matched != Negated;
    }
}

/// <summary>
/// A point entity with no behaviour of its own, kept in the world so that I/O and teleport
/// destinations can resolve it by name. <c>info_teleport_destination</c>, <c>info_target</c> and
/// <c>path_track</c> all spawn as one of these.
/// </summary>
public sealed class PointEntity : EntityInstance
{
}
