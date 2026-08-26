using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.IO;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    // Models whose meshlet buffer is encoded with the meshoptimizer meshlet codec, flagged by
    // m_nMeshoptMeshletEncodeVersion. They still carry an ordinary index buffer for the vertex pipeline, so
    // they have to open and draw even while that codec is undecoded.
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

                        // The draw call's own buffer is the vertex pipeline one, never the meshlet encoded one
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

        // The meshlet buffer is handed over still encoded, because meshopt_decodeMeshlet needs each meshlet's
        // exact encoded size and nothing in the resource stores one. Decoding it is not supported yet, but
        // misreading it as a plain meshopt index stream (which its m_bMeshoptCompressed flag alone implies)
        // used to throw and take the whole model down with it.
        [Test]
        [MethodDataSource(nameof(MeshletCodecModels))]
        public async Task LeavesTheMeshletBufferEncodedInsteadOfMisreadingIt(string fileName)
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", fileName);
            using var resource = new Resource { FileName = file };
            resource.Read(file);

            var model = (Model)resource.DataBlock!;
            var vbib = model.GetEmbeddedMeshes().First().Mesh.VBIB;

            var meshletBuffers = vbib.IndexBuffers.Where(b => b.MeshletEncodeVersion >= 0).ToList();

            await Assert.That(meshletBuffers).IsNotEmpty().Because("these models are meshlet codec encoded");

            foreach (var buffer in meshletBuffers)
            {
                await Assert.That(buffer.MeshletEncodeVersion).IsEqualTo(1);
                await Assert.That(buffer.Data).IsNotEmpty();
                await Assert.That(buffer.Data.Length).IsLessThan((int)buffer.TotalSizeInBytes)
                    .Because("it is still the encoded payload, not the decoded elements");
            }
        }
    }
}
