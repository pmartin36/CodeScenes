using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Regression: a component present in SOURCE but with no Kind="Component" IdentityMap entry
    // (the pre-first-build window: a scene->code sync before the code->scene build that would
    // register the Component entry) must be authored ONCE. The mapped-owner ADD pass used to emit
    // an AppendComponentStatement AND the field-value diff pass an IntroduceComponentField for the
    // SAME in-source component, so the applied source authored it twice.
    public class DoubleEmitReconcileTests
    {
        [Fact]
        public void Reconcile_InSourceComponentWithNoManagedEntry_IntroducesFieldWithoutSecondAppend()
        {
            const string source = @"
public class SceneD1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"").Component<Game.DoorOpener>();
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            // Owner is mapped as a GameObject, but there is NO Component entry for DoorOpener.
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef("Game.DoorOpener"),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("speed", ValueNode.Primitive.Int(7)) }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            // The field-value diff pass owns authoring the in-source component's `speed` field in
            // place. The ADD pass must NOT also append a second `.Component<DoorOpener>()` statement.
            Assert.Empty(result.Patch.Edits.OfType<AppendComponentStatement>());
            var introduce = Assert.Single(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Equal("speed", introduce.FieldKey);
        }

        [Fact]
        public void Reconcile_NewComponentAlongsideInSourceComponent_StillAppendsTheNewOne()
        {
            // Same owner carries an in-source DoorOpener AND a genuinely new (not-in-source) Rigidbody.
            // The in-source one must not append; the new one must still append (no over-suppression).
            const string source = @"
public class SceneD1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var opener = scene.Add(""Opener"").Component<Game.DoorOpener>();
    }
}
";
            var parsed = BuilderParser.Parse(source);
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "u1",
                                Type = new TypeRef("Game.DoorOpener"),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("speed", ValueNode.Primitive.Int(7)) }),
                            },
                            new ComponentData
                            {
                                LogicalId = "u2",
                                Type = new TypeRef("UnityEngine.Rigidbody"),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)) }),
                            },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal("UnityEngine.Rigidbody", append.TypeFullName);
        }
    }
}
