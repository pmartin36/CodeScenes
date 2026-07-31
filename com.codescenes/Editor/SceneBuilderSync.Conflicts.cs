#nullable enable annotations
using System;
using System.Collections.Generic;
using System.Linq;
using SceneBuilder.Core.Diff;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Editor
{
    // The conflict-aware 3-way merge's attribution + marker
    // region, split out of SceneBuilderSync.cs per the project's file-size budget (the parent file
    // stays under the 1000-line limit). Move-only for everything except the arms explicitly
    // documented below as NEW/CHANGED for RectTransform field attribution.
    public static partial class SceneBuilderSync
    {
        /// <summary>
        /// Canonical field-key attribution: `(logicalId-or-componentLogicalId, fieldKey)`. A plain
        /// struct (not a positional record) — com.codescenes/Editor targets netstandard2.1 without an
        /// <c>IsExternalInit</c> polyfill in scope, unlike SceneBuilder.Core.
        /// </summary>
        private readonly struct FieldKey : IEquatable<FieldKey>
        {
            public readonly string Group;
            public readonly string Field;

            public FieldKey(string group, string field)
            {
                Group = group;
                Field = field;
            }

            public bool Equals(FieldKey other) => Group == other.Group && Field == other.Field;
            public override bool Equals(object obj) => obj is FieldKey other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Group, Field);
        }

        private sealed class ConflictInfo
        {
            public readonly FieldKey Key;
            public readonly string DisplayName;
            public readonly string SceneExpr;
            public readonly string CodeExpr;

            public ConflictInfo(FieldKey key, string displayName, string sceneExpr, string codeExpr)
            {
                Key = key;
                DisplayName = displayName;
                SceneExpr = sceneExpr;
                CodeExpr = codeExpr;
            }
        }

        private const string TransformFieldPrefix = "transform.";
        private static readonly string[] BaseTransformArgs = { "pos", "rot", "scale" };

        /// <summary>The ONE "is this a transform-family authoring argument?" test. The rect half reads
        /// Core's own table, so a field added there is attributed here without a second edit.</summary>
        private static bool IsTransformArg(string argName)
            => Array.IndexOf(BaseTransformArgs, argName) >= 0
               || RectTransformFields.TryFromArgName(argName, out _);

        /// <summary>
        /// Field-level `oldModel -&gt; newModel` diff for two DESIRED models (both parsed source, never
        /// live-scene) — the code-side half of the merge (see the caller's comment for why this
        /// is NOT <see cref="Differ.Diff"/> against a snapshot). Structural changes (a LogicalId present
        /// on only one side) are skipped: this merge is field-level only, matching
        /// <see cref="KeyOfSourceEdit"/>'s same structural pass-through on the scene side.
        /// </summary>
        private static List<ChangeOp> DiffDesiredFields(SceneModel oldModel, SceneModel newModel)
        {
            var oldByLogicalId = new Dictionary<string, GameObjectNode>();
            FlattenGameObjects(oldModel.Roots, oldByLogicalId);
            var newByLogicalId = new Dictionary<string, GameObjectNode>();
            FlattenGameObjects(newModel.Roots, newByLogicalId);

            var ops = new List<ChangeOp>();
            foreach (var (logicalId, newNode) in newByLogicalId)
            {
                if (!oldByLogicalId.TryGetValue(logicalId, out var oldNode))
                {
                    continue; // structurally new this batch — not a field-level edit.
                }

                if (!string.Equals(oldNode.Name, newNode.Name, StringComparison.Ordinal))
                {
                    ops.Add(new SetName { LogicalId = logicalId, Name = newNode.Name });
                }

                if (!string.Equals(oldNode.Tag, newNode.Tag, StringComparison.Ordinal))
                {
                    ops.Add(new SetTag { LogicalId = logicalId, Tag = newNode.Tag });
                }

                if (oldNode.Layer != newNode.Layer)
                {
                    ops.Add(new SetLayer { LogicalId = logicalId, Layer = newNode.Layer });
                }

                if (oldNode.Active != newNode.Active)
                {
                    ops.Add(new SetActive { LogicalId = logicalId, Active = newNode.Active });
                }

                if (oldNode.IsStatic != newNode.IsStatic)
                {
                    ops.Add(new SetStatic { LogicalId = logicalId, IsStatic = newNode.IsStatic });
                }

                // The base transform (Position/Rotation/Scale)
                // and the rect fields (anchoredPos/sizeDelta/anchorMin/anchorMax/pivot) are diffed and
                // attributed SEPARATELY — a whole-record TransformData compare makes a rect-only
                // code edit spuriously claim transform.pos/rot/scale (defect (b)).
                var oldT = oldNode.Transform;
                var newT = newNode.Transform;
                if (oldT.Position != newT.Position || oldT.Rotation != newT.Rotation || oldT.Scale != newT.Scale)
                {
                    ops.Add(new SetTransform { LogicalId = logicalId, Transform = newT });
                }

                var rectChanged = RectTransformFields.ChangedChannels(oldT, newT);
                if (rectChanged != ChannelMask.None)
                {
                    ops.Add(new SetRectTransform { LogicalId = logicalId, Transform = newT, Changed = rectChanged });
                }

                var oldComponentsByLogicalId = oldNode.Components.ToDictionary(c => c.LogicalId);
                foreach (var newComponent in newNode.Components)
                {
                    if (!oldComponentsByLogicalId.TryGetValue(newComponent.LogicalId, out var oldComponent))
                    {
                        continue; // component attached this batch — structural, not field-level.
                    }

                    foreach (var (fieldKey, newValue) in newComponent.Fields)
                    {
                        if (!oldComponent.Fields.TryGetValue(fieldKey, out var oldValue) || !Equals(oldValue, newValue))
                        {
                            ops.Add(new SetField
                            {
                                LogicalId = logicalId,
                                ComponentLogicalId = newComponent.LogicalId,
                                Path = fieldKey,
                                Value = newValue,
                            });
                        }
                    }
                }
            }

            return ops;
        }

        private static void FlattenGameObjects(IReadOnlyList<GameObjectNode> nodes, Dictionary<string, GameObjectNode> map)
        {
            foreach (var node in nodes)
            {
                map[node.LogicalId] = node;
                FlattenGameObjects(node.Children, map);
            }
        }

        /// <summary>Canonical key for a scene-side <see cref="SourceEdit"/> — null when unattributable (structural).</summary>
        private static FieldKey? KeyOfSourceEdit(
            SourceEdit edit,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans)
        {
            switch (edit)
            {
                case PatchArgument { ArgName: "name" } pa:
                    return new FieldKey(pa.Anchor, "name");
                // Rect argument names resolve too, so a scene-side RectTransform edit is attributed
                // rather than falling through to `default: return null` (unattributable), which would
                // mean it is ALWAYS kept regardless of whether the code side touched the same field,
                // silently reverting a concurrent code edit (defect (a)).
                case PatchArgument pa when IsTransformArg(pa.ArgName):
                    return new FieldKey(pa.Anchor, TransformFieldPrefix + pa.ArgName);
                case PatchFlagArgument pf:
                    return new FieldKey(pf.Anchor, FlagFieldName(pf.Flag));
                case PatchComponentField pcf:
                    if (fieldArgumentSpans.TryGetValue(pcf.Anchor, out var compSpans))
                    {
                        foreach (var (fieldKey, span) in compSpans)
                        {
                            if (span.Equals(pcf.ValueSpan))
                            {
                                return new FieldKey(pcf.Anchor, fieldKey);
                            }
                        }
                    }

                    return null;
                // A RemoveComponentField carries its FieldKey explicitly, so
                // no span reverse-lookup is needed, unlike PatchComponentField above. Attributing it
                // matters: an unmapped edit is ALWAYS kept (see the caller), so a live-value default
                // reset the auto-sync merge failed to attribute would silently clobber a concurrent
                // code edit to the SAME field.
                case RemoveComponentField rcf:
                    return new FieldKey(rcf.Anchor, rcf.FieldKey);
                default:
                    return null;
            }
        }

        /// <summary>Canonical key(s) for a code-side <see cref="ChangeOp"/> — a transform op yields all three sub-keys.</summary>
        private static IEnumerable<FieldKey> KeysOfChangeOp(ChangeOp op)
        {
            switch (op)
            {
                case SetName n:
                    yield return new FieldKey(n.LogicalId, "name");
                    break;
                case SetTag t:
                    yield return new FieldKey(t.LogicalId, "tag");
                    break;
                case SetLayer l:
                    yield return new FieldKey(l.LogicalId, "layer");
                    break;
                case SetActive a:
                    yield return new FieldKey(a.LogicalId, "active");
                    break;
                case SetStatic s:
                    yield return new FieldKey(s.LogicalId, "static");
                    break;
                case SetTransform tr:
                    yield return new FieldKey(tr.LogicalId, "transform.pos");
                    yield return new FieldKey(tr.LogicalId, "transform.rot");
                    yield return new FieldKey(tr.LogicalId, "transform.scale");
                    break;
                // Yields one key PER CHANGED rect field, never all
                // five — SetRectTransform carries the precise Changed mask, so over-claiming (like
                // SetTransform's three-key yield above) is avoidable and deliberately avoided here.
                case SetRectTransform rt:
                    foreach (var field in RectTransformFields.All)
                    {
                        if ((rt.Changed & field.Mask) != ChannelMask.None)
                        {
                            yield return new FieldKey(rt.LogicalId, TransformFieldPrefix + field.ArgName);
                        }
                    }

                    break;
                case SetField f:
                    yield return new FieldKey(f.ComponentLogicalId, f.Path);
                    break;
            }
        }

        private static string FlagFieldName(FlagKind flag) => flag switch
        {
            FlagKind.Tag => "tag",
            FlagKind.Layer => "layer",
            FlagKind.Active => "active",
            FlagKind.Static => "static",
            _ => "flag",
        };

        /// <summary>The SCENE-side rendered literal already computed by the reconcile for this edit.</summary>
        private static string SceneExprOfEdit(SourceEdit edit) => edit switch
        {
            PatchArgument pa => pa.NewExpr,
            PatchFlagArgument pf => pf.NewExpr,
            PatchComponentField pcf => pcf.NewExpr,
            // A removal has no scene LITERAL (the setter disappears, it isn't replaced by one) —
            // the default "" arm would render an empty `scene value  applied` marker, indistinguishable
            // from a real edit that lost its text. Named explicitly instead. The text itself is owned
            // by ConflictSurfacing so the plugin's one piece of
            // fixed copy written verbatim into a user's builder .cs stays ASCII-only in one place.
            RemoveComponentField => ConflictSurfacing.RemovedFieldMarkerValue,
            _ => "",
        };

        /// <summary>Renders the CODE-side (prior) value a conflicting <see cref="ChangeOp"/> carries, for the marker.</summary>
        private static string RenderPriorCodeExpr(ChangeOp op, string subKey) => op switch
        {
            SetName n => SourceExpr.StringLiteral(n.Name),
            SetTag t => SourceExpr.StringLiteral(t.Tag),
            SetLayer l => SourceExpr.IntLiteral(l.Layer),
            SetActive a => a.Active ? "true" : "false",
            SetStatic s => s.IsStatic ? "true" : "false",
            SetTransform tr => subKey switch
            {
                "transform.pos" => SourceExpr.Vec3Literal(tr.Transform.Position),
                "transform.scale" => SourceExpr.Vec3Literal(tr.Transform.Scale),
                _ => $"new UnityEngine.Quaternion({tr.Transform.Rotation.X}f, {tr.Transform.Rotation.Y}f, " +
                     $"{tr.Transform.Rotation.Z}f, {tr.Transform.Rotation.W}f)",
            },
            // The rect half of the marker: one Vec2 literal
            // per changed field, rendered via the SAME SourceExpr.Vec2Literal the reconcile's own
            // RectTransformEdits uses (Reconciler.RectTransform.cs), so a conflict marker's code value
            // never reads as a false difference from what the argument would have shown.
            SetRectTransform rt => RectTransformFields.TryFromArgName(
                    subKey.StartsWith(TransformFieldPrefix, StringComparison.Ordinal)
                        ? subKey.Substring(TransformFieldPrefix.Length) : subKey,
                    out var rectField)
                ? SourceExpr.Vec2Literal(rectField.Get(rt.Transform) ?? rectField.Default)
                : "?",
            SetField f => SourceExpr.ValueNodeLiteral(f.Value),
            _ => "?",
        };

        /// <summary>
        /// Inserts a `// CONFLICT:` comment line immediately above each conflicting statement/field, at
        /// its CURRENT (post-patch) position — never replacing the applied scene value inline. Processed
        /// bottom-to-top so each insertion leaves earlier offsets valid.
        /// </summary>
        private static string InsertConflictMarkers(
            string source,
            IReadOnlyDictionary<string, IReadOnlyDictionary<string, SourceSpan>> fieldArgumentSpans,
            IReadOnlyDictionary<string, SourceSpan> anchors,
            IReadOnlyList<ConflictInfo> conflicts)
        {
            var insertions = new List<(int Position, string Text)>();
            foreach (var c in conflicts)
            {
                int anchorStart;
                if (fieldArgumentSpans.TryGetValue(c.Key.Group, out var compSpans)
                    && compSpans.TryGetValue(c.Key.Field, out var valueSpan))
                {
                    anchorStart = valueSpan.Start;
                }
                else if (anchors.TryGetValue(c.Key.Group, out var goSpan))
                {
                    anchorStart = goSpan.Start;
                }
                else
                {
                    continue; // cannot relocate post-patch — should not happen, never lose the value though.
                }

                var clamped = Math.Min(anchorStart, source.Length);
                var lineStart = source.LastIndexOf('\n', Math.Max(clamped - 1, 0)) + 1;
                var indentEnd = lineStart;
                while (indentEnd < source.Length && (source[indentEnd] == ' ' || source[indentEnd] == '\t'))
                {
                    indentEnd++;
                }

                var indent = source.Substring(lineStart, indentEnd - lineStart);
                var markerLine = indent + ConflictSurfacing.BuildMarkerLine(c.Key.Field, c.CodeExpr, c.SceneExpr) + "\n";
                insertions.Add((lineStart, markerLine));
            }

            foreach (var (position, text) in insertions.OrderByDescending(i => i.Position))
            {
                source = source.Insert(position, text);
            }

            return source;
        }
    }
}
