using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Diagnostics;
using ValveResourceFormat.IO.ContentFormats.ValveMap;
using static ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;
using static ValveResourceFormat.IO.HammerMeshBuilder;
using System.ComponentModel;
using Plankton;

namespace ValveResourceFormat.IO
{
    internal class HammerMeshBuilder
    {
        public PlanktonMesh pMesh = new();

        public List<string> Materials { get; set; } = new();

        [Flags]
        public enum EdgeFlag
        {
            None = 0x0,
            SoftNormals = 0x1,
            HardNormals = 0x2,
        }

        public CDmePolygonMesh GenerateMesh()
        {
            var mesh = new CDmePolygonMesh();

            var faceMaterialIndices = CreateStream<Datamodel.IntArray, int>(8, "materialindex:0");
            var faceFlags = CreateStream<Datamodel.IntArray, int>(3, "flags:0");
            mesh.FaceData.Streams.Add(faceMaterialIndices);
            mesh.FaceData.Streams.Add(faceFlags);

            var textureCoords = CreateStream<Datamodel.Vector2Array, Vector2>(1, "texcoord:0");
            var textureCoords4 = CreateStream<Datamodel.Vector4Array, Vector4>(1, "texcoord:4");
            var normals = CreateStream<Datamodel.Vector3Array, Vector3>(1, "normal:0");
            var tangent = CreateStream<Datamodel.Vector4Array, Vector4>(1, "tangent:0");
            mesh.FaceVertexData.Streams.Add(textureCoords);
            mesh.FaceVertexData.Streams.Add(normals);
            mesh.FaceVertexData.Streams.Add(tangent);

            var vertexPositions = CreateStream<Datamodel.Vector3Array, Vector3>(3, "position:0");
            mesh.VertexData.Streams.Add(vertexPositions);

            var edgeFlags = CreateStream<Datamodel.IntArray, int>(3, "flags:0");
            mesh.EdgeData.Streams.Add(edgeFlags);

            foreach (var Vertex in pMesh.Vertices)
            {
                var vertexDataIndex = mesh.VertexData.Size;
                mesh.VertexEdgeIndices.Add(Vertex.OutgoingHalfedge);
                mesh.VertexDataIndices.Add(vertexDataIndex);
                mesh.VertexData.Size++;

                vertexPositions.Data.Add(new Vector3(Vertex.X, Vertex.Y, Vertex.Z));
            }

            for (var i = 0; i < pMesh.Halfedges.Count / 2; i++)
            {
                //all dummy data for now since only material is good enough for exporting tool brushes
                mesh.EdgeData.Size++;
                normals.Data.Add(new Vector3(0, -0.44f, 0.89f));
                tangent.Data.Add(new Vector4(1, 0, 0, -1));
                normals.Data.Add(new Vector3(0, -0.44f, 0.89f));
                tangent.Data.Add(new Vector4(1, 0, 0, -1));
                edgeFlags.Data.Add((int)EdgeFlag.None);
                textureCoords.Data.Add(new Vector2(0, 0));
                textureCoords.Data.Add(new Vector2(0, 1));
            }

            int prevHalfEdge = -1;
            for (var i = 0; i < pMesh.Halfedges.Count; i++)
            {
                var halfEdge = pMesh.Halfedges[i];
                //EdgeData refers to a single edge, so its half of the total
                //of half edges and both halfs of the edge should have the same EdgeData Index
                if (pMesh.Halfedges.GetPairHalfedge(i) == prevHalfEdge)
                {
                    mesh.EdgeDataIndices.Add(prevHalfEdge / 2);
                }
                else
                {
                    mesh.EdgeDataIndices.Add(i / 2);
                }

                mesh.EdgeVertexIndices.Add(pMesh.Halfedges[halfEdge.NextHalfedge].StartVertex);
                mesh.EdgeOppositeIndices.Add(pMesh.Halfedges.GetPairHalfedge(i));
                mesh.EdgeNextIndices.Add(halfEdge.NextHalfedge);
                if (halfEdge.AdjacentFace != -1)
                {
                    mesh.EdgeFaceIndices.Add(halfEdge.AdjacentFace);
                }
                else
                {
                    mesh.EdgeFaceIndices.Add(-1);
                }
                mesh.EdgeVertexDataIndices.Add(i);

                prevHalfEdge = i;

                mesh.FaceVertexData.Size += 1;
            }

            foreach (var Face in pMesh.Faces)
            {
                var faceDataIndex = mesh.FaceData.Size;
                mesh.FaceDataIndices.Add(faceDataIndex);
                mesh.FaceData.Size++;

                var materialIndex = mesh.Materials.IndexOf(pMesh.Vertices[pMesh.Halfedges[Face.FirstHalfedge].StartVertex].material);
                if (materialIndex == -1)
                {
                    materialIndex = mesh.Materials.Count;
                    mesh.Materials.Add(pMesh.Vertices[pMesh.Halfedges[Face.FirstHalfedge].StartVertex].material);
                }

                faceMaterialIndices.Data.Add(materialIndex);
                faceFlags.Data.Add(0);

                mesh.FaceEdgeIndices.Add(Face.FirstHalfedge);
            }


            AddSubdivisionStuff(mesh);

            return mesh;
        }

        public void AddVertex(Vector3 Vertex)
        {
            pMesh.Vertices.Add(Vertex.X, Vertex.Y, Vertex.Z);
        }

        public void AddFace(IEnumerable<int> Indices)
        {
            VerifyIndicesWithinBounds(Indices);
            pMesh.Faces.AddFace(Indices);

        }

        private bool VerifyIndicesWithinBounds(IEnumerable<int> indices)
        {
            foreach (var index in indices)
            {
                if (index < 0 || index >= pMesh.Vertices.Count)
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddSubdivisionStuff(CDmePolygonMesh mesh, int count = 8)
        {
            for (int i = 0; i < count; i++)
            {
                mesh.SubdivisionData.SubdivisionLevels.Add(0);
            }
        }

        public static CDmePolygonMeshDataStream<T> CreateStream<TArray, T>(int dataStateFlags, string name, params T[] data)
            where TArray : Datamodel.Array<T>, new()
        {

            var dmArray = new TArray();
            foreach (var item in data)
            {
                dmArray.Add(item);
            }

            var stream = new CDmePolygonMeshDataStream<T>
            {
                Name = name,
                StandardAttributeName = name[..^2],
                SemanticName = name[..^2],
                SemanticIndex = 0,
                VertexBufferLocation = 0,
                DataStateFlags = dataStateFlags,
                SubdivisionBinding = null,
                Data = dmArray
            };

            return stream;
        }
    }
}
