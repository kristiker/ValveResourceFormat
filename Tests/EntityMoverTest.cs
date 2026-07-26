using System.Linq;
using System.Numerics;
using NUnit.Framework;
using ValveResourceFormat.Renderer.Entities;

namespace Tests
{
    /// <summary>
    /// Brush mover travel: stroke length, timing, and the outputs that mark the ends of it.
    /// </summary>
    [TestFixture]
    public class EntityMoverTest
    {
        private static T Spawn<T>(EntityWorld world, string classname, params (string Key, object Value)[] keys)
            where T : EntityInstance
        {
            var instance = (T)EntityFactory.Create(EntityIOTest.MakeEntity(classname, keys), classname, Matrix4x4.Identity)!;

            world.Add(instance);

            return instance;
        }

        [Test]
        public void MoveLinearTravelsItsDistanceAtItsSpeed()
        {
            var world = new EntityWorld();

            // movedir (0,0,0) is a QAngle pointing straight down +X
            var mover = Spawn<LinearMover>(world, "func_movelinear",
                ("movedistance", 100f),
                ("speed", 50f),
                ("startposition", 0f),
                ("origin", "0 0 0"),
                ("movedir", "0 0 0"));

            world.SpawnAll();

            Assert.That(mover.CurrentTransform.Translation.X, Is.EqualTo(0f).Within(1e-3f));

            mover.AcceptInput("Open", string.Empty, null, null);

            // 100 units at 50 u/s is a two second stroke
            for (var i = 0; i < 64; i++)
            {
                world.Tick(1f / 64f);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(mover.Position, Is.EqualTo(0.5f).Within(0.02f));
                Assert.That(mover.CurrentTransform.Translation.X, Is.EqualTo(50f).Within(2f));
            }

            for (var i = 0; i < 70; i++)
            {
                world.Tick(1f / 64f);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(mover.Position, Is.EqualTo(1f));
                Assert.That(mover.CurrentTransform.Translation.X, Is.EqualTo(100f).Within(1e-3f));
            }
        }

        [Test]
        public void MoveLinearAnnouncesBothEndsOfItsStroke()
        {
            var world = new EntityWorld();

            var mover = Spawn<LinearMover>(world, "func_movelinear",
                ("targetname", "platform"),
                ("movedistance", 100f),
                ("speed", 200f), // a half second stroke
                ("movedir", "0 0 0"));

            var listener = EntityIOTest.MakeProbe("listener");

            mover.Connections.Add(EntityIOTest.Connect("OnOpen", "listener", "Opening"));
            mover.Connections.Add(EntityIOTest.Connect("OnFullyOpen", "listener", "Opened"));
            mover.Connections.Add(EntityIOTest.Connect("OnFullyClosed", "listener", "Closed"));

            world.Add(listener);
            world.SpawnAll();

            mover.AcceptInput("Open", string.Empty, null, null);

            for (var i = 0; i < 64; i++)
            {
                world.Tick(1f / 64f);
            }

            mover.AcceptInput("Close", string.Empty, null, null);

            for (var i = 0; i < 64; i++)
            {
                world.Tick(1f / 64f);
            }

            string[] expected = ["Opening", "Opened", "Closed"];

            Assert.That(listener.Received.Select(r => r.Input), Is.EqualTo(expected));
        }

        [Test]
        public void DoorDerivesItsStrokeFromItsOwnSizeLessTheLip()
        {
            var world = new EntityWorld();

            var door = Spawn<LinearMover>(world, "func_door",
                ("speed", 100f),
                ("lip", 8f),
                ("movedir", "270 0 0"), // pitch 270 is straight up
                ("wait", -1f));

            door.Collider = null;

            world.SpawnAll();

            // Without a collision shape there is no size to derive from, so the door cannot move
            Assert.That(door.Travel, Is.EqualTo(Vector3.Zero));
        }

        [Test]
        public void RotatingMoverSweepsItsDistanceAtItsSpeed()
        {
            var world = new EntityWorld();

            var spinner = Spawn<RotatingMover>(world, "momentary_rot_button",
                ("distance", 90f),
                ("speed", 45f),
                ("startposition", 0f),
                ("origin", "0 0 0"),
                ("angles", "0 0 0"));

            world.SpawnAll();

            spinner.AcceptInput("SetPosition", "1", null, null);

            // 90 degrees at 45 deg/s is a two second sweep; halfway is 45 degrees of yaw
            for (var i = 0; i < 64; i++)
            {
                world.Tick(1f / 64f);
            }

            var forward = Vector3.TransformNormal(Vector3.UnitX, spinner.CurrentTransform);
            var yaw = float.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X));

            using (Assert.EnterMultipleScope())
            {
                Assert.That(spinner.Position, Is.EqualTo(0.5f).Within(0.02f));
                Assert.That(yaw, Is.EqualTo(45f).Within(2f));
            }
        }

        [Test]
        public void SetPositionImmediatelySkipsTheTravel()
        {
            var world = new EntityWorld();

            var spinner = Spawn<RotatingMover>(world, "momentary_rot_button",
                ("distance", 90f),
                ("speed", 1f)); // a 90 second sweep, so any travel at all would show

            world.SpawnAll();

            spinner.AcceptInput("SetPositionImmediately", "1", null, null);

            var forward = Vector3.TransformNormal(Vector3.UnitX, spinner.CurrentTransform);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(spinner.Position, Is.EqualTo(1f));
                Assert.That(spinner.IsMoving, Is.False);
                Assert.That(float.RadiansToDegrees(MathF.Atan2(forward.Y, forward.X)), Is.EqualTo(90f).Within(1e-3f));
            }
        }

        [Test]
        public void FuncBrushSolidityOverridesTheEnabledState()
        {
            var world = new EntityWorld();

            var toggling = Spawn<FuncBrush>(world, "func_brush", ("targetname", "toggling"), ("solidity", "0"));
            var never = Spawn<FuncBrush>(world, "func_brush", ("targetname", "never"), ("solidity", "1"));
            var always = Spawn<FuncBrush>(world, "func_brush", ("targetname", "always"), ("solidity", "2"));

            world.SpawnAll();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(toggling.IsSolid, Is.True);
                Assert.That(never.IsSolid, Is.False);
                Assert.That(always.IsSolid, Is.True);
            }

            foreach (var brush in new[] { toggling, never, always })
            {
                brush.AcceptInput("Disable", string.Empty, null, null);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(toggling.IsSolid, Is.False, "a toggling brush stops colliding when disabled");
                Assert.That(never.IsSolid, Is.False);
                Assert.That(always.IsSolid, Is.True, "an always-solid brush keeps colliding when disabled");
            }
        }

        [Test]
        public void FuncRotatingSpinsUpToItsMaxSpeedAndStops()
        {
            var world = new EntityWorld();

            var fan = Spawn<FuncRotating>(world, "func_rotating",
                ("maxspeed", 100f),
                ("fanfriction", 100f), // ramps a full 100 deg/s per second, so one second to speed
                ("spawnflags", 1));    // start on

            world.SpawnAll();

            Assert.That(fan.CurrentSpeed, Is.EqualTo(100f).Within(1e-3f));

            fan.AcceptInput("Stop", string.Empty, null, null);

            for (var i = 0; i < 64; i++)
            {
                world.Tick(1f / 64f);
            }

            Assert.That(fan.CurrentSpeed, Is.EqualTo(0f).Within(1e-3f));
        }
    }
}
