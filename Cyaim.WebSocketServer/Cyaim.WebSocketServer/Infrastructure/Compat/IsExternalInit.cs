// Polyfill for the compiler-required type behind `init`-only setters.
// Built into net5.0+; missing on netstandard2.1, so declare it there.
// `init` 访问器所需的编译器类型：net5.0+ 内置，netstandard2.1 缺失，故在此声明。
#if !NET5_0_OR_GREATER
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    [EditorBrowsable(EditorBrowsableState.Never)]
    internal static class IsExternalInit
    {
    }
}
#endif
