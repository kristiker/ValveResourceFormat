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
using System.Linq;
using SharpGLTF.Geometry.VertexTypes;

namespace ValveResourceFormat.IO
{
    internal class HammerMeshBuilder
    {
        public PlanktonMesh pMesh = new();

        [Flags]
        public enum EdgeFlag
        {
            None = 0x0,
            SoftNormals = 0x1,
            HardNormals = 0x2,
        }

        public class Vertex
        {
            public Vector3 position;
            public string material;

            public Vertex(Vector3 position, string material)
            {
                this.position = position;
                this.material = material;
            }
        }

        public CDmePolygonMesh GenerateMesh()
        {
            var mesh = new CDmePolygonMesh();

            var faceMaterialIndices = CreateStream<Datamodel.IntArray, int>(8, "materialindex:0");
            var faceFlags = CreateStream<Datamodel.IntArray, int>(3, "flags:0");
            mesh.FaceData.Streams.Add(faceMaterialIndices);
            mesh.FaceData.Streams.Add(faceFlags);

            var textureCoords = CreateStream<Datamodel.Vector2Array, Vector2>(1, "texcoord:0");
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
                mesh.EdgeData.Size++;
                edgeFlags.Data.Add((int)EdgeFlag.None);
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

                if (halfEdge.AdjacentFace != -1)
                {
                    var normal = CalculateFaceNormal(pMesh, i);
                    var tangents = CalculateTangentFromNormal(normal);
                    normals.Data.Add(normal);
                    tangent.Data.Add(tangents);

                }
                else
                {
                    normals.Data.Add(new Vector3(0, 0, 0));
                    tangent.Data.Add(new Vector4(0, 0, 0, 0));
                }

                var startVertex = pMesh.Vertices[halfEdge.StartVertex];
                var startVertexVector = new Vector3(startVertex.X, startVertex.Y, startVertex.Z);

                textureCoords.Data.Add(new Vector2(startVertexVector.Length() % 1.0f));
            }

            foreach (var Face in pMesh.Faces)
            {
                var faceDataIndex = mesh.FaceData.Size;
                mesh.FaceDataIndices.Add(faceDataIndex);
                mesh.FaceData.Size++;

                var mat = pMesh.Vertices[pMesh.Halfedges[Face.FirstHalfedge].StartVertex].material;
                var materialIndex = mesh.Materials.IndexOf(mat);
                if (materialIndex == -1 && mat != null)
                {
                    materialIndex = mesh.Materials.Count;
                    faceMaterialIndices.Data.Add(materialIndex);
                    mesh.Materials.Add(mat);
                }

                faceFlags.Data.Add(0);

                mesh.FaceEdgeIndices.Add(Face.FirstHalfedge);
            }


            AddSubdivisionStuff(mesh);

            return mesh;
        }

        public void AddVertex(Vertex Vertex)
        {
            pMesh.Vertices.Add(Vertex.position.X, Vertex.position.Y, Vertex.position.Z, Vertex.material);
        }

        public void AddFace(int index1, int index2, int index3)
        {
            pMesh.Faces.AddFace(index1, index2, index3);
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

        private static Vector3 CalculateFaceNormal(PlanktonMesh mesh, int i)
        {
            var parentFaceHalfEdges = new int[] { i, mesh.Halfedges[i].NextHalfedge, mesh.Halfedges[i].PrevHalfedge };
            var v1 = mesh.Vertices[mesh.Halfedges[parentFaceHalfEdges[0]].StartVertex];
            var v2 = mesh.Vertices[mesh.Halfedges[parentFaceHalfEdges[1]].StartVertex];
            var v3 = mesh.Vertices[mesh.Halfedges[parentFaceHalfEdges[2]].StartVertex];

            var p1 = new Vector3(v1.X, v1.Y, v1.Z);
            var p2 = new Vector3(v2.X, v2.Y, v2.Z);
            var p3 = new Vector3(v3.X, v3.Y, v3.Z);

            var normal = Vector3.Normalize(Vector3.Cross(p2 - p1, p3 - p1));

            return normal;
        }

        public static Vector4 CalculateTangentFromNormal(Vector3 normal)
        {
            Vector3 tangent1 = Vector3.Cross(normal, new Vector3(0, 1, 0));
            Vector3 tangent2 = Vector3.Cross(normal, new Vector3(0, 0, 1));
            return new Vector4(tangent1.Length() > tangent2.Length() ? tangent1 : tangent2, 1.0f);
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
