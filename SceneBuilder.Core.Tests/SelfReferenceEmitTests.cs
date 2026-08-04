using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A component field whose ObjectRef target IS the node the component is on must emit the
    // variable-free `NodeHandle.Self` selector, never the owner's own local. The chained form
    // (`var button = ....Component<Button>(c => c.Set("m_TargetGraphic", button));`) is CS0841.
    public class SelfReferenceEmitTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string ButtonType = "UnityEngine.UI.Button";
        private const string ImageType = "UnityEngine.UI.Image";
        private const string TargetGraphicField = "m_TargetGraphic";

        private const string ButtonChainedScene = @"
public class ButtonScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var button = scene.Add(""Button"").Component<UnityEngine.UI.Image>().Component<UnityEngine.UI.Button>();
    }
}
";

        private const string ButtonStatementAnchoredScene = @"
public class ButtonScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var button = scene.Add(""Button"");
        button.Component<UnityEngine.UI.Image>();
        button.Component<UnityEngine.UI.Button>();
    }
}
";

        // For every LocalDeclarationStatementSyntax, no IdentifierNameSyntax inside its own
        // initializer names the local being declared: a structural encoding of CS0841 for this
        // class of bug.
        private static void AssertNoLocalUsedInOwnDeclaration(string source)
        {
            var root = CSharpSyntaxTree.ParseText(source).GetRoot();

            foreach (var decl in root.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
            {
                foreach (var variable in decl.Declaration.Variables)
                {
                    if (variable.Initializer == null)
                    {
                        continue;
                    }

                    var name = variable.Identifier.Text;
                    var selfUse = variable.Initializer.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Any(id => id.Identifier.Text == name);

                    Assert.False(selfUse, $"local '{name}' is referenced inside its own declaration: {decl}");
                }
            }
        }

        private static void AssertNoSyntaxErrors(string source)
        {
            var diagnostics = CSharpSyntaxTree.ParseText(source).GetDiagnostics();
            Assert.DoesNotContain(diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        }

        private static SceneSnapshot SelfTargetingButtonSnapshot(string buttonLogicalId) => new SceneSnapshot
        {
            SchemaVersion = 1,
            Roots = new[]
            {
                new SnapshotNode
                {
                    GlobalObjectId = "goid-button",
                    Name = "Button",
                    Components = new[]
                    {
                        new ComponentData { LogicalId = "unused-image", Type = new TypeRef(ImageType), Fields = FieldMap.Empty },
                        new ComponentData
                        {
                            LogicalId = "unused-button",
                            Type = new TypeRef(ButtonType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>(TargetGraphicField, new ValueNode.ObjectRef(buttonLogicalId)),
                            }),
                        },
                    },
                },
            },
        };

        private static IdentityMap ButtonIdentityMap(ParseResult parsed, GameObjectNode button, string imageId, string buttonCompId) => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = button.LogicalId, GlobalObjectId = "goid-button", Kind = "GameObject" },
                new IdentityMapEntry { LogicalId = imageId, GlobalObjectId = "", Kind = "Component", ComponentType = ImageType, ParentLogicalId = button.LogicalId },
                new IdentityMapEntry { LogicalId = buttonCompId, GlobalObjectId = "", Kind = "Component", ComponentType = ButtonType, ParentLogicalId = button.LogicalId },
            },
        };

        [Fact]
        public void SceneToCode_SelfReference_ChainedComponent_EmitsSelfSelector_AndNoLocalUsedInOwnDeclaration()
        {
            var parsed = BuilderParser.Parse(ButtonChainedScene);
            var button = Assert.Single(parsed.Model.Roots);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ImageType + "#0"));
            var buttonCompId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ButtonType + "#0"));

            var map = ButtonIdentityMap(parsed, button, imageId, buttonCompId);
            var snapshot = SelfTargetingButtonSnapshot(button.LogicalId);

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(ButtonChainedScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains("NodeHandle.Self", patched);
            AssertNoLocalUsedInOwnDeclaration(patched);
            AssertNoSyntaxErrors(patched);
        }

        [Fact]
        public void SceneToCode_SelfReference_StatementAnchoredComponent_EmitsSelfSelector()
        {
            var parsed = BuilderParser.Parse(ButtonStatementAnchoredScene);
            var button = Assert.Single(parsed.Model.Roots);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ImageType + "#0"));
            var buttonCompId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ButtonType + "#0"));

            var map = ButtonIdentityMap(parsed, button, imageId, buttonCompId);
            var snapshot = SelfTargetingButtonSnapshot(button.LogicalId);

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(ButtonStatementAnchoredScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains(".Set(\"m_TargetGraphic\", NodeHandle.Self)", patched);
            AssertNoSyntaxErrors(patched);
        }

        // An existing chained `.Set("target", b)` retargeted in the scene to the owner itself
        // (PatchComponentField, not introduce) emits the same variable-free selector.
        [Fact]
        public void SceneToCode_SelfReference_PatchPath_EmitsSelfSelector()
        {
            const string ownerLogicalId = "root-1";
            const string otherLogicalId = "other-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new GameObjectNode[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId,
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = componentLogicalId,
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(otherLogicalId)) }),
                            },
                        },
                    },
                    new GameObjectNode { LogicalId = otherLogicalId, Name = "Other" },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = otherLogicalId, GlobalObjectId = "goid-other", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = componentLogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = DoorOpenerType, ParentLogicalId = ownerLogicalId },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(ownerLogicalId)) }),
                            },
                        },
                    },
                    new SnapshotNode { GlobalObjectId = "goid-other", Name = "Other" },
                },
            };

            var handles = new Dictionary<string, string> { [otherLogicalId] = "other" };
            var fieldSpans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>
            {
                [componentLogicalId] = new Dictionary<string, SourceSpan> { ["target"] = new SourceSpan(0, 10) },
            };

            var result = Reconciler.Reconcile(model, snapshot, map, null, null, null, null, fieldSpans, handles);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("NodeHandle.Self", patch.NewExpr);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceHandle>());
        }

        // The owner authors no `var` (`scene.Add("A").Component<T>();`) and a self-target
        // introduces its field. The self-selector needs no handle, so it must introduce none: a
        // handle-less owner must stay handle-less even when its own field targets itself.
        [Fact]
        public void SceneToCode_SelfReference_HandleLessOwner_IntroducesNoHandle()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId,
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(DoorOpenerType), Fields = FieldMap.Empty },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = componentLogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = DoorOpenerType, ParentLogicalId = ownerLogicalId },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-root",
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(ownerLogicalId)) }),
                            },
                        },
                    },
                },
            };

            // Non-null and empty: the owner has no authored handle anywhere in source.
            var handles = new Dictionary<string, string>();

            var result = Reconciler.Reconcile(model, snapshot, map, handles: handles);

            var introduce = Assert.Single(result.Patch.Edits.OfType<IntroduceComponentField>());
            Assert.Equal("NodeHandle.Self", introduce.NewExpr);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceHandle>());
        }

        // Re-parsing the patched source and reconciling against the same snapshot yields zero
        // edits: the convergence proof, and the case that would catch a marker leaking past
        // lowering.
        [Fact]
        public void SceneToCode_SelfReference_SecondReconcileIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(ButtonChainedScene);
            var button = Assert.Single(parsed.Model.Roots);
            var imageId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ImageType + "#0"));
            var buttonCompId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(ButtonType + "#0"));

            var map = ButtonIdentityMap(parsed, button, imageId, buttonCompId);
            var snapshot = SelfTargetingButtonSnapshot(button.LogicalId);

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);

            var patched = SourcePatchApplier.Apply(ButtonChainedScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));

            Assert.Contains("NodeHandle.Self", patched);

            var reparsed = BuilderParser.Parse(patched, map);

            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);

            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Patch.Edits);
        }

        // `NodeHandle.Self` parses and lowers to the LogicalId of the GameObject whose component
        // carries it. A nested child's `Self` resolves to the child, not the root.
        [Fact]
        public void Parse_NodeHandleSelf_LowersToOwningNodeLogicalId()
        {
            const string source = @"
public class DoorOpener : UnityEngine.MonoBehaviour { public UnityEngine.GameObject target; }

public class SelfLoweringScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var root = scene.Add(""Root"");
        root.Component<DoorOpener>(c => c.Set(x => x.target, NodeHandle.Self));

        var child = root.Add(""Child"");
        child.Component<DoorOpener>(c => c.Set(x => x.target, NodeHandle.Self));
    }
}
";
            var result = BuilderParser.Parse(source);

            var root = Assert.Single(result.Model.Roots);
            var rootComponent = Assert.Single(root.Components);
            var rootField = Assert.IsType<ValueNode.ObjectRef>(rootComponent.Fields["member:target"]);
            Assert.Equal(root.LogicalId, rootField.TargetLogicalId);

            var child = Assert.Single(root.Children);
            var childComponent = Assert.Single(child.Components);
            var childField = Assert.IsType<ValueNode.ObjectRef>(childComponent.Fields["member:target"]);
            Assert.Equal(child.LogicalId, childField.TargetLogicalId);
            Assert.NotEqual(root.LogicalId, childField.TargetLogicalId);
        }

        // A prefab-instance root override retargeted to the instance itself routes through
        // ReconcilerInstances.BuildOverrideSetSpec and must emit the same selector.
        [Fact]
        public void SceneToCode_SelfReferencingPrefabInstanceOverride_EmitsSelfSelector()
        {
            const string instanceLogicalId = "instance-1";
            const string prefabGuid = "guid-button-prefab";
            const string prefabPath = "Assets/Prefabs/Button.prefab";

            var snapshotOverride = new PropertyOverride
            {
                Target = new OverrideTarget { ComponentType = ButtonType },
                PropertyPath = TargetGraphicField,
                Value = ValueNode.Primitive.Int(0),
                ObjectReference = new ValueNode.ObjectRef(instanceLogicalId),
            };

            var instance = new PrefabInstanceNode
            {
                LogicalId = instanceLogicalId,
                Name = "Button",
                SourcePrefab = new AssetRef { Guid = prefabGuid, DisplayPath = prefabPath },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Button",
                SourcePrefabGuid = prefabGuid,
                Overrides = new[] { snapshotOverride },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = instanceLogicalId, GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = prefabGuid,
                    },
                },
            };

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var set = Assert.Single(append.Sets);
            Assert.Equal("NodeHandle.Self", set.ValueExpression);
        }

        // The deletion hole this fix closes: a node whose own component references itself must be
        // removed from source when it is deleted from the scene, never silently retained.
        [Fact]
        public void SelfReferencingNodeDeletedFromScene_IsRemovedFromSource()
        {
            const string ownerLogicalId = "root-1";
            var componentLogicalId = $"{ownerLogicalId}/{DoorOpenerType}#0";

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId,
                        Name = "Root",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = componentLogicalId,
                                Type = new TypeRef(DoorOpenerType),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef(ownerLogicalId)) }),
                            },
                        },
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = "goid-root", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = componentLogicalId, GlobalObjectId = "", Kind = "Component", ComponentType = DoorOpenerType, ParentLogicalId = ownerLogicalId },
                },
            };

            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = System.Array.Empty<SnapshotNode>() };

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Contains(result.Patch.Edits.OfType<RemoveStatement>(), r => r.Anchor == ownerLogicalId);
            Assert.Contains(ownerLogicalId, result.RemovedLogicalIds);
        }
    }
}
