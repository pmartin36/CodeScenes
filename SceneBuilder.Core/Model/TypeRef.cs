using System;
using System.Text.Json.Serialization;

namespace SceneBuilder.Core.Model
{
    public sealed record TypeRef(
        string FullName,
        string? AssemblyHint = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? MonoScriptGuid = null)
    {
        public bool Equals(TypeRef? other)
        {
            if (other is null) return false;
            if (MonoScriptGuid is not null || other.MonoScriptGuid is not null)
                return MonoScriptGuid == other.MonoScriptGuid;
            return FullName == other.FullName && AssemblyHint == other.AssemblyHint;
        }

        public override int GetHashCode() =>
            MonoScriptGuid is not null
                ? MonoScriptGuid.GetHashCode()
                : HashCode.Combine(FullName, AssemblyHint);
    }
}
