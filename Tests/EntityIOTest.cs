using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ValveResourceFormat;
using ValveResourceFormat.Renderer.Entities;
using ValveResourceFormat.ResourceTypes;

namespace Tests
{
    /// <summary>
    /// Entity I/O dispatch: name resolution, delays, and the fire budget.
    /// </summary>
    [TestFixture]
    public class EntityIOTest
    {
        /// <summary>Counts the inputs it receives, so a chain can be asserted on its far end.</summary>
        internal sealed class Probe : EntityInstance
        {
            public List<(string Input, string Parameter, string? Activator)> Received { get; } = [];

            public override bool AcceptInput(string input, string parameter, EntityInstance? activator, EntityInstance? caller)
            {
                Received.Add((input, parameter, activator?.TargetName));
                return true;
            }
        }

        internal static EntityLump.Entity MakeEntity(string classname, params (string Key, object Value)[] keys)
        {
            var entity = new EntityLump.Entity { ParentLump = new EntityLump { Resource = null! } };

            entity.Add("classname", classname);

            foreach (var (key, value) in keys)
            {
                entity.Add(key, value switch
                {
                    string s => s,
                    int i => i,
                    float f => f,
                    bool b => b,
                    _ => throw new System.NotSupportedException($"Unhandled keyvalue type for '{key}'"),
                });
            }

            return entity;
        }

        internal static Probe MakeProbe(string? targetName)
        {
            var probe = new Probe();
            var keys = targetName == null ? [] : new[] { ("targetname", (object)targetName) };

            probe.Initialize(MakeEntity("probe", keys), "probe", System.Numerics.Matrix4x4.Identity);

            return probe;
        }

        internal static EntityConnection Connect(string output, string target, string input, float delay = 0f, int timesToFire = -1, string parameter = "")
            => new()
            {
                OutputName = output,
                TargetType = EntityIOTargetType.EntityNameOrClassName,
                TargetName = target,
                InputName = input,
                OverrideParam = parameter,
                Delay = delay,
                TimesToFire = timesToFire,
            };

        [Test]
        public void DeliversInputAfterDelay()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var target = MakeProbe("target");

            source.Connections.Add(Connect("OnFire", "target", "Go", delay: 0.5f));

            world.Add(source);
            world.Add(target);
            world.SpawnAll();

            world.SendOutput(source, "OnFire", null);

            world.Tick(0.25f);
            Assert.That(target.Received, Is.Empty, "input arrived before its delay elapsed");

            world.Tick(0.30f);
            string[] expected = ["Go"];
            Assert.That(target.Received.Select(r => r.Input), Is.EqualTo(expected));
        }

        [Test]
        public void ZeroDelayInputArrivesOnTheSameTick()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var target = MakeProbe("target");

            source.Connections.Add(Connect("OnFire", "target", "Go"));

            world.Add(source);
            world.Add(target);
            world.SpawnAll();

            world.SendOutput(source, "OnFire", null);
            world.Tick(1f / 64f);

            Assert.That(target.Received, Has.Count.EqualTo(1));
        }

        [Test]
        public void StopsFiringOnceTheBudgetIsSpent()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var target = MakeProbe("target");

            source.Connections.Add(Connect("OnFire", "target", "Go", timesToFire: 2));

            world.Add(source);
            world.Add(target);
            world.SpawnAll();

            for (var i = 0; i < 5; i++)
            {
                world.SendOutput(source, "OnFire", null);
                world.Tick(1f / 64f);
            }

            Assert.That(target.Received, Has.Count.EqualTo(2));
        }

        [Test]
        public void ResolvesSelfActivatorAndPlayer()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var activator = MakeProbe("theactivator");
            var player = MakeProbe("theplayer");

            source.Connections.Add(Connect("OnFire", "!self", "Self"));
            source.Connections.Add(Connect("OnFire", "!activator", "Activator"));
            source.Connections.Add(Connect("OnFire", "!player", "Player"));

            world.Add(source);
            world.Add(activator);
            world.Add(player);
            world.Player = player;
            world.SpawnAll();

            world.SendOutput(source, "OnFire", activator);
            world.Tick(1f / 64f);

            string[] self = ["Self"];
            string[] byActivator = ["Activator"];
            string[] byPlayer = ["Player"];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(source.Received.Select(r => r.Input), Is.EqualTo(self));
                Assert.That(activator.Received.Select(r => r.Input), Is.EqualTo(byActivator));
                Assert.That(player.Received.Select(r => r.Input), Is.EqualTo(byPlayer));
            }
        }

        [Test]
        public void FallsBackToClassnameThenWildcard()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var unnamed = MakeProbe(null);
            var prefixedA = MakeProbe("stage_a");
            var prefixedB = MakeProbe("stage_b");

            world.Add(source);
            world.Add(unnamed);
            world.Add(prefixedA);
            world.Add(prefixedB);
            world.SpawnAll();

            // "probe" is nobody's targetname, so it resolves as a classname and reaches all four
            world.QueueInput("probe", "ByClass");
            world.QueueInput("stage_*", "ByWildcard");
            world.Tick(1f / 64f);

            string[] classOnly = ["ByClass"];
            string[] classAndWildcard = ["ByClass", "ByWildcard"];

            using (Assert.EnterMultipleScope())
            {
                Assert.That(unnamed.Received.Select(r => r.Input), Is.EqualTo(classOnly));
                Assert.That(prefixedA.Received.Select(r => r.Input), Is.EqualTo(classAndWildcard));
                Assert.That(prefixedB.Received.Select(r => r.Input), Is.EqualTo(classAndWildcard));
            }
        }

        [Test]
        public void PassesTheOverrideParameterAndActivatorDownTheChain()
        {
            var world = new EntityWorld();
            var source = MakeProbe("source");
            var activator = MakeProbe("who");
            var target = MakeProbe("target");

            source.Connections.Add(Connect("OnFire", "target", "SetValue", parameter: "42"));

            world.Add(source);
            world.Add(activator);
            world.Add(target);
            world.SpawnAll();

            world.SendOutput(source, "OnFire", activator);
            world.Tick(1f / 64f);

            (string, string, string?)[] expected = [("SetValue", "42", "who")];
            Assert.That(target.Received, Is.EqualTo(expected));
        }

        [Test]
        public void LogicAutoFiresOnMapSpawnOnce()
        {
            var world = new EntityWorld();
            var auto = (LogicAuto)EntityFactory.Create(MakeEntity("logic_auto"), "logic_auto", System.Numerics.Matrix4x4.Identity)!;
            var target = MakeProbe("target");

            auto.Connections.Add(Connect("OnMapSpawn", "target", "Go"));

            world.Add(auto);
            world.Add(target);
            world.SpawnAll();

            for (var i = 0; i < 5; i++)
            {
                world.Tick(1f / 64f);
            }

            Assert.That(target.Received, Has.Count.EqualTo(1));
        }

        [Test]
        public void LogicTimerRepeatsOnItsInterval()
        {
            var world = new EntityWorld();
            var timer = (LogicTimer)EntityFactory.Create(
                MakeEntity("logic_timer", ("refiretime", 1f), ("initialdelay", 1f)),
                "logic_timer",
                System.Numerics.Matrix4x4.Identity)!;
            var target = MakeProbe("target");

            timer.Connections.Add(Connect("OnTimer", "target", "Tick"));

            world.Add(timer);
            world.Add(target);
            world.SpawnAll();

            // 3.5 seconds of simulation: fires at t=1, 2 and 3, with the next one well past the end
            for (var i = 0; i < 70; i++)
            {
                world.Tick(0.05f);
            }

            Assert.That(target.Received, Has.Count.EqualTo(3));
        }
    }
}
