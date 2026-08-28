using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    // A (component TYPE, member name) pair the adapter has PROVEN is a safe, selectable public C#
    // member of the component's declaring type -- `r => r.MemberName` compiles AND names the real
    // serialized member. Positive-proof whitelist (spec 54): absence means NOT proven safe, never
    // "assumed fine". MemberName is the public selector spelling a user/LLM would author (a public
    // field name, or a native-backed public property's conventional name, e.g. "mass"/"renderMode").
    public sealed record SafeMember
    {
        [JsonPropertyOrder(0)]
        public TypeRef Type { get; init; } = new TypeRef("");

        [JsonPropertyOrder(1)]
        public string MemberName { get; init; } = "";
    }

    // Lookup over SceneSnapshot.SafeMembers: an empty/absent index proves NOTHING safe
    // (deny-by-default), so every typed selector downgrades until a producer actually populates
    // it. Keyed on (Type.FullName, MemberName), ordinal equality; first-wins on a duplicate.
    public sealed class SafeMemberIndex
    {
        public static readonly SafeMemberIndex Empty = new SafeMemberIndex(Array.Empty<SafeMember>(), null);

        private readonly HashSet<(string TypeFullName, string MemberName)> _safe;

        // Types the adapter has actually READ (via ReadComponent / a type-level template), whether or
        // not that read produced any safe member. Distinguishes "type not inspected" (no knowledge,
        // never rewrite) from "member not safe" (positive proof, safe to rewrite) -- load-bearing only
        // for a self-heal that rewrites already-converged, already-compiling source.
        private readonly HashSet<string> _inspected;

        private SafeMemberIndex(IReadOnlyList<SafeMember> members, string[]? inspectedTypes)
        {
            _safe = new HashSet<(string, string)>();
            _inspected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var member in members)
            {
                _safe.Add((member.Type.FullName, member.MemberName));
                _inspected.Add(member.Type.FullName);
            }

            if (inspectedTypes != null)
            {
                foreach (var typeFullName in inspectedTypes)
                {
                    _inspected.Add(typeFullName);
                }
            }
        }

        public static SafeMemberIndex Build(SafeMember[]? members, string[]? inspectedTypes = null) =>
            (members is null || members.Length == 0) && (inspectedTypes is null || inspectedTypes.Length == 0)
                ? Empty
                : new SafeMemberIndex(members ?? Array.Empty<SafeMember>(), inspectedTypes);

        public bool IsSafe(string typeFullName, string memberName) =>
            _safe.Contains((typeFullName, memberName));

        public bool IsInspected(string typeFullName) =>
            _inspected.Contains(typeFullName);
    }
}
