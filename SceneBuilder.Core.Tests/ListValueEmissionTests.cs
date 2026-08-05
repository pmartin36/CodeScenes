using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A snapshot field whose List value carries an element with no shared C# type (an ObjectRef
    // handle beside an AssetRef), or an element with NO compiling emission form at all (a marker the
    // identity resolver could not map), is written correctly: a mixed-but-spellable list gets an
    // explicit `new object[] { ... }` element type so it compiles; a list reaching an unspellable
    // element writes no edit for that field and reports one located, deduplicated note instead of
    // emitting text that fails to compile.
    public class ListValueEmissionTests
    {
        private const string DoorOpenerType = "Game.DoorOpener";
        private const string FieldKey = "targets";

        private const string EmptyOpenerScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => { });
    }
}
";

        private const string AuthoredListScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.DoorOpener>(c => c.Set(""targets"", new[] { door, other }));
    }
}
";

        private const string DoorsOnlyScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
    }
}
";

        private static IdentityMap MapFor(ParseResult parsed, params (string Name, string Goid)[] nodes)
        {
            var entries = new List<IdentityMapEntry>();
            foreach (var (name, goid) in nodes)
            {
                entries.Add(new IdentityMapEntry
                {
                    LogicalId = parsed.Model.Roots.Single(r => r.Name == name).LogicalId,
                    GlobalObjectId = goid,
                    Kind = "GameObject",
                });
            }

            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var compId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith(DoorOpenerType + "#0"));
            entries.Add(new IdentityMapEntry
            {
                LogicalId = compId,
                GlobalObjectId = "",
                Kind = "Component",
                ComponentType = DoorOpenerType,
                ParentLogicalId = openerLid,
            });

            return new IdentityMap { Entries = entries.ToArray() };
        }

        private static SceneSnapshot SnapshotWith(ValueNode targetsValue, params (string Name, string Goid)[] plainNodes)
        {
            var roots = new List<SnapshotNode>();
            foreach (var (name, goid) in plainNodes)
            {
                roots.Add(new SnapshotNode { GlobalObjectId = goid, Name = name });
            }

            roots.Add(new SnapshotNode
            {
                GlobalObjectId = "goid-opener",
                Name = "Opener",
                Components = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "unused",
                        Type = new TypeRef(DoorOpenerType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, targetsValue) }),
                    },
                },
            });

            return new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
        }

        private static AssetRef PrefabAssetRef() => new AssetRef { DisplayPath = "Assets/Prefabs/Foo.prefab" };

        [Fact]
        public void MixedHandleAndAssetList_EmitsExplicitObjectArray_AndSecondReconcileIsFixedPoint()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.AssetRef(PrefabAssetRef()) }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Notes);
            var onlyEdit = Assert.Single(result.Patch.Edits);
            var introduce = Assert.IsType<IntroduceComponentField>(onlyEdit);
            Assert.Equal("new object[] { door, Asset(\"Assets/Prefabs/Foo.prefab\") }", introduce.NewExpr);

            var patched = SourcePatchApplier.Apply(EmptyOpenerScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new object[] { door, Asset(\"Assets/Prefabs/Foo.prefab\") })", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Notes);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void MixedHandleAndAssetList_AppendedComponent_EmitsExplicitObjectArray()
        {
            var parsed = BuilderParser.Parse(DoorsOnlyScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = parsed.Model.Roots.Single(r => r.Name == "Other").LogicalId, GlobalObjectId = "goid-other", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.AssetRef(PrefabAssetRef()) }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            var patched = SourcePatchApplier.Apply(DoorsOnlyScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains(".Set(\"targets\", new object[] { door, Asset(\"Assets/Prefabs/Foo.prefab\") })", patched);
        }

        [Fact]
        public void UnemittableListItem_PatchPath_EmitsNoEdit_AndReportsOneLocatedNote()
        {
            var parsed = BuilderParser.Parse(AuthoredListScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.Unsupported("ObjectReference") }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.NotNull(note.RecurrenceKey);
            Assert.Equal(FieldKey, note.Located!.MemberKey);

            var patched = SourcePatchApplier.Apply(AuthoredListScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            Assert.Equal(AuthoredListScene, patched);
        }

        [Fact]
        public void UnemittableListItem_IntroducePath_EmitsNoEdit_AndReportsOneLocatedNote()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.Unsupported("ObjectReference") }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal(FieldKey, note.Located!.MemberKey);
        }

        [Fact]
        public void UnemittableListItem_AppendPath_OmitsTheFieldAndReports()
        {
            var parsed = BuilderParser.Parse(DoorsOnlyScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = parsed.Model.Roots.Single(r => r.Name == "Other").LogicalId, GlobalObjectId = "goid-other", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                },
            };

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.Unsupported("ObjectReference") }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.False(append.Fields.TryGetValue(FieldKey, out _));
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);

            var patched = SourcePatchApplier.Apply(DoorsOnlyScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.DoesNotContain(FieldKey, patched);
        }

        [Fact]
        public void ListOfUnemittableItemsWithNoReference_EmitsNoEdit_AndReports()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { new ValueNode.Unsupported("ObjectReference"), new ValueNode.Unsupported("ObjectReference") }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
        }

        [Fact]
        public void ListItemNestedStructWithUnemittableMember_EmitsNoEdit_AndReports()
        {
            var parsed = BuilderParser.Parse(EmptyOpenerScene);
            var map = MapFor(parsed, ("Door", "goid-door"), ("Other", "goid-other"), ("Opener", "goid-opener"));

            var slot = new ValueNode.Nested(
                "Game.Slot",
                new FieldMap(new[] { new KeyValuePair<string, ValueNode>("e", new ValueNode.Unsupported("UnityEvent")) }));

            var snapshot = SnapshotWith(
                new ValueNode.List(new ValueNode[] { slot }),
                ("Door", "goid-door"), ("Other", "goid-other"));

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Patch.Edits.OfType<IntroduceComponentField>());
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
        }

        [Fact]
        public void NestedFieldCarryingAReferenceList_IntroducesTypedInitializer_AndIsFixedPoint()
        {
            const string nestedFieldKey = "waypoints";
            const string ownerScene = @"
public class S : ISceneDefinition
{
    public void Build(SceneRoot scene)
    {
        var door = scene.Add(""Door"");
        var other = scene.Add(""Other"");
        var opener = scene.Add(""Opener"");
        opener.Component<Game.PathFollower>(c => { });
    }
}
";
            var parsed = BuilderParser.Parse(ownerScene);
            var doorLid = parsed.Model.Roots.Single(r => r.Name == "Door").LogicalId;
            var otherLid = parsed.Model.Roots.Single(r => r.Name == "Other").LogicalId;
            var openerLid = parsed.Model.Roots.Single(r => r.Name == "Opener").LogicalId;
            var compId = parsed.ComponentAnchors.Keys.Single(k => k.EndsWith("Game.PathFollower#0"));

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = doorLid, GlobalObjectId = "goid-door", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = otherLid, GlobalObjectId = "goid-other", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = openerLid, GlobalObjectId = "goid-opener", Kind = "GameObject" },
                    new IdentityMapEntry { LogicalId = compId, GlobalObjectId = "", Kind = "Component", ComponentType = "Game.PathFollower", ParentLogicalId = openerLid },
                },
            };

            var path = new ValueNode.Nested(
                "Game.Slot",
                new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "items",
                        new ValueNode.List(new ValueNode[] { new ValueNode.ObjectRef(doorLid), new ValueNode.ObjectRef(otherLid) })),
                }));

            var roots = new List<SnapshotNode>
            {
                new SnapshotNode { GlobalObjectId = "goid-door", Name = "Door" },
                new SnapshotNode { GlobalObjectId = "goid-other", Name = "Other" },
                new SnapshotNode
                {
                    GlobalObjectId = "goid-opener",
                    Name = "Opener",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            LogicalId = "unused",
                            Type = new TypeRef("Game.PathFollower"),
                            Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(nestedFieldKey, path) }),
                        },
                    },
                },
            };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors,
                parsed.FieldArgumentSpans, parsed.Handles);

            Assert.Empty(result.Conflicts);
            var onlyEdit = Assert.Single(result.Patch.Edits.OfType<IntroduceComponentField>());

            var patched = SourcePatchApplier.Apply(ownerScene, result.Patch, SourcePatchTestHelpers.MergeAnchors(parsed));
            SourcePatchTestHelpers.AssertNoSyntaxErrors(patched);
            Assert.Contains("new Game.Slot { items = new[] { door, other } }", patched);

            var reparsed = BuilderParser.Parse(patched, map);
            var recon2 = Reconciler.Reconcile(
                reparsed.Model, snapshot, reparsed.IdentityMap, reparsed.Anchors, null, null,
                reparsed.ComponentAnchors, reparsed.FieldArgumentSpans, reparsed.Handles);
            Assert.Empty(recon2.Conflicts);
            Assert.Empty(recon2.Notes);
            Assert.Empty(recon2.Patch.Edits);
        }

        [Fact]
        public void ArrayPrefix_MixedHandleAndAsset_IsExplicitlyTyped()
        {
            var list = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Unsupported("door"),
                new ValueNode.AssetRef(PrefabAssetRef()),
            });

            Assert.Equal("new object[] { ", ListValueEmission.ArrayPrefix(list));
        }

        [Fact]
        public void ArrayPrefix_AllHandleTokens_StaysImplicitlyTyped()
        {
            var list = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Unsupported("door"),
                new ValueNode.Unsupported("NodeHandle.None"),
            });

            Assert.Equal("new[] { ", ListValueEmission.ArrayPrefix(list));
        }

        [Fact]
        public void ArrayPrefix_SameNestedType_StaysImplicitlyTyped()
        {
            var list = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.Nested("Game.Damage", new FieldMap(Array.Empty<KeyValuePair<string, ValueNode>>())),
                new ValueNode.Nested("Game.Damage", new FieldMap(Array.Empty<KeyValuePair<string, ValueNode>>())),
            });

            Assert.Equal("new[] { ", ListValueEmission.ArrayPrefix(list));
        }

        [Fact]
        public void HasUnemittableItem_MixedHandleAndAsset_IsFalse()
        {
            var value = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.ObjectRef("some-id"),
                new ValueNode.AssetRef(PrefabAssetRef()),
            });

            Assert.False(ListValueEmission.HasUnemittableItem(value));
        }

        [Fact]
        public void HasUnemittableItem_ListContainingUnsupported_IsTrue()
        {
            var value = new ValueNode.List(new ValueNode[]
            {
                new ValueNode.ObjectRef("some-id"),
                new ValueNode.Unsupported("ObjectReference"),
            });

            Assert.True(ListValueEmission.HasUnemittableItem(value));
        }

        [Fact]
        public void HasUnemittableItem_BareUnsupported_NotInsideAList_IsFalse()
        {
            Assert.False(ListValueEmission.HasUnemittableItem(new ValueNode.Unsupported("ObjectReference")));
        }

        [Fact]
        public void HasUnemittableItem_NestedMemberIsAListWithUnsupportedItem_IsTrue()
        {
            var value = new ValueNode.Nested(
                "Game.Slot",
                new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        "items",
                        new ValueNode.List(new ValueNode[] { new ValueNode.Unsupported("ObjectReference") })),
                }));

            Assert.True(ListValueEmission.HasUnemittableItem(value));
        }

        // Scans production source (SceneBuilder.Core/**, com.codescenes/**, excluding tests and
        // build output) for a string literal spelling an array-literal prefix (`"new[] { "` /
        // `"new object[] { "`) — asserts ListValueEmission.cs is the only file that spells one, so a
        // new emit site cannot hand-roll its own element-type decision.
        [Fact]
        public void ArrayLiteralPrefix_HasOneProductionSite()
        {
            var repoRoot = RepoRoot();
            var offenders = new List<string>();

            foreach (var scanRoot in new[] { "SceneBuilder.Core", "com.codescenes" })
            {
                var rootPath = Path.Combine(repoRoot, scanRoot);
                if (!Directory.Exists(rootPath))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                    if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.Contains("Tests"))
                    {
                        continue;
                    }

                    var text = File.ReadAllText(file);
                    if (text.Contains("\"new[] { ") || text.Contains("\"new object[] {"))
                    {
                        offenders.Add(relative);
                    }
                }
            }

            Assert.Equal(new[] { "SceneBuilder.Core/Reconcile/ListValueEmission.cs" }, offenders.OrderBy(o => o, StringComparer.Ordinal));
        }

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
                    $"ListValueEmissionTests.RepoRoot: no SceneBuilder.sln found walking up from '{here}'");
            }

            return dir;
        }
    }
}
