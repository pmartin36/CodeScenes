// netstandard2.0 has no System.Runtime.CompilerServices.IsExternalInit; the compiler needs this
// type to exist to support C# 9 `init`/record accessors. Polyfill (harmless, compile-time only).
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
