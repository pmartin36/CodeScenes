using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Lowering;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A prefab-instance ROOT rewired onto a reference field is a valid emitted token -- the
    // reconciler and applier already produce it -- but the applied source must also BIND against
    // the real com.codescenes/Runtime authoring surface. A syntax-only parse cannot see a type
    // mismatch (SourcePatchTestHelpers.AssertNoSyntaxErrors passes on the broken source too), so
    // these tests bind with AuthoringBindHarness instead.
    public class InstanceTargetBindTests
    {
        private const string ComponentTypeFullName = "Game.DoorOpener";

        // ---- pipeline helper: parse -> lower ObjectRef -> the member: rewrite Reconcile expects
        // from the adapter -> Reconcile -> Apply.
        private static (ReconcileResult Result, string Applied) ReconcileAndApply(
            string source, IdentityMap map, SceneSnapshot snapshot)
        {
            var parsed = BuilderParser.Parse(source);
            var lowered = ObjectRefLowering.Lower(
                parsed.Model,
                name => parsed.Handles.TryGetValue(name, out var id) ? id : null);
            var model = RewriteMemberKeys(lowered);
            var spans = RewriteSpanKeys(parsed.FieldArgumentSpans);

            var anchors = new Dictionary<string, SourceSpan>(parsed.Anchors);
            foreach (var kv in parsed.ComponentAnchors)
            {
                anchors[kv.Key] = kv.Value;
            }

            var result = Reconciler.Reconcile(
                model, snapshot, map, anchors, null, parsed.FlagPresence, parsed.ComponentAnchors,
                spans, parsed.Handles, null, null, parsed.ChainedComponents);

            var applied = SourcePatchApplier.Apply(source, result.Patch, anchors);
            return (result, applied);
        }

        // The transient "member:<name>" field key a typed-selector parse produces, rewritten to the
        // plain key before Reconcile sees it -- mirrors the adapter's own rewrite ahead of the diff.
        private static GameObjectNode RewriteNode(GameObjectNode node) => node with
        {
            Components = node.Components.Select(c => c with
            {
                Fields = new FieldMap(c.Fields.Select(f => new KeyValuePair<string, ValueNode>(
                    f.Key.StartsWith("member:") ? f.Key.Substring("member:".Length) : f.Key, f.Value))),
            }).ToArray(),
            Children = node.Children.Select(RewriteNode).ToArray(),
        };

        private static SceneModel RewriteMemberKeys(SceneModel model) =>
            model with { Roots = model.Roots.Select(RewriteNode).ToArray() };

        private static Dictionary<string, IReadOnlyDictionary<string, SourceSpan>> RewriteSpanKeys(
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> spans) =>
            spans.ToDictionary(
                kv => kv.Key,
                kv => (IReadOnlyDictionary<string, SourceSpan>)kv.Value.ToDictionary(
                    f => f.Key.StartsWith("member:") ? f.Key.Substring("member:".Length) : f.Key,
                    f => f.Value));

        // ---- fixture 1: a typed-selector component field rewired onto a prefab-instance root ----

        private const string TypedSelectorSource = @"using SceneBuilder.Authoring;

public class TypedSelectorScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => c.Set(x => x.target, door));
    }
}
";

        private static (IdentityMap Map, SceneSnapshot Snapshot) TypedSelectorFixture()
        {
            var componentLogicalId = "opener/" + ComponentTypeFullName + "#0";

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "door", GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                    new IdentityMapEntry { LogicalId = "opener", GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId, GlobalObjectId = "goid-comp", Kind = "Component",
                        ComponentType = ComponentTypeFullName, ParentLogicalId = "opener",
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                    new SnapshotNode { GlobalObjectId = "goid-tank", Name = "Tank" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(ComponentTypeFullName),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("tank")),
                                }),
                            },
                        },
                    },
                },
            };

            return (map, snapshot);
        }

        // #1: the reconciler already emits the instance-root token into the typed selector; the
        // applied source must also compile against the real authoring surface.
        [Fact]
        public void TypedSelectorRewiredToInstanceRoot_AppliedSourceBinds()
        {
            var (map, snapshot) = TypedSelectorFixture();
            var (result, applied) = ReconcileAndApply(TypedSelectorSource, map, snapshot);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("tank", patch.NewExpr);
            Assert.Contains("c.Set(x => x.target, tank)", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // #2: re-running the same pipeline over the applied source, against the same snapshot,
        // converges -- no further edits, conflicts, notes, or skipped fields.
        [Fact]
        public void TypedSelectorRewiredToInstanceRoot_SecondReconcileIsAFixedPoint()
        {
            var (map, snapshot) = TypedSelectorFixture();
            var (_, applied) = ReconcileAndApply(TypedSelectorSource, map, snapshot);

            var (second, _) = ReconcileAndApply(applied, map, snapshot);

            Assert.Empty(second.Patch.Edits);
            Assert.Empty(second.Conflicts);
            Assert.Empty(second.Notes);
            Assert.Empty(second.Skipped);
        }

        // ---- fixture 3: NodeHandle.SurfaceSnap(target:) rewired onto a prefab-instance root ----

        private const string SurfaceSnapSource = @"using SceneBuilder.Authoring;

public class SurfaceSnapScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var opener = scene.Add(""Opener"");
        opener.SurfaceSnap(up: true, target: door);
    }
}
";

        // #3: the second reference-accepting authoring parameter -- SurfaceSnap's optional `target`
        // -- reproduces the same defect at a second site and must bind after the same fix.
        [Fact]
        public void SurfaceSnapTargetRewiredToInstanceRoot_AppliedSourceBinds()
        {
            const string surfaceSnapType = "SceneBuilder.Authoring.SurfaceSnap";
            var componentLogicalId = "opener/" + surfaceSnapType + "#0";

            var parsed = BuilderParser.Parse(SurfaceSnapSource);
            var lowered = ObjectRefLowering.Lower(
                parsed.Model,
                name => parsed.Handles.TryGetValue(name, out var id) ? id : null);

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "door", GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                    new IdentityMapEntry { LogicalId = "opener", GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId, GlobalObjectId = "goid-snap", Kind = "Component",
                        ComponentType = surfaceSnapType, ParentLogicalId = "opener",
                    },
                },
            };

            var snapshotFields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>(
                    "vertical", new ValueNode.Enum("SceneBuilder.Authoring.SurfaceSnap+Vertical", new[] { "Up" }, false)),
                new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("tank")),
            });

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                    new SnapshotNode { GlobalObjectId = "goid-tank", Name = "Tank" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(surfaceSnapType),
                                Fields = snapshotFields,
                            },
                        },
                    },
                },
            };

            var anchors = new Dictionary<string, SourceSpan>(parsed.Anchors);
            foreach (var kv in parsed.ComponentAnchors)
            {
                anchors[kv.Key] = kv.Value;
            }

            var result = Reconciler.Reconcile(
                lowered, snapshot, map, anchors, null, parsed.FlagPresence, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles, null, null, parsed.ChainedComponents);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal("tank", patch.NewExpr);

            var applied = SourcePatchApplier.Apply(SurfaceSnapSource, result.Patch, anchors);
            Assert.Contains("target: tank", applied);

            var errors = AuthoringBindHarness.BindErrors(applied, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // #4: whether the reconciler can PRODUCE this pairing through OverrideHandle.Set is a
        // separate reachability question; this pins that the authoring SURFACE accepts it once
        // written by hand.
        [Fact]
        public void InstanceOverrideTypedSelector_WithAnInstanceTarget_Binds()
        {
            const string source = @"using SceneBuilder.Authoring;

public class OverrideScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var door = scene.Add(""Door"");
        var other = scene.Instance(""Assets/Prefabs/Other.prefab"");
        other.Override(e => e.Set((Game.DoorOpener x) => x.target, tank).Set((Game.DoorOpener x) => x.target, door));
    }
}
";
            var errors = AuthoringBindHarness.BindErrors(source, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // #5: the full authoring matrix in one compilation -- the anti-ambiguity regression net.
        // Every pre-existing reference spelling must keep binding alongside the widened ones.
        [Fact]
        public void EveryAuthoredReferenceSpelling_StillBinds()
        {
            const string source = @"using SceneBuilder.Authoring;
using static SceneBuilder.Authoring.AssetRefs;

public class FullMatrixScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var typedTank = scene.Instance(Prefabs.Tank);
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => {
            c.Set(x => x.target, door);
            c.Set(x => x.target, NodeHandle.None);
            c.Set(x => x.target, NodeHandle.Self);
            c.Set(x => x.target, tank);
            c.Set(x => x.target, typedTank);
            c.Set(""target"", tank);
            c.Set(x => x.speed, 5f);
            c.Set(x => x.mat, Asset(""Assets/M.mat""));
        });
        opener.SurfaceSnap(up: true, target: door);
        opener.SurfaceSnap(up: true, target: tank);
        opener.SurfaceSnap(up: true, target: NodeHandle.None);
        opener.SurfaceSnap(up: true);
        tank.Override(e => e.Set((Game.DoorOpener x) => x.target, tank).Set((Game.DoorOpener x) => x.target, door));
    }
}
";
            var errors = AuthoringBindHarness.BindErrors(source, AuthoringBindHarness.DoorOpenerStubs);
            Assert.Empty(errors);
        }

        // ---- fixture 6: the APPEND path (component absent from source, present in the snapshot) ----

        // #6: the spec forbids changing the append path -- a sync-emitted component `Set` renders
        // in the hardcoded STRING spelling, never a typed selector, even for a reference field
        // whose target is a prefab-instance root.
        [Fact]
        public void AppendedComponentReferenceField_StillRendersTheStringSpelling()
        {
            const string source = @"using SceneBuilder.Authoring;

public class AppendScene : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var tank = scene.Instance(""Assets/Prefabs/Tank.prefab"");
        var opener = scene.Add(""Opener"");
    }
}
";
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "tank", GlobalObjectId = "goid-tank", Kind = "PrefabInstance" },
                    new IdentityMapEntry { LogicalId = "opener", GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode { GlobalObjectId = "goid-tank", Name = "Tank" },
                    new SnapshotNode
                    {
                        GlobalObjectId = "goid-opener",
                        Name = "Opener",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(ComponentTypeFullName),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("target", new ValueNode.ObjectRef("tank")),
                                }),
                            },
                        },
                    },
                },
            };

            var (result, applied) = ReconcileAndApply(source, map, snapshot);

            Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Contains("c.Set(\"target\", tank)", applied);
        }

        // ---- fixture 7: the structural surface scan ----

        private static string RepoRoot([CallerFilePath] string here = "")
        {
            var dir = Path.GetDirectoryName(here);
            while (dir != null && !File.Exists(Path.Combine(dir, "SceneBuilder.sln")))
            {
                dir = Path.GetDirectoryName(dir);
            }

            if (dir == null)
            {
                throw new InvalidOperationException(
                    $"InstanceTargetBindTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }

        // #7: no parameter in the published authoring surface is typed exactly `NodeHandle` -- the
        // structural invariant that makes a bypassing emit site fail to COMPILE rather than rely on
        // a convention. An `Action<NodeHandle>` closure parameter is a GenericNameSyntax, not an
        // IdentifierNameSyntax, so it is untouched by this identifier match and needs no allowlist.
        [Fact]
        public void AuthoringSurface_NoParameterAcceptsOnlyANodeHandle()
        {
            var runtimeDir = Path.Combine(RepoRoot(), "com.codescenes", "Runtime");
            var files = Directory.EnumerateFiles(runtimeDir, "*.cs").ToList();
            Assert.True(files.Count > 0, "scan found zero com.codescenes/Runtime/*.cs files -- repo root resolution is broken");

            var offenders = new List<string>();
            foreach (var file in files)
            {
                var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), path: file);
                foreach (var parameter in tree.GetRoot().DescendantNodes().OfType<ParameterSyntax>())
                {
                    if (parameter.Type is IdentifierNameSyntax { Identifier.Text: "NodeHandle" })
                    {
                        var line = parameter.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                        offenders.Add($"{Path.GetFileName(file)}:{line} {parameter}");
                    }
                }
            }

            Assert.True(offenders.Count == 0, string.Join("\n", offenders));
        }
    }
}
