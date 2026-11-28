// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Graphics.Pipelines;
using Vortice.Direct3D12;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// Runs a captured dispatch (<see cref="DispatchCapture"/>) on a device with a program's DXIL: the resources are made
/// and filled with what they held before the dispatch, the descriptors written at their captured heap indices, then
/// each run puts the inputs back, binds the bindless root as the loader does and dispatches between two timestamp
/// queries. The first run's writes are read back. After VIctory's frame replay (a GPL-3.0 port of the same game):
/// the inputs restored before each loop, the time taken from GPU timestamps and the median of several runs.
/// </summary>
public sealed class DispatchReplay
{
    private const int QueriesPerRun = 2;
    private const double MillisecondsPerSecond = 1000;

    private readonly ID3D12Device _device;

    public DispatchReplay(ID3D12Device device)
    {
        _device = device;
    }

    /// <summary>
    /// The captured ranges of game memory in imported system memory, as the loader gives them to the GPU, instead of
    /// video memory: the two times tell what a program pays for where game memory lives.
    /// </summary>
    public bool ImportGameMemory { get; init; }

    /// <summary>Runs the dispatch <paramref name="runs"/> times with <paramref name="dxil"/>; the first run's outputs and every run's time.</summary>
    public ReplayOutcome Run(CaptureFolder folder, DispatchCapture capture, byte[] dxil, int runs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);

        using var queue = new ReplayQueue(_device);
        using var bindings = new ReplayBindings(_device, folder, capture, ImportGameMemory);
        using var root = _device.CreateRootSignature(BindlessRootSignature.Description(capture.UserConstants));
        using var pipeline = _device.CreateComputePipelineState<ID3D12PipelineState>(new ComputePipelineStateDescription { RootSignature = root, ComputeShader = dxil });
        using var queries = _device.CreateQueryHeap<ID3D12QueryHeap>(new QueryHeapDescription(QueryHeapType.Timestamp, (uint)(runs * QueriesPerRun)));
        using var stamps = _device.CreateCommittedResource(new HeapProperties(HeapType.Readback), HeapFlags.None, ResourceDescription.Buffer((ulong)(runs * QueriesPerRun * sizeof(ulong))), ResourceStates.CopyDest);

        for (var run = 0; run < runs; run++)
        {
            Record(queue.List, bindings, root, pipeline, capture, queries, run);

            if (run == runs - 1)
            {
                queue.List.ResolveQueryData(queries, QueryType.Timestamp, 0, (uint)(runs * QueriesPerRun), stamps, 0);
            }

            queue.Execute();
        }

        var outputs = bindings.Resources.Values.Where(resource => resource.HasOutput).ToDictionary(resource => resource.Captured.Id, resource => resource.Output(), StringComparer.Ordinal);

        return new ReplayOutcome(outputs, Milliseconds(stamps, runs, queue.TimestampFrequency)) { Skipped = [.. bindings.Skipped] };
    }

    /// <summary>One run: inputs back, bindings, the dispatches between two timestamps; the first run's outputs read back.</summary>
    private static void Record(ID3D12GraphicsCommandList list, ReplayBindings bindings, ID3D12RootSignature root, ID3D12PipelineState pipeline, DispatchCapture capture, ID3D12QueryHeap queries, int run)
    {
        foreach (var resource in bindings.Resources.Values)
        {
            resource.Restore(list);
        }

        bindings.Bind(list, root);
        list.SetPipelineState(pipeline);
        list.EndQuery(queries, QueryType.Timestamp, (uint)(run * QueriesPerRun));

        foreach (var dispatch in capture.Dispatches)
        {
            list.Dispatch(dispatch.X, dispatch.Y, dispatch.Z);
            list.ResourceBarrier(ResourceBarrier.BarrierUnorderedAccessView(null!));
        }

        list.EndQuery(queries, QueryType.Timestamp, (uint)((run * QueriesPerRun) + 1));

        if (run != 0)
        {
            return;
        }

        foreach (var resource in bindings.Resources.Values)
        {
            resource.Read(list);
        }
    }

    private static unsafe List<double> Milliseconds(ID3D12Resource stamps, int runs, ulong frequency)
    {
        void* data;
        stamps.Map(0, null, &data).CheckError();
        var ticks = new ReadOnlySpan<ulong>(data, runs * QueriesPerRun);
        var times = new List<double>(runs);

        for (var run = 0; run < runs; run++)
        {
            var elapsed = ticks[(run * QueriesPerRun) + 1] - ticks[run * QueriesPerRun];
            times.Add(elapsed * MillisecondsPerSecond / frequency);
        }

        stamps.Unmap(0);

        return times;
    }
}
