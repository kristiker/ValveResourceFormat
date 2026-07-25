using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using ValveKeyValue;
using ValveResourceFormat.Renderer.Buffers;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace ValveResourceFormat.Renderer;

public partial class GPUMeshBufferCache
{
    /// <summary>
    /// The immutable gather tables for one morph resource, shared by every mesh instance using it.
    /// Ranges point into the concatenated table buffers owned by this cache.
    /// </summary>
    public class MorphTables
    {
        /// <summary>Gets the atlas holding the encoded morph deltas.</summary>
        public required RenderTexture Atlas { get; init; }

        /// <summary>Gets the width of the composite in slots; a slot's row is <c>slot / Width</c>.</summary>
        public required int CompositeWidth { get; init; }

        /// <summary>Gets the number of slots one instance of this morph occupies.</summary>
        public required int SlotCount { get; init; }

        /// <summary>Gets the number of blend weights one instance of this morph occupies.</summary>
        public required int WeightCount { get; init; }

        /// <summary>Gets where this morph's per-slot rect lists start in the shared rect start buffer.</summary>
        public required uint RectStartsBase { get; init; }

        /// <summary>Gets whether this morph has any rects to gather, and so a range worth reserving.</summary>
        public required bool HasMorphData { get; init; }
    }

    /// <summary>A single morph rect, mirroring <c>MorphRect</c> in morph_composite.comp.slang.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MorphRect
    {
        public uint DestX;
        public uint DestY;
        public uint SrcX;
        public uint SrcY;

        public uint MorphId;
        /// <summary>Rect width in texels. Only used to enumerate covered slots; the shader derives its own position within the rect.</summary>
        public uint Width;
        /// <summary>Rect height in texels. Only used to enumerate covered slots.</summary>
        public uint Height;
        public uint Padding;

        public Vector4 Offsets;
        public Vector4 Ranges;
    }

    private readonly Dictionary<Morph, MorphTables> morphTables = [];

    // Concatenated across every morph loaded so far, so one dispatch can reach all of them.
    // Append only, so the ranges handed out above stay valid as more morphs load.
    private readonly List<MorphRect> morphRects = [];
    private readonly List<uint> morphRectStarts = [];
    private readonly List<uint> morphRectEntries = [];

    private StorageBuffer? morphRectsGpu;
    private StorageBuffer? morphRectStartsGpu;
    private StorageBuffer? morphRectEntriesGpu;
    private bool morphTablesDirty;

    /// <summary>Returns the shared gather tables for the given morph resource, building them on first use.</summary>
    public MorphTables GetMorphTables(Morph morph)
    {
        if (morphTables.TryGetValue(morph, out var tables))
        {
            return tables;
        }

        ArgumentNullException.ThrowIfNull(morph.TextureResource);

        var atlas = RendererContext.MaterialLoader.LoadTexture(morph.TextureResource);
        var compositeWidth = morph.Data.GetInt32Property("m_nWidth");
        var compositeHeight = morph.Data.GetInt32Property("m_nHeight");
        var slotCount = compositeWidth * compositeHeight;

        var morphDatas = morph.GetMorphDatas();
        var rects = BuildMorphRects(morphDatas, atlas);

        tables = new MorphTables
        {
            Atlas = atlas,
            CompositeWidth = compositeWidth,
            SlotCount = slotCount,
            WeightCount = Math.Max(morph.GetMorphCount(), morphDatas.Count),
            RectStartsBase = (uint)morphRectStarts.Count,
            HasMorphData = slotCount > 0 && rects.Count > 0,
        };

        morphTables.Add(morph, tables);

        if (tables.HasMorphData)
        {
            AppendMorphRectTable(rects, compositeWidth, compositeHeight);
            morphTablesDirty = true;
        }

        return tables;
    }

    /// <summary>Binds the shared morph gather tables, uploading them if morphs have loaded since the last bind.</summary>
    public void BindMorphTables()
    {
        morphRectsGpu ??= new StorageBuffer(ReservedBufferSlots.MorphRects);
        morphRectStartsGpu ??= new StorageBuffer(ReservedBufferSlots.MorphRectStarts);
        morphRectEntriesGpu ??= new StorageBuffer(ReservedBufferSlots.MorphRectEntries);

        if (morphTablesDirty)
        {
            morphRectsGpu.Create(morphRects);
            morphRectStartsGpu.Create(morphRectStarts);
            morphRectEntriesGpu.Create(morphRectEntries);
            morphTablesDirty = false;
        }

        morphRectsGpu.BindBufferBase();
        morphRectStartsGpu.BindBufferBase();
        morphRectEntriesGpu.BindBufferBase();
    }

    private void DeleteMorphTables()
    {
        morphRectsGpu?.Delete();
        morphRectStartsGpu?.Delete();
        morphRectEntriesGpu?.Delete();

        morphRectsGpu = null;
        morphRectStartsGpu = null;
        morphRectEntriesGpu = null;

        morphTables.Clear();
        morphRects.Clear();
        morphRectStarts.Clear();
        morphRectEntries.Clear();
        morphTablesDirty = false;
    }

    /// <summary>Flattens the rects of every morph target into gather sources, in atlas texel coordinates.</summary>
    private static List<MorphRect> BuildMorphRects(IReadOnlyList<KVObject> morphDatas, RenderTexture atlas)
    {
        var rects = new List<MorphRect>();

        for (var morphId = 0; morphId < morphDatas.Count; morphId++)
        {
            var morphData = morphDatas[morphId];

            if (morphData.ValueType != KVValueType.Collection)
            {
                continue;
            }

            foreach (var rect in morphData.GetArray("m_morphRectDatas") ?? [])
            {
                //TODO: Implement normal/wrinkle bundle type (second bundle data usually, if exists)
                var bundleData = (rect.GetArray("m_bundleDatas") ?? [])[0];

                rects.Add(new MorphRect
                {
                    DestX = (uint)rect.GetInt32Property("m_nXLeftDst"),
                    DestY = (uint)rect.GetInt32Property("m_nYTopDst"),
                    SrcX = (uint)MathF.Round(bundleData.GetFloatProperty("m_flULeftSrc") * atlas.Width),
                    SrcY = (uint)MathF.Round(bundleData.GetFloatProperty("m_flVTopSrc") * atlas.Height),

                    MorphId = (uint)morphId,
                    Width = (uint)MathF.Round(rect.GetFloatProperty("m_flUWidthSrc") * atlas.Width),
                    Height = (uint)MathF.Round(rect.GetFloatProperty("m_flVHeightSrc") * atlas.Height),

                    Offsets = new Vector4(bundleData.GetFloatArray("m_offsets")),
                    Ranges = new Vector4(bundleData.GetFloatArray("m_ranges")),
                });
            }
        }

        return rects;
    }

    /// <summary>
    /// Appends one morph's per-slot rect lists to the shared tables in compressed sparse row form. Start and
    /// entry values are absolute indices into the shared buffers, so the shader needs no per-morph rebasing.
    /// </summary>
    private void AppendMorphRectTable(List<MorphRect> rects, int compositeWidth, int compositeHeight)
    {
        var rectsBase = (uint)morphRects.Count;
        var slotCount = compositeWidth * compositeHeight;
        var counts = new int[slotCount];

        foreach (var rect in rects)
        {
            foreach (var slot in CoveredSlots(rect, compositeWidth, compositeHeight))
            {
                counts[slot]++;
            }
        }

        var entriesBase = (uint)morphRectEntries.Count;
        var starts = new uint[slotCount + 1];
        var total = 0u;

        for (var slot = 0; slot < slotCount; slot++)
        {
            starts[slot] = entriesBase + total;
            total += (uint)counts[slot];
        }

        starts[slotCount] = entriesBase + total;

        var entries = new uint[total];
        var cursors = new int[slotCount];

        for (var slot = 0; slot < slotCount; slot++)
        {
            cursors[slot] = (int)(starts[slot] - entriesBase);
        }

        for (var i = 0; i < rects.Count; i++)
        {
            foreach (var slot in CoveredSlots(rects[i], compositeWidth, compositeHeight))
            {
                entries[cursors[slot]++] = rectsBase + (uint)i;
            }
        }

        morphRectStarts.AddRange(starts);
        morphRectEntries.AddRange(entries);
        morphRects.AddRange(rects);
    }

    /// <summary>Enumerates the composite slots covered by the given rect.</summary>
    private static IEnumerable<int> CoveredSlots(MorphRect rect, int compositeWidth, int compositeHeight)
    {
        for (var y = 0; y < rect.Height; y++)
        {
            var destY = (int)rect.DestY + y;

            // Content can author rects running past the composite; do not let them wrap into other rows
            if (destY >= compositeHeight)
            {
                break;
            }

            for (var x = 0; x < rect.Width; x++)
            {
                var destX = (int)rect.DestX + x;

                if (destX >= compositeWidth)
                {
                    break;
                }

                yield return destY * compositeWidth + destX;
            }
        }
    }
}
