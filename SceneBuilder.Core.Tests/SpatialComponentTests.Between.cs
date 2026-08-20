using System.Collections.Generic;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // .Between(...) parse/emit round-trip pins, split into a sibling partial to keep
    // SpatialComponentTests.cs under the file-size budget.
    public partial class SpatialComponentTests
    {
        private const string BetweenWorldSource = @"
public class BetweenWorldScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var a = scene.Add(""A"");
        var b = scene.Add(""B"");
        scene.Add(""Crate"").Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X);
    }
}
";

        private const string BetweenOrientedSource = @"
public class BetweenOrientedScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var a = scene.Add(""A"");
        var b = scene.Add(""B"");
        var root = scene.Add(""Root"");
        scene.Add(""Crate"").Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X, alongOrientationOf: root);
    }
}
";

        private const string BetweenPositionalSource = @"
public class BetweenPositionalScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var a = scene.Add(""A"");
        var b = scene.Add(""B"");
        scene.Add(""Crate"").Between(a, b, 0.25f, Between.Axis.X);
    }
}
";

        private const string BetweenIdempotentSource = @"
public class BetweenIdempotentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var from = scene.Add(""From"");
        var to = scene.Add(""To"");
        var crate = scene.Add(""Crate"");
        crate.Between(from: from, to: to, fraction: 0.5f, axis: Between.Axis.Y);
    }
}
";

        // Pins the mapped field shapes (ObjectRef for from/to, Float for fraction, the CANONICAL
        // Between.Axis enum shape for axis) that the parse arm must produce from a world (no
        // alongOrientationOf) call.
        [Fact]
        public void Parse_Between_World_ProducesMappedFieldsAndCanonicalAxisEnum()
        {
            var result = BuilderParser.Parse(BetweenWorldSource);

            var a = Assert.Single(result.Model.Roots, r => r.Name == "A");
            var b = Assert.Single(result.Model.Roots, r => r.Name == "B");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            Assert.Equal(SpatialComponents.BetweenTypeName, component.Type.FullName);

            var from = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.BetweenFields.From]);
            Assert.Equal(a.LogicalId, from.TargetLogicalId);

            var to = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.BetweenFields.To]);
            Assert.Equal(b.LogicalId, to.TargetLogicalId);

            Assert.Equal(ValueNode.Primitive.Float(0.25f), component.Fields[SpatialComponents.BetweenFields.Fraction]);
            Assert.Equal(
                new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.X }, false),
                component.Fields[SpatialComponents.BetweenFields.Axis]);

            Assert.False(component.Fields.ContainsKey(SpatialComponents.BetweenFields.Orientation));
        }

        // World-axis Between drives exactly the one position bit its axis names, via
        // SpatialComponents.BetweenDrivenMask — never open-coded in the parse arm.
        [Fact]
        public void Parse_Between_World_SetsSingleAxisDrivenChannel()
        {
            var result = BuilderParser.Parse(BetweenWorldSource);

            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");

            Assert.Equal(ChannelMask.PositionX, crate.Transform.DrivenChannels);
        }

        // `alongOrientationOf:` present drives the full position mask (all three world axes can move
        // once the corridor follows a tilted reference frame), and the Orientation field is populated.
        [Fact]
        public void Parse_Between_Oriented_SetsFullPositionDrivenChannel()
        {
            var result = BuilderParser.Parse(BetweenOrientedSource);

            var root = Assert.Single(result.Model.Roots, r => r.Name == "Root");
            var crate = Assert.Single(result.Model.Roots, r => r.Name == "Crate");
            var component = Assert.Single(crate.Components);

            var orientation = Assert.IsType<ValueNode.ObjectRef>(component.Fields[SpatialComponents.BetweenFields.Orientation]);
            Assert.Equal(root.LogicalId, orientation.TargetLogicalId);

            Assert.Equal(
                ChannelMask.PositionX | ChannelMask.PositionY | ChannelMask.PositionZ,
                crate.Transform.DrivenChannels);
        }

        // Appending a Between component renders the dedicated `.Between(...)` call, fixed argument
        // order (from, to, fraction, axis), with `alongOrientationOf:` omitted for the world case.
        // The axis token must be the authoring form `Between.Axis.X` — never the runtime nested-type
        // FullName (the '+' separator is not valid C#).
        [Fact]
        public void Emit_AppendBetween_RendersCanonicalCall_ByteStable()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.From, new ValueNode.ObjectRef("a")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.To, new ValueNode.ObjectRef("b")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ValueNode.Primitive.Float(0.25f)),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.BetweenFields.Axis,
                    new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.X }, false)),
            });
            var fieldExpressions = new Dictionary<string, string>
            {
                [SpatialComponents.BetweenFields.From] = "a",
                [SpatialComponents.BetweenFields.To] = "b",
            };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.BetweenTypeName, fields, fieldExpressions);

            Assert.Equal("crate.Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X);", text);
            Assert.DoesNotContain("+", text);
            Assert.DoesNotContain("SceneBuilder.Authoring", text);
            Assert.DoesNotContain("alongOrientationOf", text);
        }

        // Oriented case: `alongOrientationOf:` is present and rendered as the pre-resolved handle,
        // appended AFTER axis (fixed order), never in place of `axis:`.
        [Fact]
        public void Emit_BetweenOriented_EmitsAlongOrientationOf()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.From, new ValueNode.ObjectRef("a")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.To, new ValueNode.ObjectRef("b")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ValueNode.Primitive.Float(0.25f)),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.BetweenFields.Axis,
                    new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.X }, false)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Orientation, new ValueNode.ObjectRef("r")),
            });
            var fieldExpressions = new Dictionary<string, string>
            {
                [SpatialComponents.BetweenFields.From] = "a",
                [SpatialComponents.BetweenFields.To] = "b",
                [SpatialComponents.BetweenFields.Orientation] = "r",
            };

            var text = SpatialComponentSource.RenderStatement("crate", SpatialComponents.BetweenTypeName, fields, fieldExpressions);

            Assert.Equal("crate.Between(from: a, to: b, fraction: 0.25f, axis: Between.Axis.X, alongOrientationOf: r);", text);
        }

        // `fraction: 0f` and an index-0 `axis` are real authored values, not "unset" — the emit-side
        // default-omission pass must exempt Between's fraction/axis so they are never dropped as
        // matching a constructed default, or placement silently disappears from source.
        [Fact]
        public void Emit_BetweenFractionZeroAndAxisX_SurviveDefaultPruning()
        {
            var defaultTemplate = new ComponentData
            {
                LogicalId = "template",
                Type = new TypeRef(SpatialComponents.BetweenTypeName),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ValueNode.Primitive.Float(0f)),
                    new KeyValuePair<string, ValueNode>(
                        SpatialComponents.BetweenFields.Axis,
                        new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.X }, false)),
                }),
            };
            var defaults = ComponentDefaultOmission.Index.Build(new[] { defaultTemplate });

            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.From, new ValueNode.ObjectRef("a")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.To, new ValueNode.ObjectRef("b")),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ValueNode.Primitive.Float(0f)),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.BetweenFields.Axis,
                    new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.X }, false)),
            });

            var conflicts = new List<Conflict>();
            var kept = ComponentDefaultOmission.OmitDefaults(SpatialComponents.BetweenTypeName, fields, defaults, "between-1", conflicts);

            Assert.True(kept.ContainsKey(SpatialComponents.BetweenFields.Fraction));
            Assert.True(kept.ContainsKey(SpatialComponents.BetweenFields.Axis));
        }

        // A positional-arg `.Between(a, b, 0.25f, Between.Axis.X)` is REJECTED — Between is
        // named-args-only, mirroring FitSize/SurfaceSnap.
        [Fact]
        public void Parse_Between_PositionalArgs_Rejected()
        {
            Assert.Throws<ParseException>(() => BuilderParser.Parse(BetweenPositionalSource));
        }

        // Parse -> reconcile against an unchanged live snapshot converges with no edits/conflicts —
        // the field mapping and the driven-channel mask agree with what the reconciler expects.
        [Fact]
        public void RoundTrip_Between_Idempotent()
        {
            var parsed = BuilderParser.Parse(BetweenIdempotentSource);
            var from = Assert.Single(parsed.Model.Roots, r => r.Name == "From");
            var to = Assert.Single(parsed.Model.Roots, r => r.Name == "To");
            var crate = Assert.Single(parsed.Model.Roots, r => r.Name == "Crate");
            var between = Assert.Single(crate.Components);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = from.LogicalId, GlobalObjectId = "goid-from", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = to.LogicalId, GlobalObjectId = "goid-to", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = crate.LogicalId, GlobalObjectId = "goid-crate", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = between.LogicalId,
                        GlobalObjectId = "",
                        Kind = "Component",
                        ComponentType = SpatialComponents.BetweenTypeName,
                        ParentLogicalId = crate.LogicalId,
                    },
                },
            };

            var betweenFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.From, new ValueNode.ObjectRef(from.LogicalId)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.To, new ValueNode.ObjectRef(to.LogicalId)),
                new KeyValuePair<string, ValueNode>(SpatialComponents.BetweenFields.Fraction, ValueNode.Primitive.Float(0.5f)),
                new KeyValuePair<string, ValueNode>(
                    SpatialComponents.BetweenFields.Axis,
                    new ValueNode.Enum(SpatialComponents.BetweenEnums.AxisTypeName, new[] { SpatialComponents.BetweenEnums.Y }, false)),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-from", Name = "From" },
                    new SnapshotNode { GlobalObjectId = "goid-to", Name = "To" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-crate",
                        Name = "Crate",
                        Transform = new TransformData { DrivenChannels = ChannelMask.PositionY },
                        Components = new[]
                        {
                            new ComponentData { LogicalId = "unused", Type = new TypeRef(SpatialComponents.BetweenTypeName), Fields = betweenFields },
                        },
                    },
                },
            };

            var result = Reconciler.Reconcile(parsed.Model, snapshot, map, parsed.Anchors);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
        }
    }
}
