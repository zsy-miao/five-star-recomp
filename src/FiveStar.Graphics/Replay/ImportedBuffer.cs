// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Vortice.Direct3D12;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// A buffer over memory of this process the device imported (VirtualAlloc, then ID3D12Device3::OpenExistingHeapFromAddress),
/// the way the loader gives the GPU game memory: programs read and write it in system memory across PCIe, and the
/// GPU does not cache it (docs/physical-memory.md, What the GPU pays for system memory). For
/// <c>fivestar shader replay --game-memory system</c>.
/// </summary>
internal sealed partial class ImportedBuffer : IDisposable
{
    /// <summary>A heap opened from an address covers whole 64 KiB allocation units.</summary>
    private const ulong Granularity = 1UL << 16;

    /// <summary>MEM_COMMIT | MEM_RESERVE, MEM_RELEASE, PAGE_READWRITE (winnt.h).</summary>
    private const uint CommitReserve = 0x3000;
    private const uint Release = 0x8000;
    private const uint ReadWrite = 0x04;

    private readonly nint _memory;
    private readonly ID3D12Heap _heap;

    public ImportedBuffer(ID3D12Device device, ulong width)
    {
        ulong bytes = (width + Granularity - 1) & ~(Granularity - 1);
        _memory = VirtualAlloc(0, (nuint)bytes, CommitReserve, ReadWrite);

        if (_memory == 0)
        {
            throw new InvalidOperationException($"VirtualAlloc of {bytes} bytes failed: error {Marshal.GetLastPInvokeError()}");
        }

        using var device3 = device.QueryInterface<ID3D12Device3>();
        _heap = device3.OpenExistingHeapFromAddress<ID3D12Heap>(_memory);
        Resource = device.CreatePlacedResource<ID3D12Resource>(_heap, 0, ResourceDescription.Buffer(width, ResourceFlags.AllowUnorderedAccess | ResourceFlags.AllowCrossAdapter), ResourceStates.Common);
    }

    public ID3D12Resource Resource { get; }

    public void Dispose()
    {
        Resource.Dispose();
        _heap.Dispose();
        VirtualFree(_memory, 0, Release);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAlloc(nint address, nuint size, uint type, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFree(nint address, nuint size, uint type);
}
