using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using SceneBuilder.Core.Identity;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;
using Xunit;

namespace SceneBuilder.Core.Tests
{
    // A bare (non-list) snapshot Unsupported whose RawToken cannot compile (a naked scene-read
    // sentinel like "ObjectReference") must classify Unemittable at every plain snapshot-emit site
    // (EmitComponentAppend, BuildAddInstanceComponent, BuildOverrideSetSpec), never render straight
    // through as a phantom identifier. BuildOverrideSetSpec must also route a dangling ObjectRef
    // through the same omit-and-report gate the two append sites already apply. A legitimately
    // compiling escape-hatch token (a call, a member access, a literal) must keep round-tripping
    // verbatim with no conflict, and a same-batch pending target must be omitted silently.
    public class SnapshotEmitClassificationTests
    {
        // ---- IsUnemittableUnsupported predicate table -------------------------------------------

        [Theory]
        [InlineData("ObjectReference")]
        [InlineData("Integer")]
        [InlineData("Generic")]
        [InlineData("no public member for 'x'")]
        [InlineData("PPtr<$GameObject>")]
        [InlineData("")]
        public void IsUnemittableUnsupported_NonCompilingOrSentinelToken_IsTrue(string rawToken)
        {
            Assert.True(ListValueEmission.IsUnemittableUnsupported(new ValueNode.Unsupported(rawToken)));
        }

        [Theory]
        [InlineData("SomeWeirdExpr()")]
        [InlineData("Mathf.PI")]
        [InlineData("42")]
        [InlineData("\"x\"")]
        [InlineData("new UnityEngine.Vector3(1f,2f,3f)")]
        public void IsUnemittableUnsupported_CompilingEscapeHatchToken_IsFalse(string rawToken)
        {
            Assert.False(ListValueEmission.IsUnemittableUnsupported(new ValueNode.Unsupported(rawToken)));
        }

        // ---- Defect 1 x EmitComponentAppend (plain new component) -------------------------------

        private static IdentityMap RootMap() => new IdentityMap
        {
            Entries = new[]
            {
                new IdentityMapEntry { LogicalId = "root-1", GlobalObjectId = "goid-root", Kind = "GameObject" },
            },
        };

        private static ReconcileResult ReconcileAppendedComponent(ValueNode centerOfMassValue)
        {
            var model = new SceneModel
            {
                SchemaVersion = 1,
                Roots = new[] { new GameObjectNode { LogicalId = "root-1", Name = "Root" } },
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
                                Type = new TypeRef("UnityEngine.Rigidbody"),
                                Fields = new FieldMap(new[]
                                {
                                    new KeyValuePair<string, ValueNode>("m_Mass", ValueNode.Primitive.Float(5f)),
                                    new KeyValuePair<string, ValueNode>("m_CenterOfMass", centerOfMassValue),
                                }),
                            },
                        },
                    },
                },
            };

            return Reconciler.Reconcile(model, snapshot, RootMap());
        }

        [Fact]
        public void EmitComponentAppend_BareNonCompilingUnsupportedField_OmitsFieldAndReportsUnrepresentableValue()
        {
            var result = ReconcileAppendedComponent(new ValueNode.Unsupported("ObjectReference"));

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.False(append.Fields.TryGetValue("m_CenterOfMass", out _));
            Assert.Equal(ValueNode.Primitive.Float(5f), append.Fields["m_Mass"]);

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal("root-1/UnityEngine.Rigidbody#0", note.Located!.LogicalId);
            Assert.Equal("UnityEngine.Rigidbody", note.Located!.ComponentTypeFullName);
            Assert.Equal("m_CenterOfMass", note.Located!.MemberKey);
        }

        [Fact]
        public void EmitComponentAppend_BareCompilingEscapeHatchToken_RoundTripsVerbatim_NoConflict()
        {
            var result = ReconcileAppendedComponent(new ValueNode.Unsupported("SomeWeirdExpr()"));

            var append = Assert.Single(result.Patch.Edits.OfType<AppendComponentStatement>());
            Assert.Equal(new ValueNode.Unsupported("SomeWeirdExpr()"), append.Fields["m_CenterOfMass"]);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Notes);
        }

        // ---- Shared prefab-instance fixture (BuildAddInstanceComponent / BuildOverrideSetSpec) --

        private const string PrefabGuid = "guid-enemy-prefab";
        private const string PrefabPath = "Assets/Prefabs/Enemy.prefab";
        private const string DoorOpenerType = "Game.DoorOpener";

        private static OverrideTarget DoorOpenerTarget() => new() { ComponentType = DoorOpenerType };

        private static (SceneModel model, SceneSnapshot snapshot, IdentityMap map) BuildMatchedInstance(
            PropertyOverride[]? snapshotOverrides = null,
            AddedComponent[]? snapshotAddedComponents = null,
            IEnumerable<SnapshotNode>? extraSnapshotRoots = null)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                Overrides = snapshotOverrides ?? System.Array.Empty<PropertyOverride>(),
                AddedComponents = snapshotAddedComponents ?? System.Array.Empty<AddedComponent>(),
            };

            var roots = new List<SnapshotNode> { snapshotInstance };
            if (extraSnapshotRoots != null)
            {
                roots.AddRange(extraSnapshotRoots);
            }

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = roots.ToArray() };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            return (model, snapshot, map);
        }

        // ---- Defect 1 x BuildAddInstanceComponent (added component on a prefab instance) --------

        [Fact]
        public void BuildAddInstanceComponent_BareNonCompilingUnsupportedField_OmitsFieldAndReportsUnrepresentableValue()
        {
            var fields = new FieldMap(new[]
            {
                new KeyValuePair<string, ValueNode>("target", new ValueNode.Unsupported("ObjectReference")),
            });
            var added = new AddedComponent
            {
                Target = DoorOpenerTarget(),
                Component = new ComponentData { LogicalId = "opener-1", Type = new TypeRef(DoorOpenerType), Fields = fields },
            };
            var (model, snapshot, map) = BuildMatchedInstance(snapshotAddedComponents: new[] { added });

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddComponent>());
            Assert.False(append.Fields.ContainsKey("target"));
            Assert.False(append.FieldExpressions != null && append.FieldExpressions.ContainsKey("target"));

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal(DoorOpenerType, note.Located!.ComponentTypeFullName);
            Assert.Equal("target", note.Located!.MemberKey);
        }

        // ---- Defect 1 x BuildOverrideSetSpec ------------------------------------------------------

        [Fact]
        public void BuildOverrideSetSpec_BareNonCompilingUnsupportedValue_OmitsSetAndReportsUnrepresentableValue()
        {
            var snapshotOverride = new PropertyOverride
            {
                Target = DoorOpenerTarget(),
                PropertyPath = "target",
                Value = new ValueNode.Unsupported("ObjectReference"),
            };
            var (model, snapshot, map) = BuildMatchedInstance(snapshotOverrides: new[] { snapshotOverride });

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal("instance-1/" + DoorOpenerType, note.Located!.LogicalId);
            Assert.Equal("target", note.Located!.MemberKey);
        }

        [Fact]
        public void BuildOverrideSetSpec_BareCompilingEscapeHatchToken_RoundTripsVerbatim_NoConflict()
        {
            var snapshotOverride = new PropertyOverride
            {
                Target = DoorOpenerTarget(),
                PropertyPath = "target",
                Value = new ValueNode.Unsupported("Mathf.PI"),
            };
            var (model, snapshot, map) = BuildMatchedInstance(snapshotOverrides: new[] { snapshotOverride });

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceOverride>());
            var set = Assert.Single(append.Sets);
            Assert.Equal("Mathf.PI", set.ValueExpression);
            Assert.Empty(result.Conflicts);
            Assert.Empty(result.Notes);
        }

        // ---- Defect 2 x BuildOverrideSetSpec: dangling ObjectRef ---------------------------------

        [Fact]
        public void BuildOverrideSetSpec_UnmappedObjectRefTarget_OmitsSetAndReportsDanglingReference()
        {
            var snapshotOverride = new PropertyOverride
            {
                Target = DoorOpenerTarget(),
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef("ghost-1"),
            };
            var (model, snapshot, map) = BuildMatchedInstance(snapshotOverrides: new[] { snapshotOverride });

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            Assert.Equal("instance-1/" + DoorOpenerType, conflict.LogicalId);
        }

        // ---- Pending x BuildOverrideSetSpec: same-batch unmapped target is silent ----------------

        [Fact]
        public void BuildOverrideSetSpec_SameBatchPendingTarget_OmitsSetSilently()
        {
            const string pendingGoid = "goid-newtarget";
            var snapshotOverride = new PropertyOverride
            {
                Target = DoorOpenerTarget(),
                PropertyPath = "target",
                ObjectReference = new ValueNode.ObjectRef(pendingGoid),
            };
            var (model, snapshot, map) = BuildMatchedInstance(
                snapshotOverrides: new[] { snapshotOverride },
                extraSnapshotRoots: new[] { new SnapshotNode { GlobalObjectId = pendingGoid, Name = "NewTarget" } });

            var result = Reconciler.Reconcile(model, snapshot, map);

            Assert.Empty(result.Patch.Edits.OfType<AppendInstanceOverride>());
            Assert.Empty(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Empty(result.Notes.Where(n => n.Kind == ConflictKind.UnrepresentableValue));
        }

        // ---- Defect 1/2 x ReconcileAddedGameObjects (added-child component field) ---------------
        //
        // The added-child snapshot-emit site routes through the SAME EmitFieldSet classification the
        // three plain sites above already run — pinned here so the shared bypass check keeps covering
        // all four sites, not just three.

        private static (SceneModel model, SceneSnapshot snapshot, IdentityMap map) BuildInstanceWithAddedChildField(
            ValueNode targetValue)
        {
            var instance = new PrefabInstanceNode
            {
                LogicalId = "instance-1",
                Name = "Enemy",
                SourcePrefab = new AssetRef { Guid = PrefabGuid, DisplayPath = PrefabPath },
            };

            var added = new AddedGameObject
            {
                Parent = new OverrideTarget { ChildPath = "" },
                Node = new GameObjectNode
                {
                    Name = "MuzzleFlash",
                    Components = new[]
                    {
                        new ComponentData
                        {
                            Type = new TypeRef(DoorOpenerType),
                            Fields = new FieldMap(new[]
                            {
                                new KeyValuePair<string, ValueNode>("target", targetValue),
                            }),
                        },
                    },
                },
            };

            var snapshotInstance = new SnapshotNode
            {
                GlobalObjectId = "goid-instance-1",
                Name = "Enemy",
                SourcePrefabGuid = PrefabGuid,
                AddedGameObjects = new[] { added },
            };

            var model = new SceneModel { SchemaVersion = 1, Roots = new GameObjectNode[] { instance } };
            var snapshot = new SceneSnapshot { SchemaVersion = 1, Roots = new[] { snapshotInstance } };
            var map = new IdentityMap
            {
                Entries = new[]
                {
                    new IdentityMapEntry
                    {
                        LogicalId = "instance-1", GlobalObjectId = "goid-instance-1", Kind = "PrefabInstance",
                        SourcePrefabGuid = PrefabGuid,
                    },
                },
            };

            return (model, snapshot, map);
        }

        [Fact]
        public void AddedChildGameObject_BareNonCompilingUnsupportedField_OmitsFieldAndReportsUnrepresentableValue()
        {
            var (model, snapshot, map) = BuildInstanceWithAddedChildField(new ValueNode.Unsupported("ObjectReference"));

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            var component = Assert.Single(append.Node.Components);
            Assert.False(component.Fields.TryGetValue("target", out _));

            var note = Assert.Single(result.Notes);
            Assert.Equal(ConflictKind.UnrepresentableValue, note.Kind);
            Assert.Equal("instance-1/" + DoorOpenerType + "#0", note.Located!.LogicalId);
            Assert.Equal(DoorOpenerType, note.Located!.ComponentTypeFullName);
            Assert.Equal("target", note.Located!.MemberKey);
        }

        [Fact]
        public void AddedChildGameObject_UnmappedObjectRefTarget_OmitsFieldAndReportsDanglingReference()
        {
            var (model, snapshot, map) = BuildInstanceWithAddedChildField(new ValueNode.ObjectRef("ghost-1"));

            var result = Reconciler.Reconcile(model, snapshot, map);

            var append = Assert.Single(result.Patch.Edits.OfType<AppendInstanceAddChild>());
            var component = Assert.Single(append.Node.Components);
            Assert.False(component.Fields.TryGetValue("target", out _));

            var conflict = Assert.Single(result.Conflicts.Where(c => c.Kind == ConflictKind.DanglingReference));
            Assert.Contains("ghost-1", conflict.Reason);
            Assert.Equal("instance-1/" + DoorOpenerType + "#0", conflict.LogicalId);
        }

        // ---- Structural bypass guard --------------------------------------------------------------
        //
        // Every call to ComponentReconciler.RenderFieldValue and SourceExpr.ValueNodeLiteral is
        // declared here with its member and whether that member is required to also call
        // ComponentReconciler.ClassifySnapshotRef (the one decision point). An undeclared (file,
        // member) site fails outright — a new snapshot-emit call site must be consciously routed
        // through the classifier or declared exempt with a reason, never silently added. A declared
        // Guarded site whose member does not itself invoke ClassifySnapshotRef fails too.

        private sealed record Site(string RelativePath, string Member, int Count, bool RequiresGuard, string Reason);

        private static readonly Site[] RenderFieldValueSites =
        {
            new("SceneBuilder.Core/Reconcile/ComponentReconciler.cs", "ReconcileComponents", 3, true,
                "Detection 2 patch and both introduce branches; each is preceded by its own ClassifySnapshotRef call"),
            new("SceneBuilder.Core/Reconcile/SnapshotFieldEmission.cs", "EmitFieldSet", 1, true,
                "the classified choke point shared by EmitComponentAppend and BuildAddInstanceComponent"),
            new("SceneBuilder.Core/Reconcile/ReconcilerInstances.cs", "BuildOverrideSetSpec", 1, true,
                "the override snapshot-emit site this task brings onto the gate"),
            new("SceneBuilder.Core/Reconcile/ComponentReconciler.ManagedRef.cs", "TryReconcileManagedRefField", 1, false,
                "managed-ref field render, not a snapshot ObjectRef/Unsupported concern"),
        };

        private static readonly Site[] ValueNodeLiteralSites =
        {
            new("SceneBuilder.Core/Reconcile/ComponentReconciler.cs", "RenderFieldValue", 2, false,
                "internal to the trusted render function; classification already ran in the caller"),
            new("SceneBuilder.Core/Reconcile/ManagedReferenceEmission.cs", "RenderMember", 1, false,
                "managed-ref member render, own path"),
            new("SceneBuilder.Core/Reconcile/SpatialComponentSource.cs", "RenderFieldValue", 1, false,
                "spatial value render, no ObjectRef/Unsupported concern"),
            new("SceneBuilder.Core/Reconcile/SourcePatchApplier.Instances.cs", "RenderOverrideSetCall", 1, false,
                "apply-time formatter; a value classified Unemittable is never added as an OverrideSetSpec"),
            // The call site sits in the `Render` LOCAL FUNCTION nested inside RenderComponentClosureArgs.
            new("SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs", "Render", 1, false,
                "apply-time formatter"),
            new("SceneBuilder.Core/Reconcile/ComponentPatchApplier.cs", "ResolveIntroduceComponentField", 1, false,
                "apply-time formatter"),
        };

        private static readonly string[] ScanRoots = { "SceneBuilder.Core" };

        private static IEnumerable<string> ProductionFiles(string repoRoot) =>
            SourceScanPlumbing.ProductionFiles(repoRoot, ScanRoots, _ => false);

        private readonly record struct Match(string Member, int Line);

        // Every invocation of `name`, either unqualified inside a file whose name starts with
        // `unqualifiedOwnerFilePrefix`, or qualified as `qualifier.name` anywhere.
        private static IEnumerable<Match> InvocationsOf(
            string sourceText, string relativePath, string name, string qualifier, string unqualifiedOwnerFilePrefix)
        {
            var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
            var root = tree.GetRoot();
            var isOwnerFile = Path.GetFileName(relativePath).StartsWith(unqualifiedOwnerFilePrefix, System.StringComparison.Ordinal);

            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                string? calleeName = null;
                string? calleeQualifier = null;

                switch (invocation.Expression)
                {
                    case IdentifierNameSyntax id:
                        calleeName = id.Identifier.Text;
                        break;
                    case MemberAccessExpressionSyntax member:
                        calleeName = member.Name.Identifier.Text;
                        calleeQualifier = (member.Expression as IdentifierNameSyntax)?.Identifier.Text;
                        break;
                }

                if (calleeName != name)
                {
                    continue;
                }

                var isMatch = calleeQualifier == qualifier || (calleeQualifier == null && isOwnerFile);
                if (!isMatch)
                {
                    continue;
                }

                var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                yield return new Match(EnclosingMemberName(invocation), line);
            }
        }

        private static string EnclosingMemberName(SyntaxNode node)
        {
            for (var n = node.Parent; n != null; n = n.Parent)
            {
                switch (n)
                {
                    case MethodDeclarationSyntax m: return m.Identifier.Text;
                    case LocalFunctionStatementSyntax l: return l.Identifier.Text;
                    case ConstructorDeclarationSyntax c: return c.Identifier.Text;
                }
            }

            return "<top-level>";
        }

        // True when the SyntaxNode's enclosing method/local-function/constructor body ALSO contains
        // an identifier token naming `ClassifySnapshotRef` — i.e. this call site's value was
        // classified in the same member before (or via) reaching the render primitive.
        private static bool EnclosingMemberCallsClassifySnapshotRef(SyntaxNode node)
        {
            SyntaxNode? memberNode = null;
            for (var n = node.Parent; n != null; n = n.Parent)
            {
                if (n is MethodDeclarationSyntax || n is LocalFunctionStatementSyntax || n is ConstructorDeclarationSyntax)
                {
                    memberNode = n;
                    break;
                }
            }

            return memberNode != null && memberNode.DescendantTokens()
                .Any(t => t.Kind() == SyntaxKind.IdentifierToken && t.Text == "ClassifySnapshotRef");
        }

        private static List<string> Violations(
            string repoRoot, Site[] declared, string calleeName, string qualifier, string unqualifiedOwnerFilePrefix)
        {
            var declaredByKey = declared.ToDictionary(s => (File: s.RelativePath, Member: s.Member), s => s);
            var actualCounts = new Dictionary<(string File, string Member), int>();
            // One representative invocation node (as SyntaxNode) per (file, member), to test the
            // guard requirement without re-parsing.
            var actualGuarded = new Dictionary<(string File, string Member), bool>();

            foreach (var relativePath in ProductionFiles(repoRoot))
            {
                var sourceText = File.ReadAllText(Path.Combine(repoRoot, relativePath));
                var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath);
                var root = tree.GetRoot();
                var isOwnerFile = Path.GetFileName(relativePath).StartsWith(unqualifiedOwnerFilePrefix, System.StringComparison.Ordinal);

                foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    string? calleeText = null;
                    string? calleeQualifier = null;
                    switch (invocation.Expression)
                    {
                        case IdentifierNameSyntax id:
                            calleeText = id.Identifier.Text;
                            break;
                        case MemberAccessExpressionSyntax member:
                            calleeText = member.Name.Identifier.Text;
                            calleeQualifier = (member.Expression as IdentifierNameSyntax)?.Identifier.Text;
                            break;
                    }

                    if (calleeText != calleeName)
                    {
                        continue;
                    }

                    var isMatch = calleeQualifier == qualifier || (calleeQualifier == null && isOwnerFile);
                    if (!isMatch)
                    {
                        continue;
                    }

                    var key = (File: relativePath, Member: EnclosingMemberName(invocation));
                    actualCounts[key] = actualCounts.GetValueOrDefault(key) + 1;
                    actualGuarded[key] = EnclosingMemberCallsClassifySnapshotRef(invocation);
                }
            }

            var violations = new List<string>();
            foreach (var key in declaredByKey.Keys.Union(actualCounts.Keys))
            {
                var expectedCount = declaredByKey.TryGetValue(key, out var site) ? site.Count : 0;
                var actualCount = actualCounts.GetValueOrDefault(key, 0);
                if (expectedCount != actualCount)
                {
                    violations.Add($"{key.File} :: {key.Member} -- expected {expectedCount} call(s) to {calleeName}, found {actualCount}. " +
                        "Route this site through ComponentReconciler.ClassifySnapshotRef, or declare it in the inventory with its reason.");
                    continue;
                }

                if (site != null && site.RequiresGuard && actualCount > 0 && !actualGuarded.GetValueOrDefault(key))
                {
                    violations.Add($"{key.File} :: {key.Member} -- calls {calleeName} but its member never calls " +
                        "ComponentReconciler.ClassifySnapshotRef; an Unemittable/Dangling/Pending value would render straight through.");
                }
            }

            return violations;
        }

        [Fact]
        public void ProductionSource_EveryRenderFieldValueSite_IsDeclaredAndGuardedPerInventory()
        {
            var repoRoot = RepoRootLocator.Find();
            var violations = Violations(repoRoot, RenderFieldValueSites, "RenderFieldValue", "ComponentReconciler", "ComponentReconciler");

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        [Fact]
        public void ProductionSource_EveryValueNodeLiteralSite_IsDeclaredPerInventory()
        {
            var repoRoot = RepoRootLocator.Find();
            var violations = Violations(repoRoot, ValueNodeLiteralSites, "ValueNodeLiteral", "SourceExpr", "SourceExpr");

            Assert.True(violations.Count == 0, string.Join("\n", violations));
        }

        // Proves the guard check is real, not vacuous: a synthetic file with an undeclared member
        // calling ComponentReconciler.RenderFieldValue with no ClassifySnapshotRef in sight must be
        // flagged, both for being undeclared AND for lacking the guard.
        [Fact]
        public void SyntheticUndeclaredUnguardedSite_IsFlagged()
        {
            const string relativePath = "SceneBuilder.Core/Reconcile/SomeOtherPass.cs";
            const string synthetic = @"
namespace SceneBuilder.Core.Reconcile
{
    internal static class SomeOtherPass
    {
        private static string Undeclared(ValueNode value)
        {
            return ComponentReconciler.RenderFieldValue(value, ""T"", null, null, ""owner"", null);
        }
    }
}
";
            var matches = InvocationsOf(synthetic, relativePath, "RenderFieldValue", "ComponentReconciler", "ComponentReconciler").ToList();
            var match = Assert.Single(matches);
            Assert.Equal("Undeclared", match.Member);

            var declared = RenderFieldValueSites.Any(s => s.RelativePath == relativePath && s.Member == "Undeclared");
            Assert.False(declared, "the synthetic site must not already be in the declared inventory");
        }
    }
}
