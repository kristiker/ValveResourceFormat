using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    // Models whose meshlet packed IVB is encoded with the meshoptimizer meshlet codec, flagged by
    // m_nMeshoptMeshletEncodeVersion = 1. The buffer is a sequence of per-meshlet chunks (u16 header of
    // blob size + lane count, then a stock meshoptimizer meshlet blob) that Mesh decodes back into plain
    // packed elements when its VBIB is accessed.
    public class MeshletCodecTest
    {
        public static IEnumerable<string> MeshletCodecModels()
        {
            yield return "n0_lr0_agg_merge_antenna_card_0.vmdl_c";
            yield return "n0_lr0_agg_merge_dust_access_panel_03_color_0.vmdl_c";
        }

        [Test]
        [MethodDataSource(nameof(MeshletCodecModels))]
        public async Task OpensAndYieldsADrawableIndexBuffer(string fileName)
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", fileName);
            using var resource = new Resource { FileName = file };
            resource.Read(file);

            var model = (Model)resource.DataBlock!;
            var meshes = model.GetEmbeddedMeshes().ToList();

            await Assert.That(meshes).IsNotEmpty();

            foreach (var (mesh, _, _) in meshes)
            {
                var vbib = mesh.VBIB;
                await Assert.That(vbib.IndexBuffers).IsNotEmpty();

                foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
                {
                    foreach (var drawCall in sceneObject.GetArray("m_drawCalls"))
                    {
                        var bufferIndex = (int)drawCall.GetSubCollection("m_indexBuffer").GetUInt32Property("m_hBuffer");
                        var indexBuffer = vbib.IndexBuffers[bufferIndex];

                        // The draw call's own buffer is the vertex pipeline one, never the meshlet one
                        await Assert.That(indexBuffer.MeshletEncodeVersion).IsEqualTo(-1);
                        await Assert.That(indexBuffer.Data.Length).IsEqualTo((int)indexBuffer.TotalSizeInBytes);
                        await Assert.That((int)indexBuffer.ElementCount % 3).IsZero();

                        var startIndex = drawCall.GetInt32Property("m_nStartIndex");
                        var indexCount = drawCall.GetInt32Property("m_nIndexCount");
                        var vertexCount = drawCall.GetInt32Property("m_nVertexCount");

                        var indices = GltfModelExporter.ReadIndices(indexBuffer, startIndex, indexCount, 0);

                        await Assert.That(indices).Count().IsEqualTo(indexCount);
                        await Assert.That(indices.Max()).IsLessThan(vertexCount)
                            .Because("every index has to address the draw call's vertices");
                    }
                }
            }
        }

        // The decoded elements have to reproduce the classic index buffer: resolving each meshlet triangle's
        // local references through its vertex list must yield the MIDX triangle at the same slot (each meshlet
        // spans align2(m_nVertexCount) slots in both buffers, real triangles first, degenerate padding after).
        [Test]
        [MethodDataSource(nameof(MeshletCodecModels))]
        public async Task DecodesTheMeshletPackedIVB(string fileName)
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", fileName);
            using var resource = new Resource { FileName = file };
            resource.Read(file);

            var model = (Model)resource.DataBlock!;
            var mesh = model.GetEmbeddedMeshes().First().Mesh;
            var vbib = mesh.VBIB;

            foreach (var sceneObject in mesh.Data.GetArray("m_sceneObjects"))
            {
                var drawCalls = sceneObject.GetArray("m_drawCalls");
                var meshletBufferIndex = (int)drawCalls[0].GetSubCollection("m_meshletPackedIVB").GetUInt32Property("m_hBuffer");
                var buffer = vbib.IndexBuffers[meshletBufferIndex];

                await Assert.That(buffer.MeshletEncodeVersion).IsEqualTo(-1).Because("the buffer decodes on load");
                await Assert.That(buffer.Data.Length).IsEqualTo((int)buffer.TotalSizeInBytes);

                var elements = MemoryMarshal.Cast<byte, uint>(buffer.Data).ToArray();
                var meshlets = sceneObject.GetArray("m_meshlets");

                await Assert.That(elements.Length).IsEqualTo(meshlets.Sum(m => Align2(m.GetInt32Property("m_nVertexCount"))));

                var midx = vbib.IndexBuffers[(int)drawCalls[0].GetSubCollection("m_indexBuffer").GetUInt32Property("m_hBuffer")];
                var indices = GltfModelExporter.ReadIndices(midx, 0, (int)midx.ElementCount, 0);

                // Map each meshlet to its MIDX slot range
                var midxStart = new int[meshlets.Count];
                foreach (var drawCall in drawCalls)
                {
                    var first = drawCall.GetInt32Property("m_nFirstMeshlet");
                    var num = drawCall.GetInt32Property("m_nNumMeshlets");
                    var slot = drawCall.GetInt32Property("m_nStartIndex") / 3;
                    for (var m = first; m < first + num; m++)
                    {
                        midxStart[m] = slot;
                        slot += Align2(meshlets[m].GetInt32Property("m_nVertexCount"));
                    }
                }

                var entryOffset = 0;

                for (var mi = 0; mi < meshlets.Count; mi++)
                {
                    var vo = meshlets[mi].GetInt32Property("m_nVertexOffset");
                    var vc = Align2(meshlets[mi].GetInt32Property("m_nVertexCount"));
                    var tc = meshlets[mi].GetInt32Property("m_nTriangleCount");

                    await Assert.That(meshlets[mi].GetInt32Property("m_nTriangleOffset")).IsEqualTo(entryOffset)
                        .Because($"meshlet {mi} entries tile by align2(vertexCount)");

                    for (var k = tc; k < vc; k++)
                    {
                        await Assert.That(elements[entryOffset + k] & 0x3FFFFu).IsZero()
                            .Because($"meshlet {mi} slot {k} past the triangle count is padding");
                    }

                    // Meshlets with more than 64 vertices need the 64-entry sliding window to resolve their
                    // 6-bit references, and their seam stitching came out of Valve's experimental encoder
                    // with duplicated entries that MIDX disagrees with, so only the common case is compared.
                    if (vc > 64)
                    {
                        entryOffset += vc;
                        continue;
                    }

                    for (var k = 0; k < tc; k++)
                    {
                        var triangle = elements[entryOffset + k] & 0x3FFFFu;
                        var d0 = vo + (int)(elements[entryOffset + (int)(triangle & 0x3F)] >> 18);
                        var d1 = vo + (int)(elements[entryOffset + (int)((triangle >> 6) & 0x3F)] >> 18);
                        var d2 = vo + (int)(elements[entryOffset + (int)((triangle >> 12) & 0x3F)] >> 18);

                        var slot = (midxStart[mi] + k) * 3;
                        int e0 = indices[slot], e1 = indices[slot + 1], e2 = indices[slot + 2];

                        var matches = (d0 == e0 && d1 == e1 && d2 == e2)
                            || (d0 == e1 && d1 == e2 && d2 == e0)
                            || (d0 == e2 && d1 == e0 && d2 == e1);

                        await Assert.That(matches).IsTrue()
                            .Because($"meshlet {mi} triangle {k} resolves to ({d0},{d1},{d2}), MIDX has ({e0},{e1},{e2})");
                    }

                    entryOffset += vc;
                }
            }
        }

        private static int Align2(int value) => (value + 1) & ~1;
    }
}
