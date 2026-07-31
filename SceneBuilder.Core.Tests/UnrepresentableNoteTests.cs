using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A nested field's projection can come to nothing (NestedValueEmission.Projection.IsEmptyNested)
    // for two different reasons, and the two must be told apart:
    //   * every excluded member is PROVABLY non-default (an AssetRef/ObjectRef the live scene
    //     carries a real value for) -- an INSTANCE fact, reported once per component;
    //   * every excluded member is an Unsupported placeholder, whose marker names the member but
    //     never its value, so no instance can ever prove it changed -- a TYPE fact, reported once
    //     per (component type, field), never once per instance (a stock scene with N Buttons must
    //     not cost N reports).
    // Either way the report is a STANDING condition (ReconcileResult.Notes, Conflict.RecurrenceKey
    // set), never a per-pass event (ReconcileResult.Conflicts): a settled scene must never grow a
    // fresh conflict on every sync just because nothing about it changed.
    public class UnrepresentableNoteTests
    {
        private const string CompType = "UnityEngine.UI.Text";
        private const string FieldKey = "m_FontData";
        private const string TypeName = "UnityEngine.UI.FontData";

        private static ValueNode.Nested Node(params (string Key, ValueNode Value)[] fields) =>
            new(TypeName, new FieldMap(fields.Select(f => new KeyValuePair<string, ValueNode>(f.Key, f.Value))));

        private static ValueNode.Primitive Int(int v) => ValueNode.Primitive.Int(v);

        private static ValueNode.Unsupported Unsupported(string member) =>
            new($"no public member for '{member}'");

        private static ValueNode.AssetRef Font(string? guid) =>
            new(guid is null ? null : new AssetRef { Guid = guid, DisplayPath = "Assets/MyFont.ttf", TypeHint = "Font" });

        // Builds a one-component scene (owner "root-1" unless overridden) whose field is `source`
        // in the builder file (null = absent), `live` in the snapshot, and `default` in the
        // per-type default template -- the exact shape `Reconciler.Reconcile` compares.
        private static ReconcileResult Reconcile(
            ValueNode? source, ValueNode live, ValueNode @default, string ownerLogicalId = "root-1")
        {
            var componentLogicalId = $"{ownerLogicalId}/{CompType}#0";
            var sourceFields = source is null
                ? new FieldMap(System.Array.Empty<KeyValuePair<string, ValueNode>>())
                : new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, source) });

            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new GameObjectNode
                    {
                        LogicalId = ownerLogicalId,
                        Name = "Txt",
                        Components = new[]
                        {
                            new ComponentData { LogicalId = componentLogicalId, Type = new TypeRef(CompType), Fields = sourceFields },
                        },
                    },
                },
            };

            var snapshot = new SceneSnapshot
            {
                SchemaVersion = 1,
                Roots = new[]
                {
                    new SnapshotNode
                    {
                        GlobalObjectId = $"goid-{ownerLogicalId}",
                        Name = "Txt",
                        Components = new[]
                        {
                            new ComponentData
                            {
                                LogicalId = "unused",
                                Type = new TypeRef(CompType),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, live) }),
                            },
                        },
                    },
                },
                ComponentDefaults = new[]
                {
                    new ComponentData
                    {
                        LogicalId = "template",
                        Type = new TypeRef(CompType),
                        Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, @default) }),
                    },
                },
            };

            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = ownerLogicalId, GlobalObjectId = $"goid-{ownerLogicalId}", Kind = "GameObject" },
                    new IdentityMapEntry
                    {
                        LogicalId = componentLogicalId, GlobalObjectId = "", Kind = "Component",
                        ComponentType = CompType, ParentLogicalId = ownerLogicalId,
                    },
                },
            };

            var spans = new Dictionary<string, IReadOnlyDictionary<string, SourceSpan>>
            {
                [componentLogicalId] = source is null
                    ? new Dictionary<string, SourceSpan>()
                    : new Dictionary<string, SourceSpan> { [FieldKey] = new SourceSpan(0, 10) },
            };

            return Reconciler.Reconcile(model, snapshot, map, null, null, null, null, spans, null);
        }

        [Fact]
        public void Reconcile_AllMembersUnrepresentableWithNoPublicSpelling_ReportsOneNoteAndNoConflict()
        {
            var value = Node(("m_Hidden", Unsupported("m_Hidden")));

            var result = Reconcile(source: null, live: value, @default: value);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal("root-1/UnityEngine.UI.Text#0", note.LogicalId);
            Assert.Contains(FieldKey, note.Reason);
            Assert.NotNull(note.RecurrenceKey);
            Assert.Contains(CompType, note.RecurrenceKey!);
            Assert.Contains(FieldKey, note.RecurrenceKey!);
        }

        [Fact]
        public void Reconcile_SourceHasFieldAndOnlyMemberIsUnrepresentable_ReportsOneNote()
        {
            var value = Node(("m_Hidden", Unsupported("m_Hidden")));

            var result = Reconcile(source: value, live: value, @default: value);

            Assert.Empty(result.Conflicts);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.NotNull(note.RecurrenceKey);
        }

        [Fact]
        public void Reconcile_EveryMemberAtTypeDefault_ReportsNothing()
        {
            var settled = Node(("font", Font(null)), ("fontSize", Int(14)));

            var result = Reconcile(source: null, live: settled, @default: settled);

            Assert.Empty(result.Patch.Edits);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Notes);
        }

        [Fact]
        public void Reconcile_AllDifferingMembersAreAssetRefs_ReportsAnInstanceScopedNote()
        {
            var live = Node(("font", Font("guid-1")), ("fontSize", Int(14)));
            var @default = Node(("font", Font(null)), ("fontSize", Int(14)));

            var result = Reconcile(source: null, live: live, @default: @default);

            Assert.Empty(result.Conflicts);
            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.NotNull(note.RecurrenceKey);
            Assert.Contains("root-1/UnityEngine.UI.Text#0", note.RecurrenceKey!);
        }

        [Fact]
        public void Reconcile_TwoComponentsOfTheSameTypeWithTheSameUnprovableField_ShareOneRecurrenceKey()
        {
            var value = Node(("m_OnClick", Unsupported("m_OnClick")));

            var first = Reconcile(source: null, live: value, @default: value, ownerLogicalId: "root-1");
            var second = Reconcile(source: null, live: value, @default: value, ownerLogicalId: "root-2");

            var firstNote = Assert.Single(first.Notes);
            var secondNote = Assert.Single(second.Notes);
            Assert.NotNull(firstNote.RecurrenceKey);
            Assert.Equal(firstNote.RecurrenceKey, secondNote.RecurrenceKey);
            Assert.DoesNotContain("root-1", firstNote.RecurrenceKey);
            Assert.DoesNotContain("root-2", secondNote.RecurrenceKey);
        }

        // Control: an already-correct shape (one representable member changed, one member has no
        // public spelling) must keep reporting through the unkeyed per-pass Conflicts channel and
        // must keep emitting the edit for the representable member.
        [Fact]
        public void Reconcile_MixedStructWithOneRepresentableMember_StillReportsAnUnkeyedConflictAndEmitsTheEdit()
        {
            var source = Node(("visible", ValueNode.Primitive.Float(0)));
            var live = Node(("visible", ValueNode.Primitive.Float(3)), ("m_Hidden", Unsupported("m_Hidden")));
            var @default = Node(("visible", ValueNode.Primitive.Float(0)), ("m_Hidden", Unsupported("m_Hidden")));

            var result = Reconcile(source, live, @default);

            Assert.NotEmpty(result.Patch.Edits);
            var conflict = Assert.Single(result.Conflicts);
            Assert.Equal(ConflictKind.UnrepresentableValue, conflict.Kind);
            Assert.Null(conflict.RecurrenceKey);
            Assert.Empty(result.Notes);
        }
    }
}
