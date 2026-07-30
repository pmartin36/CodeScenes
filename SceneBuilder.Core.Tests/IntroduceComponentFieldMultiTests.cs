using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // TWO (or more) previously-unauthored fields harvested from the scene in ONE sync must ALL land
    // in the source in a SINGLE pass, whatever closure form the component statement was authored in:
    // no argument list at all, an expression-bodied `c => c.Set(...)`, or a block-bodied closure.
    //
    // Every IntroduceComponentField for one component rewrites the SAME `.Component<T>(...)` call, so
    // each edit's applier must read the CURRENT shape of that call rather than the shape resolved
    // against the original tree.
    public class IntroduceComponentFieldMultiTests
    {
        private static (ParseResult Parsed, IdentityMap Map) ParseWithMappedRootAndComponent(
            string source, string componentTypeFullName)
        {
            var parsed = BuilderParser.Parse(source);
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;
            var componentLogicalId = $"{ownerLogicalId}/{componentTypeFullName}#0";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = componentTypeFullName,
                        ParentLogicalId = ownerLogicalId,
                    },
                },
            };

            return (parsed, map);
        }

        private static SceneSnapshot SnapshotWith(string componentTypeFullName, params KeyValuePair<string, ValueNode>[] fields)
            => new()
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "NreProbe",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(componentTypeFullName),
                                Fields = new FieldMap(fields),
                            },
                        },
                    },
                },
            };

        private static KeyValuePair<string, ValueNode> F(string key, ValueNode value) => new(key, value);

        private static string ReconcileAndApply(
            string source, string componentTypeFullName, SceneSnapshot snapshot, int expectedIntroductions)
        {
            var (parsed, map) = ParseWithMappedRootAndComponent(source, componentTypeFullName);

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            Assert.Equal(expectedIntroductions, result.Patch.Edits.OfType<IntroduceComponentField>().Count());
            Assert.Empty(result.Conflicts);

            return SourcePatchApplier.Apply(source, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
        }

        // The live-editor repro: an EXPRESSION-BODIED closure taking two introductions at once.
        private const string SphereColliderSource = @"
public class Scene1 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""NreProbe"").Component<UnityEngine.SphereCollider>(c => c.Set(""m_Radius"", 2f));
    }
}
";

        [Fact]
        public void Apply_TwoIntroductionsIntoExpressionBodiedClosure_BothFieldsLandInOnePass()
        {
            var snapshot = SnapshotWith(
                "UnityEngine.SphereCollider",
                F("m_Radius", ValueNode.Primitive.Float(2f)),
                F("m_IsTrigger", ValueNode.Primitive.Bool(true)),
                F("m_Center", new ValueNode.Vec3(new Vec3(1f, 2f, 3f))));

            var patched = ReconcileAndApply(SphereColliderSource, "UnityEngine.SphereCollider", snapshot, 2);

            Assert.Contains("\"m_Radius\", 2f", patched);
            Assert.Contains("\"m_IsTrigger\", true", patched);
            Assert.Contains("\"m_Center\"", patched);
        }

        // A second sync over the applied source must be a no-op: proof the introductions landed in a
        // form the parser reads back, not just as text that happens to contain the key.
        [Fact]
        public void Apply_TwoIntroductionsIntoExpressionBodiedClosure_ReparsesAndConverges()
        {
            var snapshot = SnapshotWith(
                "UnityEngine.SphereCollider",
                F("m_Radius", ValueNode.Primitive.Float(2f)),
                F("m_IsTrigger", ValueNode.Primitive.Bool(true)),
                F("m_Center", new ValueNode.Vec3(new Vec3(1f, 2f, 3f))));

            var patched = ReconcileAndApply(SphereColliderSource, "UnityEngine.SphereCollider", snapshot, 2);

            var (reparsed, reparsedMap) = ParseWithMappedRootAndComponent(patched, "UnityEngine.SphereCollider");
            var component = Assert.Single(Assert.Single(reparsed.Model.Roots).Components);
            Assert.Equal(ValueNode.Primitive.Float(2f), component.Fields["m_Radius"]);
            Assert.Equal(ValueNode.Primitive.Bool(true), component.Fields["m_IsTrigger"]);
            Assert.Equal(new ValueNode.Vec3(new Vec3(1f, 2f, 3f)), component.Fields["m_Center"]);

            var second = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsedMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans);

            Assert.Empty(second.Patch.Edits);
            Assert.Empty(second.Conflicts);
        }

        // Not a two-only fix: the applier must survive an arbitrary number of introductions onto the
        // same expression-bodied closure.
        [Fact]
        public void Apply_ThreeIntroductionsIntoExpressionBodiedClosure_AllFieldsLandInOnePass()
        {
            var snapshot = SnapshotWith(
                "UnityEngine.SphereCollider",
                F("m_Radius", ValueNode.Primitive.Float(2f)),
                F("m_IsTrigger", ValueNode.Primitive.Bool(true)),
                F("m_Center", new ValueNode.Vec3(new Vec3(1f, 2f, 3f))),
                F("m_Enabled", ValueNode.Primitive.Bool(false)));

            var patched = ReconcileAndApply(SphereColliderSource, "UnityEngine.SphereCollider", snapshot, 3);

            Assert.Contains("\"m_Radius\", 2f", patched);
            Assert.Contains("\"m_IsTrigger\", true", patched);
            Assert.Contains("\"m_Center\"", patched);
            Assert.Contains("\"m_Enabled\", false", patched);
        }

        // The zero-argument form `.Component<T>()`: both introductions rewrite the same empty
        // argument list, so the second used to overwrite the first and one edit was silently dropped
        // (the sync still reported it as applied).
        [Fact]
        public void Apply_TwoIntroductionsIntoZeroArgComponentCall_BothFieldsLandInOnePass()
        {
            const string source = @"
public class Scene2 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""NreProbe"").Component<UnityEngine.SphereCollider>();
    }
}
";
            var snapshot = SnapshotWith(
                "UnityEngine.SphereCollider",
                F("m_IsTrigger", ValueNode.Primitive.Bool(true)),
                F("m_Radius", ValueNode.Primitive.Float(2f)));

            var patched = ReconcileAndApply(source, "UnityEngine.SphereCollider", snapshot, 2);

            Assert.Contains("\"m_IsTrigger\", true", patched);
            Assert.Contains("\"m_Radius\", 2f", patched);
        }

        // Control: the block-bodied form already took two introductions; it must keep doing so.
        [Fact]
        public void Apply_TwoIntroductionsIntoBlockBodiedClosure_BothFieldsLandInOnePass()
        {
            const string source = @"
public class Scene3 : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        scene.Add(""NreProbe"").Component<UnityEngine.Rigidbody>(c => { c.Set(""m_Mass"", 5f); });
    }
}
";
            var snapshot = SnapshotWith(
                "UnityEngine.Rigidbody",
                F("m_Mass", ValueNode.Primitive.Float(5f)),
                F("m_LinearDamping", ValueNode.Primitive.Float(0.5f)),
                F("m_AngularDamping", ValueNode.Primitive.Float(0.25f)));

            var patched = ReconcileAndApply(source, "UnityEngine.Rigidbody", snapshot, 2);

            Assert.Contains("\"m_Mass\", 5f", patched);
            Assert.Contains("\"m_LinearDamping\", 0.5f", patched);
            Assert.Contains("\"m_AngularDamping\", 0.25f", patched);
        }
    }
}
