using ValveKeyValue;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// One entry of an entity's output list: when <see cref="OutputName"/> fires, <see cref="InputName"/> is
/// queued on every entity matching <see cref="TargetName"/> after <see cref="Delay"/> seconds.
/// </summary>
public sealed class EntityConnection
{
    /// <summary>Gets the output that triggers this connection, for example <c>OnStartTouch</c>.</summary>
    public required string OutputName { get; init; }

    /// <summary>Gets how <see cref="TargetName"/> is resolved. Compiled maps only ever use
    /// <see cref="EntityIOTargetType.EntityNameOrClassName"/>.</summary>
    public required EntityIOTargetType TargetType { get; init; }

    /// <summary>Gets the targetname, classname or <c>!</c>-prefixed special name receiving the input.</summary>
    public required string TargetName { get; init; }

    /// <summary>Gets the input fired on the target, for example <c>Enable</c>.</summary>
    public required string InputName { get; init; }

    /// <summary>Gets the parameter passed with the input, replacing whatever the output supplies. Empty when unset.</summary>
    public required string OverrideParam { get; init; }

    /// <summary>Gets the delay in seconds between the output firing and the input arriving.</summary>
    public required float Delay { get; init; }

    /// <summary>Gets how many times this connection may fire, or -1 for unlimited.</summary>
    public required int TimesToFire { get; init; }

    /// <summary>Gets or sets how many times this connection has fired so far.</summary>
    public int TimesFired { get; set; }

    /// <summary>Gets a value indicating whether this connection has used up its <see cref="TimesToFire"/> budget.</summary>
    public bool Exhausted => TimesToFire >= 0 && TimesFired >= TimesToFire;

    /// <summary>
    /// Reads the connection list an <see cref="ResourceTypes.EntityLump.Entity"/> carries, skipping malformed entries.
    /// </summary>
    /// <param name="connections">The raw <c>m_connections</c> array, or <see langword="null"/> when the entity has none.</param>
    /// <returns>The parsed connections, empty when there are none.</returns>
    public static List<EntityConnection> Parse(List<KVObject>? connections)
    {
        if (connections == null || connections.Count == 0)
        {
            return [];
        }

        var parsed = new List<EntityConnection>(connections.Count);

        foreach (var connection in connections)
        {
            var outputName = connection.GetStringProperty("m_outputName");
            var inputName = connection.GetStringProperty("m_inputName");

            if (string.IsNullOrEmpty(outputName) || string.IsNullOrEmpty(inputName))
            {
                continue;
            }

            var overrideParam = connection.GetStringProperty("m_overrideParam") ?? string.Empty;

            // Hammer writes the literal "(null)" for an unset parameter
            if (overrideParam == "(null)")
            {
                overrideParam = string.Empty;
            }

            parsed.Add(new EntityConnection
            {
                OutputName = outputName,
                TargetType = (EntityIOTargetType)connection.GetInt32Property("m_targetType"),
                TargetName = connection.GetStringProperty("m_targetName") ?? string.Empty,
                InputName = inputName,
                OverrideParam = overrideParam,
                Delay = connection.GetFloatProperty("m_flDelay"),
                TimesToFire = connection.GetInt32Property("m_nTimesToFire"),
            });
        }

        return parsed;
    }
}
