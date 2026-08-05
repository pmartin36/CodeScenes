using System.Collections.Generic;
using SceneBuilder.Core.Model;
using SceneBuilder.Core.Reconcile;

namespace SceneBuilder.Core.Diff
{
    // The one decision point (spec 35 D2, approach (a)): every authored component field, on both
    // the matched SetField branch and the create AddComponent branch of Differ.EmitComponentEdits,
    // is admitted through here before it can become a plan op. An excluded root, and every
    // sub-path of it, is dropped; one Conflict.UnauthorableField is appended per (component,
    // excluded root), not per sub-path.
    internal sealed class ExcludedFieldGate
    {
        private readonly IFieldExclusionPolicy _policy;
        private readonly List<Conflict> _reports;
        private readonly HashSet<(string ComponentLogicalId, string Root)> _reported = new();

        internal ExcludedFieldGate(IFieldExclusionPolicy policy, List<Conflict> reports)
        {
            _policy = policy;
            _reports = reports;
        }

        // Components with every excluded field key removed. Returns the SAME array instance when
        // nothing is excluded (the per-keystroke common case allocates nothing beyond the
        // IsExcluded calls themselves).
        internal ComponentData[] Admit(ComponentData[] components)
        {
            ComponentData[]? admitted = null;
            for (var i = 0; i < components.Length; i++)
            {
                var component = components[i];
                var filtered = AdmitFields(component);
                if (ReferenceEquals(filtered, component.Fields))
                {
                    continue;
                }

                admitted ??= (ComponentData[])components.Clone();
                admitted[i] = component with { Fields = filtered };
            }

            return admitted ?? components;
        }

        private FieldMap AdmitFields(ComponentData component)
        {
            List<KeyValuePair<string, ValueNode>>? kept = null;
            for (var i = 0; i < component.Fields.Count; i++)
            {
                var field = component.Fields[i];
                var root = Root(field.Key);
                if (_policy.IsExcluded(component.Type, root))
                {
                    kept ??= CopyUpTo(component.Fields, i);
                    if (_reported.Add((component.LogicalId, root)))
                    {
                        _reports.Add(Conflict.UnauthorableField(component.LogicalId, component.Type.FullName, root));
                    }

                    continue;
                }

                kept?.Add(field);
            }

            return kept is null ? component.Fields : new FieldMap(kept);
        }

        private static List<KeyValuePair<string, ValueNode>> CopyUpTo(FieldMap fields, int count)
        {
            var copy = new List<KeyValuePair<string, ValueNode>>(count);
            for (var i = 0; i < count; i++)
            {
                copy.Add(fields[i]);
            }

            return copy;
        }

        // Root segment of a serialized path, up to the first '.' or '[' — mirrors
        // SerializedFieldExclusions.RootSegment (com.codescenes/Editor/SerializedFieldExclusions.cs),
        // which is private there. Idempotent on an already-root path. Internal (not private) so
        // ExcludedFieldAudit shares this one Core-side copy instead of a third.
        internal static string Root(string path)
        {
            var dot = path.IndexOf('.');
            var bracket = path.IndexOf('[');
            var end = dot < 0 ? bracket : bracket < 0 ? dot : System.Math.Min(dot, bracket);
            return end < 0 ? path : path.Substring(0, end);
        }
    }
}
