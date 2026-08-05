using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // Every UnrepresentableValue conflict/note carries the object anchor, the component type full
    // name and the field/member key -- a check no future report of that kind can bypass, because
    // Conflict.Kind can only be set to UnrepresentableValue through Conflict.FromReport, which
    // requires a LocatedReport (no-default constructor: LogicalId, ComponentTypeFullName,
    // MemberKey, Detail).
    public class UnrepresentableLocatedDataTests
    {
        private const string CompType = "UnityEngine.UI.Text";
        private const string FieldKey = "m_FontData";
        private const string TypeName = "UnityEngine.UI.FontData";

        private static ValueNode.Nested Node(params (string Key, ValueNode Value)[] fields) =>
            new(TypeName, new FieldMap(fields.Select(f => new KeyValuePair<string, ValueNode>(f.Key, f.Value))));

        private static ValueNode.Primitive Float(float v) => ValueNode.Primitive.Float(v);

        private static ValueNode.Unsupported Unsupported(string member) =>
            new($"no public member for '{member}'");

        private static ValueNode.AssetRef Font(string? guid) =>
            new(guid is null ? null : new AssetRef { Guid = guid, DisplayPath = "Assets/MyFont.ttf", TypeHint = "Font" });

        // Same fixture shape as UnrepresentableNoteTests.Reconcile: one component whose field is
        // `source` in builder source (null = absent), `live` in the snapshot, `default` in the
        // per-type default template.
        private static ReconcileResult Reconcile(
            ValueNode? source, ValueNode live, ValueNode @default, string ownerLogicalId = "root-1")
        {
            var componentLogicalId = $"{ownerLogicalId}/{CompType}#0";
            var sourceFields = source is null
                ? new FieldMap(Array.Empty<KeyValuePair<string, ValueNode>>())
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
        public void Reconcile_EveryUnrepresentableReport_CarriesTheRequiredLocatedData()
        {
            var typeScoped = Node(("m_Hidden", Unsupported("m_Hidden")));
            var typeScopedResult = Reconcile(source: null, live: typeScoped, @default: typeScoped);

            var instanceLive = Node(("font", Font("guid-1")), ("fontSize", ValueNode.Primitive.Int(14)));
            var instanceDefault = Node(("font", Font(null)), ("fontSize", ValueNode.Primitive.Int(14)));
            var instanceScopedResult = Reconcile(source: null, live: instanceLive, @default: instanceDefault);

            var mixedSource = Node(("visible", Float(0)));
            var mixedLive = Node(("visible", Float(3)), ("m_Hidden", Unsupported("m_Hidden")));
            var mixedDefault = Node(("visible", Float(0)), ("m_Hidden", Unsupported("m_Hidden")));
            var mixedResult = Reconcile(mixedSource, mixedLive, mixedDefault);

            var reports = new[] { typeScopedResult, instanceScopedResult, mixedResult }
                .SelectMany(r => r.Conflicts.Concat(r.Notes))
                .Where(c => c.Kind == ConflictKind.UnrepresentableValue)
                .ToArray();

            Assert.NotEmpty(reports);
            foreach (var report in reports)
            {
                Assert.NotNull(report.Located);
                Assert.False(string.IsNullOrEmpty(report.Located!.LogicalId));
                Assert.False(string.IsNullOrEmpty(report.Located.ComponentTypeFullName));
                Assert.False(string.IsNullOrEmpty(report.Located.MemberKey));
                Assert.Equal(report.Located.Compose(), report.Reason);
                Assert.EndsWith("The live value stays in the scene and is NOT synced to code.", report.Reason);
            }
        }

        // Conflict.Unrepresentable is the only public door for an UnrepresentableValue report;
        // FromReport is the private mechanism it (and AmbiguousTypeName) shares internally, so a
        // fourth site cannot build the kind another way -- it fails to compile.
        [Fact]
        public void Conflict_HasNoPublicGenericDoorForAGuardedKind()
        {
            Assert.Null(typeof(Conflict).GetMethod("FromReport", BindingFlags.Public | BindingFlags.Static));
        }

        // Reflection sweep over every ConflictDetector factory that can produce a Conflict, so a
        // fourth UnrepresentableValue-producing factory cannot slip past this check by having an
        // unusual signature: it either routes through the located channel or fails here.
        [Fact]
        public void ConflictDetector_EveryFactoryProducingTheKind_GoesThroughTheLocatedChannel()
        {
            var factories = typeof(ConflictDetector)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(Conflict))
                .ToArray();

            Assert.NotEmpty(factories);

            foreach (var factory in factories)
            {
                var args = factory.GetParameters().Select(p => SynthesizeArg(p.ParameterType)).ToArray();
                var conflict = (Conflict)factory.Invoke(null, args)!;

                if (conflict.Kind != ConflictKind.UnrepresentableValue)
                {
                    continue;
                }

                Assert.NotNull(conflict.Located);
                Assert.False(string.IsNullOrEmpty(conflict.Located!.LogicalId));
                Assert.False(string.IsNullOrEmpty(conflict.Located.ComponentTypeFullName));
                Assert.False(string.IsNullOrEmpty(conflict.Located.MemberKey));
            }
        }

        private static object? SynthesizeArg(Type t)
        {
            if (t == typeof(string)) return "x";
            if (t == typeof(IEnumerable<string>)) return new[] { "m" };
            if (t == typeof(SourceSpan?)) return null;
            if (t == typeof(ValueNode)) return ValueNode.Primitive.Int(0);
            if (t == typeof(OverrideTarget)) return new OverrideTarget();
            throw new NotSupportedException($"extend the argument synthesizer for {t}");
        }

        [Fact]
        public void Conflict_SettingTheKindDirectly_Throws()
        {
            Assert.Throws<ArgumentException>(() => new Conflict { Kind = ConflictKind.UnrepresentableValue });
        }

        [Fact]
        public void LocatedReport_WithScenePath_RecomposesTheReasonAndKeepsTheKind()
        {
            var conflict = Conflict.Unrepresentable(
                "comp-1", CompType, FieldKey, "detail text", recurrenceKey: null, location: null);

            var moved = conflict with { Located = conflict.Located! with { ScenePath = "Canvas/Text" } };

            Assert.Contains("Canvas/Text", moved.Reason);
            Assert.Equal(ConflictKind.UnrepresentableValue, moved.Kind);
            Assert.Equal(conflict.RecurrenceKey, moved.RecurrenceKey);
        }
    }
}
