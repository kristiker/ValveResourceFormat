namespace ValveResourceFormat.Renderer.Entities;

/// <summary>
/// <c>func_brush</c>. A static piece of brush geometry whose solidity can be switched at runtime,
/// which movement maps use for gates that open a route once something else has been triggered.
/// </summary>
public sealed class FuncBrush : BrushEntity
{
    /// <summary>How a <c>func_brush</c> decides whether it collides.</summary>
    private enum SolidityMode
    {
        /// <summary>Solid only while the brush is enabled.</summary>
        ToggleSolid = 0,

        /// <summary>Never collides, regardless of enabled state.</summary>
        NeverSolid = 1,

        /// <summary>Always collides, even while disabled (only rendering toggles).</summary>
        AlwaysSolid = 2,
    }

    private SolidityMode solidity;

    /// <inheritdoc/>
    public override void Spawn()
    {
        base.Spawn();

        solidity = (SolidityMode)GetInt("solidity");

        if (GetBool("startdisabled"))
        {
            Enabled = false;
        }
    }

    /// <inheritdoc/>
    public override bool IsSolid => solidity switch
    {
        SolidityMode.NeverSolid => false,
        SolidityMode.AlwaysSolid => true,
        _ => Enabled,
    };

    /// <inheritdoc/>
    public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
    {
        if (base.AcceptInput(input, parameter, activator, caller))
        {
            foreach (var node in VisualNodes)
            {
                node.LayerEnabled = Enabled;
            }

            return true;
        }

        return false;
    }
}
