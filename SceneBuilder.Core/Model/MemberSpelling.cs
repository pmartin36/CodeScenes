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
}
