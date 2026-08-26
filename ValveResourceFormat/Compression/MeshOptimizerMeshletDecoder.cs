using System;
using System.Runtime.CompilerServices;

namespace ValveResourceFormat.Compression
{
    /// <summary>
    /// Decoder for the meshoptimizer meshlet codec, and for Valve's meshlet packed index buffer framing
    /// around it (<c>m_nMeshoptMeshletEncodeVersion</c> = 1).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A version 1 encoded buffer is a sequence of per-meshlet chunks, one per <c>m_meshlets</c> descriptor
    /// in order. Each chunk is a little-endian <see cref="ushort"/> header followed by a stock meshoptimizer
    /// meshlet blob: header bits 0-9 are the blob size in bytes, bits 10-15 are <c>(laneCount - 1) &amp; 63</c>,
    /// where <c>laneCount = align2(max(vertexCount, triangleCount))</c> is both the vertex and the triangle
    /// count the blob was encoded with (triangles beyond <c>m_nTriangleCount</c> are degenerate padding).
    /// Decoded, each meshlet becomes <c>align2(vertexCount)</c> packed <see cref="uint"/> elements:
    /// <c>(vertexListValue &lt;&lt; 18) | (c &lt;&lt; 12) | (b &lt;&lt; 6) | a</c>, the same layout unencoded
    /// MSLT data uses.
    /// </para>
    /// </remarks>
    /// <seealso href="https://github.com/zeux/meshoptimizer/blob/master/src/meshletcodec.cpp">The blob decoder is a C# port of meshoptimizer's meshletcodec.</seealso>
    public static class MeshOptimizerMeshletDecoder
    {
        /// <summary>
        /// Per-meshlet counts from the mesh's <c>m_meshlets</c> descriptors, needed to frame the chunks.
        /// </summary>
        /// <param name="VertexCount">The descriptor's <c>m_nVertexCount</c>.</param>
        /// <param name="TriangleCount">The descriptor's <c>m_nTriangleCount</c>.</param>
        public readonly record struct MeshletCounts(int VertexCount, int TriangleCount);

        private static int Align2(int value) => (value + 1) & ~1;

        /// <summary>
        /// Decodes a version 1 encoded meshlet packed index buffer into its plain <see cref="uint"/> elements.
        /// </summary>
        /// <param name="encoded">The encoded buffer contents.</param>
        /// <param name="meshlets">The meshlet descriptors of the mesh, in order.</param>
        /// <param name="decodedSizeInBytes">The buffer's total decoded size (its element count times four).</param>
        public static byte[] DecodePackedIVB(ReadOnlySpan<byte> encoded, ReadOnlySpan<MeshletCounts> meshlets, int decodedSizeInBytes)
        {
            var decoded = new byte[decodedSizeInBytes];
            var elements = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(decoded.AsSpan());

            var offset = 0;
            var element = 0;

            Span<uint> vertices = stackalloc uint[256];
            Span<uint> triangles = stackalloc uint[256];

            foreach (var meshlet in meshlets)
            {
                if (offset + 2 > encoded.Length)
                {
                    throw new InvalidOperationException($"Encoded meshlet buffer ended early at meshlet chunk offset {offset}.");
                }

                var header = (ushort)(encoded[offset] | (encoded[offset + 1] << 8));
                var size = header & 0x3FF;
                var laneField = header >> 10;

                var lanes = Align2(Math.Max(meshlet.VertexCount, meshlet.TriangleCount));
                var elementCount = Align2(meshlet.VertexCount);

                if (laneField != ((lanes - 1) & 0x3F))
                {
                    throw new InvalidOperationException($"Meshlet chunk header lane count {laneField} does not match the descriptor's {lanes} lanes.");
                }

                if (offset + 2 + size > encoded.Length)
                {
                    throw new InvalidOperationException($"Meshlet chunk of {size} bytes at offset {offset} overruns the encoded buffer.");
                }

                DecodeMeshletRaw(vertices, lanes, triangles, lanes, encoded.Slice(offset + 2, size));

                for (var k = 0; k < elementCount; k++)
                {
                    var triangle = triangles[k];
                    var a = triangle & 0x3F;
                    var b = (triangle >> 8) & 0x3F;
                    var c = (triangle >> 16) & 0x3F;

                    elements[element + k] = (vertices[k] & 0x3FFF) << 18 | c << 12 | b << 6 | a;
                }

                element += elementCount;
                offset += 2 + size;
            }

            if (offset != encoded.Length)
            {
                throw new InvalidOperationException($"Meshlet chunks cover {offset} bytes of the {encoded.Length} byte encoded buffer.");
            }

            if (element * sizeof(uint) != decodedSizeInBytes)
            {
                throw new InvalidOperationException($"Meshlets decoded to {element} elements, expected {decodedSizeInBytes / sizeof(uint)}.");
            }

            return decoded;
        }

        /// <summary>
        /// Decodes one meshlet blob into its vertex list and its triangles (each packed as <c>0xccbbaa</c>).
        /// </summary>
        public static void DecodeMeshletRaw(Span<uint> vertices, int vertexCount,
            Span<uint> triangles, int triangleCount,
            ReadOnlySpan<byte> buffer)
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThan(vertexCount, 256);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(triangleCount, 256);

            var codesSize = (triangleCount + 1) / 2;
            var ctrlSize = (vertexCount + 3) / 4;
            var gapSize = (codesSize + ctrlSize < 16) ? 16 - (codesSize + ctrlSize) : 0;

            if (buffer.Length < codesSize + ctrlSize + gapSize)
            {
                throw new InvalidOperationException("Buffer too small for meshlet data.");
            }

            var end = buffer.Length;
            var codes = buffer[(end - codesSize)..];
            var ctrl = buffer[(end - codesSize - ctrlSize)..];
            var data = buffer;
            var boundOffset = end - codesSize - ctrlSize - gapSize;

            var dataOffset = DecodeVertices(vertices, ctrl, data, boundOffset, vertexCount);

            if (dataOffset < 0)
            {
                throw new InvalidOperationException("Failed to decode meshlet vertices.");
            }

            Span<byte> decodedTriangles = stackalloc byte[triangleCount * 3];
            var endOffset = DecodeTriangles(decodedTriangles, codes, data, dataOffset, boundOffset, triangleCount);

            for (var i = 0; i < triangleCount; i++)
            {
                triangles[i] = (uint)(decodedTriangles[i * 3] | (decodedTriangles[i * 3 + 1] << 8) | (decodedTriangles[i * 3 + 2] << 16));
            }

            if (endOffset < 0)
            {
                throw new InvalidOperationException("Failed to decode meshlet triangles.");
            }

            if (endOffset != boundOffset)
            {
                throw new InvalidOperationException("Meshlet data did not decode to expected size.");
            }
        }

        private static int DecodeVertices(Span<uint> vertices, ReadOnlySpan<byte> ctrl, ReadOnlySpan<byte> data, int boundOffset, int vertexCount)
        {
            var last = ~0u;
            var dataOffset = 0;

            for (var i = 0; i < vertexCount; i += 4)
            {
                if (dataOffset > boundOffset)
                {
                    return -1;
                }

                var code4 = ctrl[i / 4];

                for (var k = 0; k < 4; k++)
                {
                    var code = ((code4 >> k) & 1) | ((code4 >> (k + 3)) & 2);
                    var length = code4 == 0xFF ? 4 : code;

                    // Read up to 4 bytes little-endian; we need at least `length` bytes available
                    // but we read up to 4 branchlessly (safe because gap guarantees 16 bytes of overread)
                    uint v = 0;
                    if (length > 0)
                    {
                        v = data[dataOffset];
                    }

                    if (length > 1)
                    {
                        v |= (uint)data[dataOffset + 1] << 8;
                    }

                    if (length > 2)
                    {
                        v |= (uint)data[dataOffset + 2] << 16;
                    }

                    if (length > 3)
                    {
                        v |= (uint)data[dataOffset + 3] << 24;
                    }

                    // unzigzag + 1
                    var d = (v >> 1) ^ (uint)(-(int)(v & 1));
                    var r = last + d + 1;

                    if (i + k < vertexCount)
                    {
                        vertices[i + k] = r;
                    }

                    dataOffset += length;
                    last = r;
                }
            }

            return dataOffset;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void WriteTriangle(Span<byte> triangles, int i, uint fifo)
        {
            triangles[i * 3 + 0] = (byte)(fifo >> 8);
            triangles[i * 3 + 1] = (byte)(fifo >> 16);
            triangles[i * 3 + 2] = (byte)(fifo >> 24);
        }

        private static int DecodeTriangles(Span<byte> triangles, ReadOnlySpan<byte> codes, ReadOnlySpan<byte> data, int dataOffset, int boundOffset, int triangleCount)
        {
            uint next = 0;
            Span<uint> fifo = stackalloc uint[3];

            for (var i = 0; i < triangleCount; i++)
            {
                if (dataOffset > boundOffset)
                {
                    return -1;
                }

                var code = (uint)((codes[i / 2] >> ((i & 1) * 4)) & 0xF);
                uint tri;

                if (code < 12)
                {
                    var edge = fifo[(int)(code / 4)];
                    edge >>= (int)((code << 3) & 16);

                    var e = data[dataOffset];
                    var c = (code & 1) != 0 ? (uint)e : next;
                    dataOffset += (int)(code & 1);
                    next += 1 - (code & 1);

                    tri = ((edge & 0xff) << 16) | (edge & 0xff00) | c | (c << 24);
                }
                else
                {
                    var fea = code > 12 ? 1 : 0;
                    var feb = code > 13 ? 1 : 0;
                    var fec = code > 14 ? 1 : 0;

                    uint e;

                    e = data[dataOffset];
                    var a = fea != 0 ? e : next;
                    dataOffset += fea;
                    next += (uint)(1 - fea);

                    e = data[dataOffset];
                    var b = feb != 0 ? e : next;
                    dataOffset += feb;
                    next += (uint)(1 - feb);

                    e = data[dataOffset];
                    var c = fec != 0 ? e : next;
                    dataOffset += fec;
                    next += (uint)(1 - fec);

                    tri = c | (a << 8) | (b << 16) | (c << 24);
                }

                WriteTriangle(triangles, i, tri);

                fifo[2] = fifo[1];
                fifo[1] = fifo[0];
                fifo[0] = tri;
            }

            return dataOffset;
        }
    }
}
