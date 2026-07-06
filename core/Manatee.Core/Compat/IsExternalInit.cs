#if NETSTANDARD2_1
// `init`-only setters (used by every `readonly record struct` in the core) emit
// a modreq on this type. net8.0 ships it in the BCL; netstandard2.1 does not, so
// the compiler needs this internal shim to lower init accessors on that target.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit
    {
    }
}
#endif
