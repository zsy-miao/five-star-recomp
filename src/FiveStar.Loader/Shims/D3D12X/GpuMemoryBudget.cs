// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Loader.Shims.D3D12X;

/// <summary>
/// The device's memory budgets (IDXGIAdapter3::QueryVideoMemoryInfo). Heaps over existing memory
/// (OpenExistingHeapFromAddress: the section heaps and the game's own allocations), upload buffers and readbacks all
/// live in the non-local (system-memory) segment group, whose budget the OS sets to about half the machine's RAM,
/// whatever the card's own memory: on a 16 GB machine about 8 GB, which the game's 6.1 GiB section alone nearly fills.
/// </summary>
internal sealed class GpuMemoryBudget : IDisposable
{
    private const double BytesPerGibibyte = 1UL << 30;
    private const int LuidHighShift = 32;

    private readonly IDXGIAdapter3 _adapter;

    private GpuMemoryBudget(IDXGIAdapter3 adapter)
    {
        _adapter = adapter;
    }

    /// <summary>The budget of <paramref name="device"/>'s adapter; null when DXGI does not report one.</summary>
    public static GpuMemoryBudget? Open(nint device, LoaderLog log)
    {
        try
        {
            Marshal.AddRef(device);
            using var desktop = new ID3D12Device(device);
            using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(false);
            var budget = new GpuMemoryBudget(factory.EnumAdapterByLuid<IDXGIAdapter3>(AdapterLuid(desktop.AdapterLuid)));

            uint vendor = budget._adapter.Description.VendorId;
            long driver = 0;

            if (budget._adapter.CheckInterfaceSupport<IDXGIDevice>(out long version))
            {
                driver = version;
            }

            Pipelines.CompileBudget.UseAdapter(vendor, driver);
            log.Info($"  [d3d12_xs] GPU memory: {budget.Describe()}; driver compile estimates for vendor 0x{vendor:x4}, driver 0x{driver:x}");

            return budget;
        }
        catch (SharpGenException exception)
        {
            log.Warn($"[d3d12_xs] no GPU memory budget from DXGI: {exception.Message}");

            return null;
        }
    }

    /// <summary>
    /// The system-memory segment group's budget and the process's use of it; false when DXGI fails or reports no such
    /// segment (a budget of 0: WARP and unified-memory adapters keep everything in the local group).
    /// </summary>
    public bool TryReadSystem(out ulong budget, out ulong usage)
    {
        return TryRead(MemorySegmentGroup.NonLocal, out budget, out usage) && budget != 0;
    }

    /// <summary>The local (video-memory) segment group's budget and the process's use of it; false when DXGI fails.</summary>
    public bool TryReadLocal(out ulong budget, out ulong usage)
    {
        return TryRead(MemorySegmentGroup.Local, out budget, out usage) && budget != 0;
    }

    /// <summary>Both segment groups, for the log.</summary>
    public string Describe()
    {
        TryRead(MemorySegmentGroup.Local, out ulong localBudget, out ulong localUsage);
        TryRead(MemorySegmentGroup.NonLocal, out ulong systemBudget, out ulong systemUsage);

        return $"local {Gib(localUsage)} of {Gib(localBudget)} GiB, system {Gib(systemUsage)} of {Gib(systemBudget)} GiB";
    }

    /// <summary>
    /// Each segment group's usage, budget, memory available for reservation and reservation, for the sample line: a
    /// process over its local budget gets its allocations demoted or evicted by the video memory manager.
    /// </summary>
    public string Sample()
    {
        return $"GPU memory: local {Group(MemorySegmentGroup.Local)}; system {Group(MemorySegmentGroup.NonLocal)}";
    }

    private string Group(MemorySegmentGroup group)
    {
        try
        {
            var info = _adapter.QueryVideoMemoryInfo(0, group);

            return $"{Gib(info.CurrentUsage)} of {Gib(info.Budget)} GiB budget, {Gib(info.AvailableForReservation)} available for reservation, {Gib(info.CurrentReservation)} reserved";
        }
        catch (SharpGenException)
        {
            return "unknown";
        }
    }

    public void Dispose()
    {
        _adapter.Dispose();
    }

    private bool TryRead(MemorySegmentGroup group, out ulong budget, out ulong usage)
    {
        budget = 0;
        usage = 0;

        try
        {
            var info = _adapter.QueryVideoMemoryInfo(0, group);
            budget = info.Budget;
            usage = info.CurrentUsage;

            return true;
        }
        catch (SharpGenException)
        {
            return false;
        }
    }

    /// <summary>LUID: LowPart, the low dword, and HighPart (winnt.h).</summary>
    internal static Luid AdapterLuid(long value)
    {
        return new Luid(unchecked((uint)value), (int)(value >> LuidHighShift));
    }

    private static string Gib(ulong bytes)
    {
        return (bytes / BytesPerGibibyte).ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
    }
}
