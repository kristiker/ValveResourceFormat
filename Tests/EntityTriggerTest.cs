using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    /// <summary>
    /// Trigger volumes: the overlap test against the player hull, the touch outputs the transitions
    /// fire, and what teleport and push volumes do to the player once touched.
    /// </summary>
    [TestFixture]
    public class EntityTriggerTest
    {
        private static readonly Vector3 PlayerHull = new(16, 16, 36);

        /// <summary>Inside the fixture shape, whose hull spans roughly x -33..30, y ±32, z -5..307.</summary>
        private static readonly Vector3 InsideVolume = new(0, 0, 150);

        private static readonly Vector3 OutsideVolume = new(5000, 0, 150);

        /// <summary>Stands in for the movement controller so a test can watch what a trigger does to it.</summary>
        private sealed class FakeController : PlayerEntity.IPlayerController
        {
            public Vector3 HullCenter { get; set; }
            public Vector3 HullHalfExtents { get; set; } = PlayerHull;
            public Vector3 Velocity { get; set; }
            public float ViewYawDegrees { get; set; }

            public List<Vector3> BasePushes { get; } = [];
            public int Teleports { get; private set; }

            public void Teleport(Vector3 feetPosition, float? yawDegrees, Vector3? velocity)
            {
                Teleports++;
                HullCenter = feetPosition + new Vector3(0, 0, HullHalfExtents.Z);

                if (yawDegrees.HasValue)
                {
                    ViewYawDegrees = yawDegrees.Value;
                }

                if (velocity.HasValue)
                {
                    Velocity = velocity.Value;
                }
            }

            public void AddBaseVelocity(Vector3 velocity) => BasePushes.Add(velocity);
        }

        private static PhysAggregateData LoadPhysics()
        {
            var file = Path.Combine(TestContext.CurrentContext.TestDirectory, "Files", "generic_grip.vphys_c");

            using var resource = new Resource { FileName = file };
            resource.Read(file);

            return (PhysAggregateData)resource.DataBlock!;
        }

        private static T SpawnTrigger<T>(EntityWorld world, string classname, params (string Key, object Value)[] keys)
            where T : TriggerEntity
        {
            var trigger = (T)EntityFactory.Create(EntityIOTest.MakeEntity(classname, keys), classname, Matrix4x4.Identity)!;

            trigger.Collider = new EntityCollider(LoadPhysics());
            world.Add(trigger);

            return trigger;
        }

        private static (EntityWorld World, FakeController Controller) MakeWorldWithPlayer()
        {
            var world = new EntityWorld();
            var controller = new FakeController { HullCenter = OutsideVolume };

            world.Player = EntityFactory.CreatePlayer(controller);
            world.Add(world.Player);

            return (world, controller);
        }

        [Test]
        public void FiresStartAndEndTouchOnTheTransitions()
        {
            var (world, controller) = MakeWorldWithPlayer();
            var trigger = SpawnTrigger<TriggerMultiple>(world, "trigger_multiple", ("wait", 0.1f));
            var listener = EntityIOTest.MakeProbe("listener");

            trigger.Connections.Add(EntityIOTest.Connect("OnStartTouch", "listener", "Enter"));
            trigger.Connections.Add(EntityIOTest.Connect("OnEndTouch", "listener", "Leave"));

            world.Add(listener);
            world.SpawnAll();

            string[] entered = ["Enter"];
            string[] enteredAndLeft = ["Enter", "Leave"];

            void Step()
            {
                world.Tick(1f / 64f);
                world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
                world.Tick(1f / 64f); // deliver the queued inputs
            }

            Step();
            Assert.That(listener.Received, Is.Empty, "the player started outside the volume");

            controller.HullCenter = InsideVolume;
            Step();
            Step();
            Assert.That(listener.Received.Select(r => r.Input), Is.EqualTo(entered), "start touch should fire once");

            controller.HullCenter = OutsideVolume;
            Step();
            Assert.That(listener.Received.Select(r => r.Input), Is.EqualTo(enteredAndLeft));
        }

        [Test]
        public void DisabledTriggerDoesNotTouch()
        {
            var (world, controller) = MakeWorldWithPlayer();
            var trigger = SpawnTrigger<TriggerMultiple>(world, "trigger_multiple", ("startdisabled", true));
            var listener = EntityIOTest.MakeProbe("listener");

            trigger.Connections.Add(EntityIOTest.Connect("OnStartTouch", "listener", "Enter"));

            world.Add(listener);
            world.SpawnAll();

            controller.HullCenter = InsideVolume;
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            world.Tick(1f / 64f);

            Assert.That(listener.Received, Is.Empty);

            trigger.AcceptInput("Enable", string.Empty, null, null);
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            world.Tick(1f / 64f);

            Assert.That(listener.Received, Has.Count.EqualTo(1));
        }

        [Test]
        public void TeleportMovesThePlayerToItsDestinationKeepingSpeed()
        {
            var (world, controller) = MakeWorldWithPlayer();

            var destination = EntityFactory.Create(
                EntityIOTest.MakeEntity("info_teleport_destination",
                    ("targetname", "stage2"),
                    ("origin", "1000 2000 300"),
                    ("angles", "0 90 0")),
                "info_teleport_destination",
                Matrix4x4.Identity)!;

            world.Add(destination);
            SpawnTrigger<TriggerTeleport>(world, "trigger_teleport", ("target", "stage2"));

            world.SpawnAll();

            controller.Velocity = new Vector3(900, 0, -200);
            controller.HullCenter = InsideVolume;

            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(controller.Teleports, Is.EqualTo(1));

                // Feet land on the destination, lifted a unit clear of the floor
                Assert.That(controller.HullCenter.X, Is.EqualTo(1000f).Within(1e-3f));
                Assert.That(controller.HullCenter.Y, Is.EqualTo(2000f).Within(1e-3f));
                Assert.That(controller.HullCenter.Z, Is.EqualTo(300f + 1f + PlayerHull.Z).Within(1e-3f));

                // Speed survives, which is the whole point on a movement map
                Assert.That(controller.Velocity, Is.EqualTo(new Vector3(900, 0, -200)));
                Assert.That(controller.ViewYawDegrees, Is.EqualTo(90f).Within(1e-3f));
            }
        }

        [Test]
        public void TeleportOnlyFiresOnEntryNotWhileStandingInside()
        {
            var (world, controller) = MakeWorldWithPlayer();

            var destination = EntityFactory.Create(
                EntityIOTest.MakeEntity("info_target", ("targetname", "dest"), ("origin", "0 0 150")),
                "info_target",
                Matrix4x4.Identity)!;

            world.Add(destination);
            SpawnTrigger<TriggerTeleport>(world, "trigger_teleport", ("target", "dest"));

            world.SpawnAll();

            controller.HullCenter = InsideVolume;

            for (var i = 0; i < 5; i++)
            {
                world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            }

            Assert.That(controller.Teleports, Is.EqualTo(1), "the destination is inside the volume, so touch must not re-fire");
        }

        [Test]
        public void PushImposesItsSpeedAlongPushDirEveryTickInside()
        {
            var (world, controller) = MakeWorldWithPlayer();

            // pushdir yaw 90 points along +Y
            var push = SpawnTrigger<TriggerPush>(world, "trigger_push", ("pushdir", "0 90 0"), ("speed", 1200f));

            world.SpawnAll();

            Assert.That(push.PushVelocity.Y, Is.EqualTo(1200f).Within(1e-2f));

            controller.HullCenter = InsideVolume;

            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(controller.BasePushes, Has.Count.EqualTo(2), "a continuous push applies on every tick inside");
                Assert.That(controller.Velocity, Is.EqualTo(Vector3.Zero), "a continuous push must never enter the player's own velocity");
            }

            controller.HullCenter = OutsideVolume;
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);

            Assert.That(controller.BasePushes, Has.Count.EqualTo(2), "leaving the volume drops the push");
        }

        [Test]
        public void OnceOnlyPushIsAnImpulseOnEntry()
        {
            var (world, controller) = MakeWorldWithPlayer();

            SpawnTrigger<TriggerPush>(world, "trigger_push",
                ("pushdir", "270 0 0"), // pitch 270 is straight up
                ("speed", 500f),
                ("spawnflags", 128));

            world.SpawnAll();

            controller.HullCenter = InsideVolume;

            for (var i = 0; i < 5; i++)
            {
                world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(controller.Velocity.Z, Is.EqualTo(500f).Within(1e-2f));
                Assert.That(controller.BasePushes, Is.Empty);
            }
        }

        [Test]
        public void TriggerFollowsItsBrushWhenTheEntityMoves()
        {
            var (world, controller) = MakeWorldWithPlayer();
            var trigger = SpawnTrigger<TriggerMultiple>(world, "trigger_multiple");

            world.SpawnAll();

            controller.HullCenter = InsideVolume;
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);
            Assert.That(trigger.Touching, Is.True);

            trigger.Collider!.Transform = Matrix4x4.CreateTranslation(new Vector3(0, 0, 10000));
            world.UpdateTriggerTouch(controller.HullCenter, controller.HullHalfExtents);

            Assert.That(trigger.Touching, Is.False, "the volume moved out from under the player");
        }
    }
}
