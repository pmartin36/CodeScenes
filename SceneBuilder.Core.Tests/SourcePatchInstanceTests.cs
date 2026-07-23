using System.Linq;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // m10-b4-t1: SourcePatchApplier appends/drops instance-root override-authoring calls
    // (.Override/.AddComponent/.RemoveComponent) onto an existing `scene.Instance(...)` chain,
    // rendering as the exact inverse of BuilderParser.Instance.cs's parse (idempotent re-parse).
    public class SourcePatchInstanceTests
    {
        private const string PlainInstanceFixture = @"
public class PlainInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";

        [Fact]
        public void Apply_AppendInstanceOverride_SelectorForm_RendersInverseOfParse_AndReparses()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = "Health",
                                PropertyPath = "member:health",
                                Value = ValueNode.Primitive.Int(50),
                            },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                "        var enemy = scene.Instance(\"Assets/Prefabs/Enemy.prefab\").Override(e => e.Set((Health x) => x.health, 50));\n",
                result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);
            Assert.Equal("member:health", propertyOverride.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Int(50), propertyOverride.Value);
        }

        [Fact]
        public void Apply_AppendInstanceOverride_StringPathForm_RendersGenericSet_AndReparses()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = "BoxCollider",
                                PropertyPath = "m_Center.x",
                                Value = ValueNode.Primitive.Float(1f),
                            },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".Override(e => e.Set<BoxCollider>(\"m_Center.x\", 1f));\n", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);
            Assert.Equal("m_Center.x", propertyOverride.PropertyPath);
            Assert.Equal(ValueNode.Primitive.Float(1f), propertyOverride.Value);
        }

        [Fact]
        public void Apply_AppendInstanceOverride_AssetRefValue_RendersAssetCall_AndAddsUsing()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = "Light",
                                PropertyPath = "member:cookie",
                                Value = new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Textures/Cookie.png" }),
                            },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                ".Override(e => e.Set((Light x) => x.cookie, Asset(\"Assets/Textures/Cookie.png\")));\n",
                result);
            Assert.Contains("using static SceneBuilder.Authoring.AssetRefs;", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var propertyOverride = Assert.Single(instance.Overrides);
            Assert.Equal(
                new ValueNode.AssetRef(new AssetRef { DisplayPath = "Assets/Textures/Cookie.png" }),
                propertyOverride.ObjectReference);
        }

        [Fact]
        public void Apply_AppendInstanceOverride_ObjectRefViaValueExpression_RendersHandleVerbatim()
        {
            const string source = @"
public class TargetedInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var target = scene.Add(""Target"");
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec
                            {
                                TypeFullName = "AI",
                                PropertyPath = "member:target",
                                ValueExpression = "target",
                            },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".Override(e => e.Set((AI x) => x.target, target));\n", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots.OfType<PrefabInstanceNode>()));
            var propertyOverride = Assert.Single(instance.Overrides);
            Assert.Equal(new ValueNode.ObjectRef("target"), propertyOverride.ObjectReference);
        }

        [Fact]
        public void Apply_AppendInstanceOverride_TwoSets_FoldIntoOneClosure_AndReparseYieldsTwoEntries()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec { TypeFullName = "Health", PropertyPath = "member:health", Value = ValueNode.Primitive.Int(50) },
                            new OverrideSetSpec { TypeFullName = "Health", PropertyPath = "member:maxHealth", Value = ValueNode.Primitive.Int(100) },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                ".Override(e => e.Set((Health x) => x.health, 50).Set((Health x) => x.maxHealth, 100));\n",
                result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Equal(2, instance.Overrides.Length);
            Assert.Equal("member:health", instance.Overrides[0].PropertyPath);
            Assert.Equal("member:maxHealth", instance.Overrides[1].PropertyPath);
        }

        [Fact]
        public void Apply_AppendInstanceAddComponent_NoFields_RendersBareGeneric_AndReparses()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceAddComponent { Anchor = "enemy", TypeFullName = "UnityEngine.Light" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".AddComponent<UnityEngine.Light>();\n", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("UnityEngine.Light", added.Component.Type.FullName);
            Assert.Empty(added.Component.Fields);
        }

        [Fact]
        public void Apply_AppendInstanceAddComponent_WithFields_RendersSameClosureShapeAsComponentAppend()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceAddComponent
                    {
                        Anchor = "enemy",
                        TypeFullName = "UnityEngine.Light",
                        Fields = new FieldMap(new[]
                        {
                            new System.Collections.Generic.KeyValuePair<string, ValueNode>("m_Intensity", ValueNode.Primitive.Float(2.5f)),
                        }),
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            // Same closure rendering as AppendComponentStatement's single-field form
            // (ComponentPatchApplier.BuildComponentStatementText) — `(c => c.Set("key", value))`.
            Assert.Contains(
                ".AddComponent<UnityEngine.Light>(c => c.Set(\"m_Intensity\", 2.5f));\n",
                result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal(ValueNode.Primitive.Float(2.5f), added.Component.Fields["m_Intensity"]);
        }

        [Fact]
        public void Apply_AppendInstanceRemoveComponent_RendersGenericCall_AndReparses()
        {
            var source = PlainInstanceFixture;
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceRemoveComponent { Anchor = "enemy", TypeFullName = "UnityEngine.BoxCollider" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(".RemoveComponent<UnityEngine.BoxCollider>();\n", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var removed = Assert.Single(instance.RemovedComponents);
            Assert.Equal("UnityEngine.BoxCollider", removed.ComponentType);
        }

        [Fact]
        public void Apply_AppendOntoStatementWithExistingChainedCall_AppendsAfterAndKeepsSurroundingTrivia()
        {
            const string source = @"
public class TransformedInstanceScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        // Comment above enemy.
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"").Transform(pos: (1f, 2f, 3f));

        // Comment above other.
        var other = scene.Instance(""Assets/Prefabs/Coin.prefab"");
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new AppendInstanceOverride
                    {
                        Anchor = "enemy",
                        Sets = new[]
                        {
                            new OverrideSetSpec { TypeFullName = "Health", PropertyPath = "member:health", Value = ValueNode.Primitive.Int(50) },
                        },
                    },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.Contains(
                "var enemy = scene.Instance(\"Assets/Prefabs/Enemy.prefab\").Transform(pos: (1f, 2f, 3f)).Override(e => e.Set((Health x) => x.health, 50));\n",
                result);
            Assert.Contains("// Comment above enemy.", result);
            Assert.Contains("// Comment above other.", result);
            Assert.Contains("var other = scene.Instance(\"Assets/Prefabs/Coin.prefab\");", result);
        }

        [Fact]
        public void Apply_DropInstanceCall_Override_RemovesOnlyThatCall_SiblingAddComponentSurvives()
        {
            const string source = @"
public class DropOverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((Health x) => x.health, 50))
             .AddComponent<Light>();
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new DropInstanceCall { Anchor = "enemy", Kind = InstanceCallKind.Override, PropertyPath = "member:health" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain(".Override(", result);
            Assert.Contains(".AddComponent<Light>()", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Empty(instance.Overrides);
            var added = Assert.Single(instance.AddedComponents);
            Assert.Equal("Light", added.Component.Type.FullName);
        }

        [Fact]
        public void Apply_DropInstanceCall_AddComponent_RemovesExactlyThatType_RemoveComponentSurvives()
        {
            const string source = @"
public class DropAddComponentScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .AddComponent<Light>()
             .RemoveComponent<BoxCollider>();
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new DropInstanceCall { Anchor = "enemy", Kind = InstanceCallKind.AddComponent, TypeFullName = "Light" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain(".AddComponent<Light>", result);
            Assert.Contains(".RemoveComponent<BoxCollider>()", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Empty(instance.AddedComponents);
            var removed = Assert.Single(instance.RemovedComponents);
            Assert.Equal("BoxCollider", removed.ComponentType);
        }

        [Fact]
        public void Apply_DropSoleChainedCall_LeavesBareInstanceStatement_ThatReparsesCleanly()
        {
            const string source = @"
public class DropSoleCallScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Enemy.prefab"")
             .Override(e => e.Set((Health x) => x.health, 50));
    }
}
";
            var anchors = BuilderParser.Parse(source).Anchors;

            var patch = new SourcePatch
            {
                Edits = new SourceEdit[]
                {
                    new DropInstanceCall { Anchor = "enemy", Kind = InstanceCallKind.Override, PropertyPath = "member:health" },
                },
            };

            var result = SourcePatchApplier.Apply(source, patch, anchors);

            Assert.DoesNotContain(".Override(", result);
            Assert.Contains("var enemy = scene.Instance(\"Assets/Prefabs/Enemy.prefab\");", result);

            var reparsed = BuilderParser.Parse(result);
            var instance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            Assert.Empty(instance.Overrides);
        }
    }
}
