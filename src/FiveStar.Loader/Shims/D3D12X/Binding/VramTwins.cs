// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using Vortice.Direct3D12;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// <c>--vram-twin-ceiling &lt;presents&gt;</c>, a measurement and not a way to play: buffers over game memory get a
/// twin in video memory that programs read (their SRVs), filled by a GPU copy of the whole buffer when made and again
/// every so many presents, and otherwise never synced. Stores still go to the game's memory (the UAVs), so what the
/// CPU or the GPU wrote since the last copy is missing from what programs read: the picture is stale or wrong. What
/// it shows is the GPU time a frame costs when its reads of game memory do not cross PCIe (docs/physical-memory.md).
/// </summary>
internal static class VramTwins
{
    /// <summary>Twins stop being made past this much video memory: an RTX 4090 has 9 GiB left in the world.</summary>
    private const ulong Budget = 8000UL << 20;

    /// <summary>A buffer over an allocation larger than this gets no twin (the game's 2.2 and 1.1 GiB heaps: they would not fit).</summary>
    private const ulong LargestRange = 512UL << 20;

    /// <summary>A buffer under this gets no twin of its own: the game places thousands of small ones.</summary>
    public const ulong SmallestRange = 1UL << 20;

    private const int MebibyteShift = 20;

    private static readonly object Gate = new();
    private static readonly Dictionary<nint, Twin> Twins = [];
    private static readonly List<nint> Fresh = [];
    private static ID3D12Device? device;
    private static ID3D12CommandQueue? queue;
    private static ID3D12CommandAllocator? allocator;
    private static ID3D12GraphicsCommandList? list;
    private static ID3D12Fence? fence;
    private static ulong submitted;
    private static ulong bytes;
    private static long lastPresent = -1;
    private static long lastRefresh;
    private static long refreshes;

    /// <summary>Presents between two copies of every twin; 0 turns the twins off.</summary>
    public static int RefreshPresents { get; set; }

    /// <summary>Only the section windows of the streaming pool get twins: no buffer of its own, and not the GPU pool's window.</summary>
    public static bool PoolOnly { get; set; }

    /// <summary>The size of the GPU pool's one section window (11,360 and 2,960 pages round up to 192 MiB), which <see cref="PoolOnly"/> leaves out.</summary>
    public const ulong GpuPoolWindow = 192UL << 20;

    /// <summary>Where the totals are logged.</summary>
    public static LoaderLog? Log { get; set; }

    /// <summary>Whether twins are made.</summary>
    public static bool Enabled => RefreshPresents > 0;

    /// <summary>
    /// The twin of <paramref name="source"/>, a buffer of <paramref name="size"/> bytes over game memory, made on the
    /// first call and copied at the next present; 0 when twins are off, the buffer is too large or the budget is spent.
    /// </summary>
    public static nint Of(nint nativeDevice, nint source, ulong size)
    {
        if (!Enabled || source == 0 || size == 0 || size > Budget)
        {
            return 0;
        }

        lock (Gate)
        {
            if (Twins.TryGetValue(source, out var known))
            {
                return known.Copy.NativePointer;
            }

            if (bytes + size > Budget || !Open(nativeDevice))
            {
                return 0;
            }

            ID3D12Resource copy;

            try
            {
                copy = device!.CreateCommittedResource(new Vortice.Direct3D12.HeapProperties(HeapType.Default), HeapFlags.None, Vortice.Direct3D12.ResourceDescription.Buffer(size), ResourceStates.Common, null);
            }
            catch (SharpGen.Runtime.SharpGenException exception)
            {
                Log?.Warn($"[vram twins] no video-memory buffer of {size >> MebibyteShift} MiB: {exception.Message}");

                return 0;
            }

            Marshal.AddRef(source);
            var twin = new Twin(new ID3D12Resource(source), copy, size);
            Twins[source] = twin;
            bytes += size;
            Fill(twin);
            Log?.Info($"  [vram twins] buffer 0x{source:x} ({size >> MebibyteShift} MiB) has a twin in video memory; {Twins.Count} twins, {bytes >> MebibyteShift} MiB");

            return copy.NativePointer;
        }
    }

    /// <summary>
    /// Copies a new twin and waits for the copy, so no program reads it empty: programs fed zeros wrote what the CPU
    /// then followed (w325 ended in the loader 4 s after its first frame). Under the gate.
    /// </summary>
    private static void Fill(Twin twin)
    {
        const int Patience = 10_000;
        var waited = System.Diagnostics.Stopwatch.StartNew();

        while (fence!.CompletedValue < submitted && waited.ElapsedMilliseconds < Patience)
        {
            Thread.Sleep(1);
        }

        allocator!.Reset();
        list!.Reset(allocator, null);
        list.CopyBufferRegion(twin.Copy, 0, twin.Source, 0, twin.Size);
        list.Close();
        queue!.ExecuteCommandList(list);
        queue.Signal(fence, ++submitted);

        while (fence.CompletedValue < submitted && waited.ElapsedMilliseconds < Patience)
        {
            Thread.Sleep(1);
        }
    }

    /// <summary>The twin programs read instead of <paramref name="source"/> when it has one of the same layout, else <paramref name="source"/>.</summary>
    public static nint Substitute(nint source)
    {
        if (!Enabled || source == 0)
        {
            return source;
        }

        lock (Gate)
        {
            if (Twins.TryGetValue(source, out var twin) && twin.Whole)
            {
                return twin.Copy.NativePointer;
            }

            return source;
        }
    }

    /// <summary>A twin whose SRVs are made by <see cref="Substitute"/>: the buffer's own views, at the same offsets.</summary>
    public static void Whole(nint nativeDevice, nint source, ulong size)
    {
        if (PoolOnly || size > LargestRange || Of(nativeDevice, source, size) == 0)
        {
            return;
        }

        lock (Gate)
        {
            Twins[source] = Twins[source] with { Whole = true };
        }
    }

    /// <summary>The buffer is about to be released: its twin is no longer copied (it stays, views may still name it).</summary>
    public static void Forget(nint source)
    {
        if (!Enabled)
        {
            return;
        }

        lock (Gate)
        {
            if (Twins.Remove(source, out var twin))
            {
                Fresh.Remove(source);
                twin.Source.Dispose();
            }
        }
    }

    /// <summary>
    /// Once a present: copies every twin when one is new or <see cref="RefreshPresents"/> presents have passed, unless
    /// the last copy is still running.
    /// </summary>
    public static void Tick(long present)
    {
        if (!Enabled || present == Volatile.Read(ref lastPresent))
        {
            return;
        }

        lock (Gate)
        {
            lastPresent = present;
            bool all = present - lastRefresh >= RefreshPresents;

            if (queue is null || Twins.Count == 0 || (!all && Fresh.Count == 0) || fence!.CompletedValue < submitted)
            {
                return;
            }

            allocator!.Reset();
            list!.Reset(allocator, null);

            // Every twin when the interval has passed, else only the ones made since the last copy.
            foreach (var (source, twin) in Twins)
            {
                if (all || Fresh.Contains(source))
                {
                    list.CopyBufferRegion(twin.Copy, 0, twin.Source, 0, twin.Size);
                }
            }

            list.Close();
            queue.ExecuteCommandList(list);
            queue.Signal(fence, ++submitted);
            Fresh.Clear();

            if (!all)
            {
                return;
            }

            lastRefresh = present;
            refreshes++;

            if ((refreshes & (refreshes - 1)) == 0)
            {
                Log?.Info($"  [vram twins] copy {refreshes} at present {present}: {Twins.Count} twins, {bytes >> MebibyteShift} MiB of video memory, every {RefreshPresents} presents");
            }
        }
    }

    /// <summary>The queue the copies run on, made at the first twin; under the gate.</summary>
    private static bool Open(nint nativeDevice)
    {
        if (queue is not null)
        {
            return true;
        }

        try
        {
            Marshal.AddRef(nativeDevice);
            device = new ID3D12Device(nativeDevice);

            queue = device.CreateCommandQueue(new Vortice.Direct3D12.CommandQueueDescription(CommandListType.Direct));
            allocator = device.CreateCommandAllocator(CommandListType.Direct);
            list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator, null);
            list.Close();
            fence = device.CreateFence(0);

            return true;
        }
        catch (SharpGen.Runtime.SharpGenException exception)
        {
            Log?.Warn($"[vram twins] no queue for the copies: {exception.Message}");
            queue = null;

            return false;
        }
    }

    /// <summary>A buffer over game memory and its copy in video memory; <see cref="Whole"/> when views of the buffer move to the copy as they are.</summary>
    private readonly record struct Twin(ID3D12Resource Source, ID3D12Resource Copy, ulong Size, bool Whole = false);
}
