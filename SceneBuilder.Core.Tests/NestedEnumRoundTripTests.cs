using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Parsing;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A field whose runtime type is a NESTED enum (e.g. UnityEngine.UI.ContentSizeFitter.FitMode)
    // carries a TypeFullName that admits two spellings of the same type identity: the adapter's live
    // read hands over the CLR reflection form (`+`), while a parsed builder file (and every rendered
    // literal) uses the C# source form (`.`). Both directions must treat the two spellings as the
    // SAME value, in both the reconcile (scene->code) and diff (code->scene) directions.
    public class NestedEnumRoundTripTests
    {
        private const string ComponentTypeFullName = "UnityEngine.UI.ContentSizeFitter";
        private const string FieldKey = "m_VerticalFit";
        private const string EnumTypeFullName = ComponentTypeFullName + ".FitMode";

        private static (ParseResult Parsed, IdentityMap Map) ParseWithMappedRootAndComponent(string source)
        {
            var parsed = BuilderParser.Parse(source);
            var ownerLogicalId = Assert.Single(parsed.Model.Roots).LogicalId;
            var componentLogicalId = $"{ownerLogicalId}/{ComponentTypeFullName}#0";

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
                        ComponentType = ComponentTypeFullName,
                        ParentLogicalId = ownerLogicalId,
                    },
                },
            };

            return (parsed, map);
        }

        private static SceneSnapshot SnapshotWithLiveEnumField(string clrMember)
        {
            var liveField = new ValueNode.Enum(EnumTypeFullName.Replace('.', '+'), new[] { clrMember }, IsFlags: false);
            return new SceneSnapshot
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
                                Type = new TypeRef(ComponentTypeFullName),
                                Fields = new FieldMap(new[] { new KeyValuePair<string, ValueNode>(FieldKey, liveField) }),
                            },
                        },
                    },
                },
            };
        }

        private static string Source(string enumLiteral) => $@"
public class Scene1 : ISceneDefinition
{{
    public void Build(SceneRoot scene)
    {{
        var root = scene.Add(""Root"").Component<{ComponentTypeFullName}>(c => c.Set(""{FieldKey}"", {enumLiteral}));
    }}
}}
";

        // The measured churn loop: an authored value equal to the live value (both naming the SAME
        // enum member, spelled differently) must reconcile to zero edits, on the first pass and again
        // on a second — a non-zero PatchEdits on an unchanged snapshot is the auto-sync churn signature.
        [Fact]
        public void Reconcile_NestedEnumFieldUnchanged_EmitsNoEditsAndConvergesOnSecondPass()
        {
            var (parsed, map) = ParseWithMappedRootAndComponent(Source($"{EnumTypeFullName}.PreferredSize"));
            var snapshot = SnapshotWithLiveEnumField("PreferredSize");

            var firstPass = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);
            Assert.Empty(firstPass.Patch.Edits);
            Assert.Empty(firstPass.Conflicts);

            var secondPass = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);
            Assert.Empty(secondPass.Patch.Edits);
            Assert.Empty(secondPass.Conflicts);
        }

        // Negative lock: the fix is "the two spellings compare equal", not "stop emitting". A genuine
        // member change must still patch, and the replacement text must be the COMPILABLE dotted
        // spelling, never the CLR `+` form the live read produced.
        [Fact]
        public void Reconcile_NestedEnumFieldChanged_EmitsPatchWithSourceSpelling()
        {
            var (parsed, map) = ParseWithMappedRootAndComponent(Source($"{EnumTypeFullName}.PreferredSize"));
            var snapshot = SnapshotWithLiveEnumField("MinSize");

            var result = Reconciler.Reconcile(
                parsed.Model, snapshot, map, parsed.Anchors, null, null, parsed.ComponentAnchors, parsed.FieldArgumentSpans);

            var patch = Assert.Single(result.Patch.Edits.OfType<PatchComponentField>());
            Assert.Equal(EnumTypeFullName + ".MinSize", patch.NewExpr);
            Assert.DoesNotContain("+", patch.NewExpr);
        }

        // Code->scene half of the same identity: a model field parsed in the dotted source spelling
        // must diff as EQUAL to a live snapshot value read in the CLR `+` spelling — no spurious
        // SetField on every Build.
        [Fact]
        public void Diff_NestedEnumFieldMatchesLiveValue_EmitsNoOps()
        {
            var desiredComponent = new ComponentData
            {
                LogicalId = "root-1/" + ComponentTypeFullName + "#0",
                Type = new TypeRef(ComponentTypeFullName),
                Fields = new FieldMap(new[]
                {
                    new KeyValuePair<string, ValueNode>(
                        FieldKey, new ValueNode.Enum(EnumTypeFullName, new[] { "PreferredSize" }, IsFlags: false)),
                }),
            };
            var root = new GameObjectNode { LogicalId = "root-1", Name = "Root", Components = new[] { desiredComponent } };
            var model = new SceneModel { SchemaVersion = 1, Roots = new[] { root } };

            var snapshot = SnapshotWithLiveEnumField("PreferredSize");
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
                },
            };

            var changeSet = Differ.Diff(model, snapshot, map);

            Assert.Empty(changeSet.Ops);
        }
    }
}
