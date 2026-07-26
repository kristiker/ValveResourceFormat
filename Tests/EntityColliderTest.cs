using System.IO;
using System.Numerics;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    /// <summary>
    /// Traces and overlap tests against a brush entity's shape once it has been moved and turned:
    /// the query is answered in the shape's own space and mapped back, so the answer has to follow
    /// the entity rather than the compiled geometry.
    /// </summary>
    [TestFixture]
    public class EntityColliderTest
    {
        private static readonly Vector3 PlayerHull = new(16, 16, 36);

        /// <summary>A height comfortably inside the fixture shape, whose hull spans roughly z -5..307.</summary>
        private const float ProbeHeight = 150f;

        private static EntityCollider LoadCollider()
        {
            var file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "generic_grip.vphys_c");

            using var resource = new Resource { FileName = file };
            resource.Read(file);

            return new EntityCollider((PhysAggregateData)resource.DataBlock!);
        }

        [Test]
        public void ShapeHasBoundsAndIsNotEmpty()
        {
            var collider = LoadCollider();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(collider.IsEmpty, Is.False);
                Assert.That(collider.LocalBounds.Size.Z, Is.GreaterThan(100f));
                Assert.That(collider.LocalBounds.Min.Z, Is.LessThan(ProbeHeight));
                Assert.That(collider.LocalBounds.Max.Z, Is.GreaterThan(ProbeHeight));
            }
        }

        [Test]
        public void TranslatingTheEntityMovesTheHitWithIt()
        {
            var collider = LoadCollider();

            var start = new Vector3(-1000, 0, ProbeHeight);
            var end = new Vector3(1000, 0, ProbeHeight);

            var atOrigin = collider.TraceAABB(start, end, PlayerHull);

            Assert.That(atOrigin.Hit, Is.True, "the sweep should cross the shape at its compiled position");

            var offset = new Vector3(3000, -500, 0);
            collider.Transform = Matrix4x4.CreateTranslation(offset);

            var moved = collider.TraceAABB(start + offset, end + offset, PlayerHull);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(moved.Hit, Is.True);
                Assert.That(moved.Distance, Is.EqualTo(atOrigin.Distance).Within(1e-2f));
                Assert.That((moved.HitPosition - (atOrigin.HitPosition + offset)).Length(), Is.LessThan(1e-2f));
                Assert.That((moved.HitNormal - atOrigin.HitNormal).Length(), Is.LessThan(1e-4f));
            }

            // The old sweep no longer reaches it
            Assert.That(collider.TraceAABB(start, end, PlayerHull).Hit, Is.False);
        }

        [Test]
        public void RotatingTheEntityRotatesTheFaceThatGetsHit()
        {
            var collider = LoadCollider();

            // Approach the shape's local -X face head on
            var alongX = collider.TraceAABB(
                new Vector3(-1000, 0, ProbeHeight),
                new Vector3(1000, 0, ProbeHeight),
                PlayerHull);

            Assert.That(alongX.Hit, Is.True);

            // Turn the entity a quarter turn: local +X now points along world +Y, so the same
            // face is reached by approaching from -Y instead
            collider.Transform = Matrix4x4.CreateRotationZ(float.DegreesToRadians(90f));

            var alongY = collider.TraceAABB(
                new Vector3(0, -1000, ProbeHeight),
                new Vector3(0, 1000, ProbeHeight),
                PlayerHull);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(alongY.Hit, Is.True);

                // The player hull is square in XY, so the turned approach must stop just as short
                Assert.That(alongY.Distance, Is.EqualTo(alongX.Distance).Within(1e-2f));

                // ...and the surface it stopped against faces back down the new approach
                Assert.That(alongY.HitNormal.Y, Is.EqualTo(alongX.HitNormal.X).Within(1e-3f));
                Assert.That(alongY.HitNormal.X, Is.EqualTo(-alongX.HitNormal.Y).Within(1e-3f));
            }
        }

        [Test]
        public void OverlapFollowsTheEntityTransform()
        {
            var collider = LoadCollider();

            var inside = new Vector3(0, 0, ProbeHeight);

            Assert.That(collider.Overlaps(inside, PlayerHull), Is.True, "the shape should contain its own centre line");

            var offset = new Vector3(0, 0, 5000);
            collider.Transform = Matrix4x4.CreateTranslation(offset);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(collider.Overlaps(inside, PlayerHull), Is.False, "the shape moved away from the probe");
                Assert.That(collider.Overlaps(inside + offset, PlayerHull), Is.True, "the probe moved with it");
            }
        }

        [Test]
        public void RayTraceFollowsTheEntityTransform()
        {
            var collider = LoadCollider();

            var start = new Vector3(-1000, 0, ProbeHeight);
            var end = new Vector3(1000, 0, ProbeHeight);

            var atOrigin = collider.TraceRay(start, end);

            Assert.That(atOrigin.Hit, Is.True, "the ray should cross the shape at its compiled position");

            var offset = new Vector3(0, 700, 0);
            collider.Transform = Matrix4x4.CreateTranslation(offset);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(collider.TraceRay(start, end).Hit, Is.False, "the shape moved off the old ray");
                Assert.That(collider.TraceRay(start + offset, end + offset).Hit, Is.True);
                Assert.That(
                    collider.TraceRay(start + offset, end + offset).Distance,
                    Is.EqualTo(atOrigin.Distance).Within(1e-2f));
            }
        }

        [Test]
        public void RayTraceStopsShortOfAnOutOfReachShape()
        {
            var collider = LoadCollider();

            // The shape's near face sits around x = -33, so a ray ending well before it must miss
            var eye = new Vector3(-1000, 0, ProbeHeight);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(collider.TraceRay(eye, eye + new Vector3(80, 0, 0)).Hit, Is.False);
                Assert.That(collider.TraceRay(eye, eye + new Vector3(1000, 0, 0)).Hit, Is.True);
            }
        }

        [Test]
        public void BroadphaseRejectsSweepsThatCannotReach()
        {
            var collider = LoadCollider();

            var far = new Vector3(100000, 100000, 100000);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(collider.MightHit(far, far + Vector3.UnitX, PlayerHull), Is.False);
                Assert.That(
                    collider.MightHit(new Vector3(-1000, 0, ProbeHeight), new Vector3(1000, 0, ProbeHeight), PlayerHull),
                    Is.True);
            }
        }
    }
}
