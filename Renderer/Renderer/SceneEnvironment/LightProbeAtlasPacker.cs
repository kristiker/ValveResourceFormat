using System.Linq;

namespace ValveResourceFormat.Renderer.SceneEnvironment;

/// <summary>The volume a single light probe occupies in the merged atlas, in probe grid cells.</summary>
public readonly record struct LightProbeAtlasSlot(int X, int Y, int Z, int Width, int Height, int Depth);

/// <summary>Packs light probe grids into a single 3D atlas volume, measured in probe grid cells.</summary>
public static class LightProbeAtlasPacker
{
    /// <summary>XY alignment of a slot origin, in grid cells. One BC6H/DXT block.</summary>
    public const int BlockAlignment = 4;

    /// <summary>Cells reserved around each slot to keep a block-wide copy off its neighbour.</summary>
    public const int Gutter = BlockAlignment;

    /// <summary>Places every probe grid in a shared atlas volume.</summary>
    public static int Pack(IReadOnlyList<(int Width, int Height, int Depth)> sizes, int maxSide, int maxDepth,
        out LightProbeAtlasSlot[] slots, out (int Width, int Height, int Depth) atlasSize)
    {
        slots = new LightProbeAtlasSlot[sizes.Count];
        atlasSize = default;

        if (sizes.Count == 0)
        {
            return 0;
        }

        var order = Enumerable.Range(0, sizes.Count)
            .Where(i => sizes[i] is { Width: > 0, Height: > 0, Depth: > 0 })
            .OrderByDescending(i => (long)sizes[i].Width * sizes[i].Height * sizes[i].Depth)
            .ToArray();

        if (order.Length == 0)
        {
            return 0;
        }

        var reserved = order.Select(i => Reserve(sizes[i])).ToArray();

        var minWidth = reserved.Max(r => r.Width);
        var minHeight = reserved.Max(r => r.Height);
        var minSide = Align(Math.Max(minWidth, minHeight));

        var bestVolume = long.MaxValue;
        LightProbeAtlasSlot[]? bestSlots = null;
        var bestCount = 0;
        (int Width, int Height, int Depth) bestSize = default;

        for (var side = minSide; side <= maxSide; side = Align(side * 2))
        {
            var count = PackInto(order, reserved, sizes, side, side, maxDepth, out var candidate, out var used);

            if (count == 0)
            {
                continue;
            }

            var volume = (long)used.Width * used.Height * used.Depth;

            if (count > bestCount || (count == bestCount && volume < bestVolume))
            {
                bestCount = count;
                bestVolume = volume;
                bestSlots = candidate;
                bestSize = used;
            }

            if (bestCount == order.Length && side >= minSide * 4)
            {
                break;
            }
        }

        if (bestSlots == null)
        {
            return 0;
        }

        slots = bestSlots;
        atlasSize = bestSize;
        return bestCount;
    }

    private static (int Width, int Height, int Depth) Reserve((int Width, int Height, int Depth) size)
        => (Align(size.Width + Gutter), Align(size.Height + Gutter), size.Depth + 1);

    private static int Align(int value) => (value + BlockAlignment - 1) / BlockAlignment * BlockAlignment;

    private static int PackInto(int[] order, (int Width, int Height, int Depth)[] reserved,
        IReadOnlyList<(int Width, int Height, int Depth)> sizes,
        int binWidth, int binHeight, int maxDepth,
        out LightProbeAtlasSlot[] slots, out (int Width, int Height, int Depth) used)
    {
        slots = new LightProbeAtlasSlot[sizes.Count];
        used = default;

        var free = new List<(int X, int Y, int Z, int Width, int Height, int Depth)>
        {
            (0, 0, 0, binWidth, binHeight, maxDepth),
        };

        var placed = 0;

        for (var n = 0; n < order.Length; n++)
        {
            var need = reserved[n];

            if (need.Width > binWidth || need.Height > binHeight)
            {
                continue;
            }

            var best = -1;
            var bestWaste = long.MaxValue;

            for (var f = 0; f < free.Count; f++)
            {
                var box = free[f];

                if (box.Width < need.Width || box.Height < need.Height || box.Depth < need.Depth)
                {
                    continue;
                }

                var waste = ((long)box.Z << 40) + (long)box.Width * box.Height * box.Depth;

                if (waste < bestWaste)
                {
                    bestWaste = waste;
                    best = f;
                }
            }

            if (best < 0)
            {
                continue;
            }

            var chosen = free[best];
            free.RemoveAt(best);

            var index = order[n];
            var size = sizes[index];
            slots[index] = new LightProbeAtlasSlot(chosen.X, chosen.Y, chosen.Z, size.Width, size.Height, size.Depth);
            placed++;

            used = (Math.Max(used.Width, chosen.X + need.Width),
                Math.Max(used.Height, chosen.Y + need.Height),
                Math.Max(used.Depth, chosen.Z + need.Depth));

            if (chosen.Width > need.Width)
            {
                free.Add((chosen.X + need.Width, chosen.Y, chosen.Z, chosen.Width - need.Width, chosen.Height, chosen.Depth));
            }

            if (chosen.Height > need.Height)
            {
                free.Add((chosen.X, chosen.Y + need.Height, chosen.Z, need.Width, chosen.Height - need.Height, chosen.Depth));
            }

            if (chosen.Depth > need.Depth)
            {
                free.Add((chosen.X, chosen.Y, chosen.Z + need.Depth, need.Width, need.Height, chosen.Depth - need.Depth));
            }
        }

        return placed;
    }
}
