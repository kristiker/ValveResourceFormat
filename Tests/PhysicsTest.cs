using System.IO;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.ResourceTypes;
using ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes;
using static ValveResourceFormat.ResourceTypes.RubikonPhysics.Shapes.Hull;

namespace Tests
{
    public class PhysicsTest
    {
        [Test]
        public void TestGenericGripPhysics()
        {
            using var resource = new Resource();
            var physPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "wooden_crate_01.vmdl_c");
            resource.Read(physPath);

            var physicsData = (PhysAggregateData)resource.GetBlockByType(BlockType.PHYS)!;

            Assert.That(physicsData, Is.Not.Null);
            Assert.That(physicsData.Parts, Is.Not.Empty);
            Assert.That(physicsData.Parts[0].Shape.Hulls, Is.Not.Empty);

            foreach (var hullDesc in physicsData.Parts[0].Shape.Hulls)
            {
                var hull = hullDesc.Shape;

                using (Assert.EnterMultipleScope())
                {
                    Assert.That(hull.GetVertexPositions().Length, Is.GreaterThan(0));
                    Assert.That(hull.GetEdges().Length, Is.GreaterThan(0));
                    Assert.That(hull.GetFaces().Length, Is.GreaterThan(0));
                    Assert.That(hull.GetPlanes().Length, Is.GreaterThan(0));
                }

                Assert.That(hull.RegionSVM, Is.Not.Null);

                var region = hull.RegionSVM;
                var hullPlanes = hull.GetPlanes();

                Assert.That(region.Nodes, Is.Not.Null);
                Assert.That(region.Nodes.Length, Is.GreaterThan(0));

                // Validate structure
                var i = 0;
                foreach (var node in region.Nodes)
                {
                    Assert.That(node.PlaneIndex, Is.LessThan(region.Planes.Length));

                    var allFlags = 0x20 | 0x40 | 0x80;
                    Assert.That((uint)node.Flags & ~allFlags, Is.Zero, $"Unexpected node flag: {node.Flags}");

                    if (node.IsInternal)
                    {
                        Assert.That(node.LeftChildIndex, Is.Zero); // why is it zero?

                        if (i > 0)
                        {
                            Assert.That(node.RightChildIndex, Is.Not.Zero);
                        }

                        Assert.That(node.RightChildIndex, Is.LessThan(region.Nodes.Length));
                    }
                    else
                    {
                        //Assert.That(node.PlaneIndex, Is.LessThan(hullPlanes.Length));
                        Assert.That(node.RightChildIndex, Is.Zero);
                    }

                    i++;
                }

                // iterate all nodes bfs
                var queue = new Queue<int>(0);
                var visited = new HashSet<int>();
                queue.Enqueue(1); // 0 has right and left both zero??
                i = 0;

                while (queue.Count > 0)
                {
                    Assert.That(i++, Is.LessThan(region.Nodes.Length), "Tree iteration failed. Infinite loop.");

                    var currentIndex = queue.Dequeue();
                    var currentNode = region.Nodes[currentIndex];

                    if (visited.Add(currentIndex) == false)
                    {
                        Assert.Fail($"Cycle detected in SVM tree. Revisited node: {currentIndex}");
                    }

                    if (currentNode.IsInternal)
                    {
                        var left = currentIndex + 1;  // Left child is always next node
                        Assert.That(left, Is.LessThan(region.Nodes.Length));
                        queue.Enqueue(left);

                        var right = currentIndex + currentNode.RightChildIndex;
                        Assert.That(right, Is.Not.Zero);
                        Assert.That(right, Is.LessThan(region.Nodes.Length));
                        queue.Enqueue(right);
                    }
                }
            }
        }

        [Test]
        public void TestHullStructure()
        {
            using var resource = new Resource();
            var physPath = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "juggernaut.vphys_c");
            resource.Read(physPath);

            var physicsData = (PhysAggregateData)resource.DataBlock!;
            Assert.That(physicsData, Is.Not.Null);
            Assert.That(physicsData.Parts, Is.Not.Empty);
            Assert.That(physicsData.Parts[0].Shape.Hulls, Is.Not.Empty);
            
            var hull = physicsData.Parts[0].Shape.Hulls[0].Shape;

            // Validate basic hull properties
            Assert.That(hull.Min, Is.Not.EqualTo(default(Vector3)));
            Assert.That(hull.Max, Is.Not.EqualTo(default(Vector3)));
            Assert.That(hull.Volume, Is.GreaterThan(0));

            // Validate vertices
            var vertices = hull.GetVertexPositions();
            Assert.That(vertices.Length, Is.GreaterThan(0));
            Assert.That(vertices.Length, Is.LessThanOrEqualTo(255), "Hulls can have at most 255 vertices");

            // Validate edges
            var edges = hull.GetEdges();
            Assert.That(edges.Length, Is.GreaterThan(0));
            Assert.That(edges.Length % 2, Is.EqualTo(0), "Edges should come in twin pairs");

            foreach (var edge in edges)
            {
                Assert.That(edge.Origin, Is.LessThan(vertices.Length),
                    $"Edge origin {edge.Origin} exceeds vertex count {vertices.Length}");
                Assert.That(edge.Next, Is.LessThan(edges.Length),
                    $"Edge next {edge.Next} exceeds edge count {edges.Length}");
                Assert.That(edge.Twin, Is.LessThan(edges.Length),
                    $"Edge twin {edge.Twin} exceeds edge count {edges.Length}");
            }

            // Validate faces
            var faces = hull.GetFaces();
            Assert.That(faces.Length, Is.GreaterThan(0));

            foreach (var face in faces)
            {
                Assert.That(face.Edge, Is.LessThan(edges.Length),
                    $"Face edge {face.Edge} exceeds edge count {edges.Length}");
            }

            // Validate planes
            var planes = hull.GetPlanes();
            Assert.That(planes.Length, Is.GreaterThan(0));

            foreach (var plane in planes)
            {
                var normalLength = plane.Normal.Length();
                Assert.That(normalLength, Is.InRange(0.99f, 1.01f),
                    $"Plane normal should be normalized, got length {normalLength}");
            }
        }
    }
}
