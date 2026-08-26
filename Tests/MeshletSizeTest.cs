using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ValveResourceFormat;
using ValveResourceFormat.Blocks;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.Serialization.KeyValues;

namespace Tests
{
    // What carrying meshlets alongside an ordinary index buffer costs on disk.
    public class MeshletSizeTest
    {
        public static IEnumerable<string> MeshletModels()
        {
            yield return "n0_lr0_agg_merge_antenna_card_0.vmdl_c";
            yield return "n0_lr0_agg_merge_dust_access_panel_03_color_0.vmdl_c";
            yield return "n0_lr0_agg_prop_plants001_0.vmdl_c";
            yield return "n0_lr0_c0_s_cb_b_nomerge236.vmdl_c";
        }

        [Test]
        [MethodDataSource(nameof(MeshletModels))]
        public async Task ReportsMeshletOverhead(string fileName)
        {
            var file = Path.Combine(TestContext.TestDirectory!, "Files", fileName);
            using var resource = new Resource { FileName = file };
            resource.Read(file);

            var total = new FileInfo(file).Length;

            var mvtx = SizeOf(resource, BlockType.MVTX);
            var midx = SizeOf(resource, BlockType.MIDX);
            var mslt = SizeOf(resource, BlockType.MSLT);

            var meshletCount = 0;
            var mdat = resource.GetBlockByType(BlockType.MDAT) as Mesh;

            if (mdat != null)
            {
                foreach (var sceneObject in mdat.Data.GetArray("m_sceneObjects"))
                {
                    meshletCount += sceneObject.GetArray("m_meshlets")?.Count ?? 0;
                }
            }

            // m_PackedAABB (2 uints), m_CullingData (4 bytes), and four ints, before any KV3 framing
            var meshletTable = meshletCount * 28L;
            var geometry = mvtx + midx + mslt;
            var meshletCost = mslt + meshletTable;

            Console.WriteLine($"{fileName}");
            Console.WriteLine($"   file={total} MVTX={mvtx} MIDX={midx} MSLT={mslt} meshlets={meshletCount} (table ~{meshletTable}B)");
            Console.WriteLine($"   MSLT vs MIDX          : {Percent(mslt, midx)}%");
            Console.WriteLine($"   MSLT vs MVTX+MIDX     : {Percent(mslt, mvtx + midx)}%");
            Console.WriteLine($"   MSLT+table vs geometry: {Percent(meshletCost, geometry - mslt)}%");
            Console.WriteLine($"   MSLT+table vs file    : {Percent(meshletCost, total)}%");

            await Assert.That(geometry).IsGreaterThan(0);
        }

        private static long SizeOf(Resource resource, BlockType type)
            => resource.GetBlockByType(type)?.Size ?? 0;

        private static string Percent(long part, long whole)
            => whole == 0 ? "n/a" : (part * 100.0 / whole).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
