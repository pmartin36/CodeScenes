using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;
using static SceneBuilder.Core.Tests.RectTransformFixtures;

namespace SceneBuilder.Core.Tests
{
    // m-ui-recttransform b3-t1 (iteration 2, research.md "the routed question, answered: YES"):
    // a UI child added inside a prefab instance must carry its RectTransform payload through the
    // SAME create-with-payload seam ReconcileConflictTests.CreateWithPayload_AddedChildWithComponent_ConvergesInOnePass
    // already proves for components. Today RenderAddChildClosure (SourcePatchApplier.Instances.cs)
    // renders components only, so the five authored rect fields are silently dropped scene->code
    // (scope/bucket-b3.md SEVERITY low finding, routed to research).
    public class RectTransformInstanceChildTests
    {
        private const string PrefabGuid = "guid-hud-prefab";
        private const string PrefabPath = "Assets/Prefabs/Hud.prefab";

        private const string InstanceFixture = @"
public class HudScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var enemy = scene.Instance(""Assets/Prefabs/Hud.prefab"");
    }
}
";

        private static (SceneModel Model, SceneSnapshot Snapshot, IdentityMap Map) SingleAddedChild(GameObjectNode node)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "enemy",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };
            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-enemy",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[]
                {
                    new AddedGameObject { Parent = new OverrideTarget { ChildPath = "Turret" }, Node = node },
                },
            };
            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "enemy", GlobalObjectId = "goid-enemy", Kind = "PrefabInstance", SourcePrefabGuid = PrefabGuid },
                },
            };

            return (model, snapshot, map);
        }

        // The load-bearing case: the rect payload must ride inside the SAME AddChild closure the
        // component payload already uses, and a second Reconcile of the re-parsed (now
        // payload-carrying) model against the SAME snapshot must converge with zero edits.
        [Fact]
        public void AddedChildRect_RendersRectTransformInClosure_Reparses_SecondReconcileNoOp()
        {
            var hud = new GameObjectNode
            {
                Name = "Hud",
                Transform = Rect(anchoredPos: new Vec2(12, -8), sizeDelta: new Vec2(200, 80)),
            };
            var (model, snapshot, map) = SingleAddedChild(hud);

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            Assert.NotNull(append.RectTransform);

            var anchors = BuilderParser.Parse(InstanceFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(InstanceFixture, result.Patch, anchors);

            Assert.Contains(
                ".AddChild(\"Turret\", \"Hud\", cfg => cfg.RectTransform(anchoredPos: (12f, -8f), sizeDelta: (200f, 80f)))",
                rendered);

            var reparsed = BuilderParser.Parse(rendered);
            var reparsedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var reparsedAdded = Assert.Single(reparsedInstance.AddedGameObjects);
            Assert.Equal(RectTransformFields.Kind, reparsedAdded.Node.Transform.Kind);
            Assert.Equal(new Vec2(12, -8), reparsedAdded.Node.Transform.AnchoredPosition);
            Assert.Equal(new Vec2(200, 80), reparsedAdded.Node.Transform.SizeDelta);

            var modelAfter = model with { Roots = new GameObjectNode[] { reparsedInstance } };
            var result2 = Reconciler.Reconcile(modelAfter, snapshot, map);
            Assert.Empty(result2.Patch.Edits.OfType<AppendInstanceAddChild>());
            Assert.Empty(result2.Patch.Edits.OfType<DropInstanceCall>());
        }

        // §13 one-pass convergence: a rect payload and a component payload on the SAME added child
        // fold into ONE closure (rect first), not two competing renders / a dropped component.
        [Fact]
        public void AddedChildRect_WithComponent_FoldsRectAndComponentIntoOneClosure()
        {
            var hud = new GameObjectNode
            {
                Name = "Hud",
                Transform = Rect(sizeDelta: new Vec2(200, 80)),
                Components = new[]
                {
                    new ComponentData
                    {
                        Type = new TypeRef("UnityEngine.UI.Image"),
                        Fields = new FieldMap(System.Array.Empty<KeyValuePair<string, ValueNode>>()),
                    },
                },
            };
            var (model, snapshot, map) = SingleAddedChild(hud);

            var result = Reconciler.Reconcile(model, snapshot, map);
            var anchors = BuilderParser.Parse(InstanceFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(InstanceFixture, result.Patch, anchors);

            Assert.Contains(
                "cfg => { cfg.RectTransform(sizeDelta: (200f, 80f)); cfg.Component<UnityEngine.UI.Image>(); }",
                rendered);

            var reparsed = BuilderParser.Parse(rendered);
            var reparsedInstance = Assert.IsType<PrefabInstanceNode>(Assert.Single(reparsed.Model.Roots));
            var reparsedAdded = Assert.Single(reparsedInstance.AddedGameObjects);
            Assert.Equal(RectTransformFields.Kind, reparsedAdded.Node.Transform.Kind);
            var reparsedComponent = Assert.Single(reparsedAdded.Node.Components);
            Assert.Equal("UnityEngine.UI.Image", reparsedComponent.Type.FullName);
        }

        // D5/D1: a driven axis must never be authored with its LIVE value — it renders the held
        // authoring default on that axis while a non-driven axis on the SAME field still renders
        // its live value (per-axis, not whole-field).
        [Fact]
        public void AddedChildRect_DrivenAxis_AuthorsTheHeldDefault_NotTheLiveValue()
        {
            var hud = new GameObjectNode
            {
                Name = "Hud",
                Transform = Rect(sizeDelta: new Vec2(50, 80), driven: ChannelMask.SizeDeltaX),
            };
            var (model, snapshot, map) = SingleAddedChild(hud);

            var result = Reconciler.Reconcile(model, snapshot, map);
            var anchors = BuilderParser.Parse(InstanceFixture).Anchors;
            var rendered = SourcePatchApplier.Apply(InstanceFixture, result.Patch, anchors);

            // sizeDelta.X is driven -> held at RectTransformFields.DefaultSizeDelta.X (100), never
            // the live 50; sizeDelta.Y (80) is free and renders verbatim.
            Assert.Contains(".RectTransform(sizeDelta: (100f, 80f))", rendered);
            Assert.DoesNotContain("(50f,", rendered);
        }
    }
}
