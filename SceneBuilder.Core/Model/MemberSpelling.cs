using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    // The public C# spelling of one serialized member of one component TYPE
    // (UnityEngine.UI.Slider + "m_OnValueChanged" -> "onValueChanged"). A per-TYPE fact, carried on
    // the snapshot beside ComponentDefaults for the same reason: Core must render source naming the
    // member and cannot reach for a Unity type to derive it.
    public sealed record MemberSpelling
    {
        [JsonPropertyOrder(0)]
        public TypeRef Type { get; init; } = new TypeRef("");

        [JsonPropertyOrder(1)]
        public string SerializedPath { get; init; } = "";

        [JsonPropertyOrder(2)]
        public string PublicName { get; init; } = "";
    }

    // Lookup over SceneSnapshot.MemberSpellings, mirroring ComponentDefaultOmission.Index.Build's
    // shape so the consumer threads it exactly like `defaults`. Keys on
    // (Type.FullName, SerializedPath), compared via the default ValueTuple<string,string> equality
    // (ordinal char comparison, same as string's own default equality); first-wins on a duplicate.
    public sealed class MemberSpellingIndex
    {
        public static readonly MemberSpellingIndex Empty = new MemberSpellingIndex(Array.Empty<MemberSpelling>());

        private readonly Dictionary<(string TypeFullName, string SerializedPath), string> _publicNames;

        private MemberSpellingIndex(IReadOnlyList<MemberSpelling> spellings)
        {
            _publicNames = new Dictionary<(string, string), string>();
            foreach (var spelling in spellings)
            {
                var key = (spelling.Type.FullName, spelling.SerializedPath);
                if (!_publicNames.ContainsKey(key))
                {
                    _publicNames[key] = spelling.PublicName;
                }
            }
        }

        public static MemberSpellingIndex Build(MemberSpelling[]? spellings) =>
            spellings is null || spellings.Length == 0 ? Empty : new MemberSpellingIndex(spellings);

        public bool TryGet(string typeFullName, string serializedPath, out string publicName) =>
            _publicNames.TryGetValue((typeFullName, serializedPath), out publicName!);
    }

    // A (component TYPE, member name) pair the adapter has determined has NO compiling typed-selector
    // spelling (a managed serialized field that is private/[SerializeField]-private with no public
    // property setter). MemberName is the selector identifier the user/LLM would author, which for a
    // field with no public alias is the field's own name.
    public sealed record InaccessibleMember
    {
        [JsonPropertyOrder(0)]
        public TypeRef Type { get; init; } = new TypeRef("");

        [JsonPropertyOrder(1)]
        public string MemberName { get; init; } = "";
    }

    // Lookup over SceneSnapshot.InaccessibleMembers, mirroring MemberSpellingIndex's shape exactly.
    // Keyed on (Type.FullName, MemberName), ordinal equality; first-wins on a duplicate. Absence
    // (Empty, or no entry for a given key) means "keep whatever selector form is authored" — the
    // signal is opt-in, never inferred from silence.
    public sealed class InaccessibleMemberIndex
    {
        public static readonly InaccessibleMemberIndex Empty = new InaccessibleMemberIndex(Array.Empty<InaccessibleMember>());

        private readonly HashSet<(string TypeFullName, string MemberName)> _inaccessible;

        private InaccessibleMemberIndex(IReadOnlyList<InaccessibleMember> members)
        {
            _inaccessible = new HashSet<(string, string)>();
            foreach (var member in members)
            {
                _inaccessible.Add((member.Type.FullName, member.MemberName));
            }
        }

        public static InaccessibleMemberIndex Build(InaccessibleMember[]? members) =>
            members is null || members.Length == 0 ? Empty : new InaccessibleMemberIndex(members);

        public bool IsInaccessible(string typeFullName, string memberName) =>
            _inaccessible.Contains((typeFullName, memberName));
    }
}
