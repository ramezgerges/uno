using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
namespace Common;

/// <summary>
/// Owns a wgpu object pointer and releases it on Dispose. `using var` declarations
/// dispose in reverse declaration order, which matches the release-in-reverse-order
/// discipline for free. Implicitly converts to the raw pointer, so an Owned value
/// can be passed anywhere a T* is expected.
/// </summary>
public readonly unsafe struct Owned<T> : IDisposable where T : unmanaged
{
    private readonly T* _ptr;
    private readonly Action<nint> _release;

    public Owned(T* ptr, Action<nint> release)
    {
        _ptr = ptr;
        _release = release;
    }

    public T* Ptr => _ptr;

    public static implicit operator T*(Owned<T> owned) => owned._ptr;

    public void Dispose() => _release((nint)_ptr);
}
