// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FiveStar.Graphics.Pipelines;
using Vortice.Direct3D12;

namespace FiveStar.Graphics.Replay;

/// <summary>
/// What a captured dispatch binds on the replay device: its resources (<see cref="ReplayResource"/>), the bindless view
/// heap with the captured descriptors at their indices, a sampler heap and the user data, set on a list as the loader
/// sets them for a translated program (<see cref="BindlessRootSignature"/>).
/// </summary>
internal sealed unsafe class ReplayBindings : IDisposable
{
    /// <summary>The bytes of the user-data CBV: 128 dwords.</summary>
    private const int UserDataBytes = UserDataDwords * sizeof(uint);
    private const int UserDataDwords = 128;

    private readonly ID3D12Device _device;
    private readonly ID3D12DescriptorHeap _views;
    private readonly ID3D12DescriptorHeap _samplers;
    private readonly ID3D12Resource? _userData;
    private readonly DispatchCapture _capture;
    private readonly List<string> _skipped = [];

    public ReplayBindings(ID3D12Device device, CaptureFolder folder, DispatchCapture capture, bool importGameMemory)
    {
        _device = device;
        _capture = capture;

        var resources = new Dictionary<string, ReplayResource>(StringComparer.Ordinal);
        Resources = resources;

        foreach (var resource in capture.Resources)
        {
            var step = $"creating {resource.Id} ({resource.Label}: dimension {resource.Dimension}, {resource.Width}x{resource.Height}x{resource.DepthOrArraySize}, {resource.MipLevels} mips, format {resource.Format}, flags 0x{resource.Flags:x})";

            try
            {
                resources[resource.Id] = new ReplayResource(device, folder, resource, importGameMemory);
            }
            catch (SharpGen.Runtime.SharpGenException exception)
            {
                throw new InvalidOperationException($"{step} failed: {exception.Message}", exception);
            }

            CheckDevice(step);
        }

        _views = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView, Math.Max(capture.HeapSize, 1), DescriptorHeapFlags.ShaderVisible));
        _samplers = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.Sampler, Math.Max(capture.SamplerHeapSize, 1), DescriptorHeapFlags.ShaderVisible));
        CheckDevice("creating the descriptor heaps");
        WriteViews();
        WriteSamplers();
        CheckDevice("writing the samplers");

        if (!capture.UserConstants)
        {
            _userData = UserDataBuffer(device, capture.UserData);
        }
    }

    public IReadOnlyDictionary<string, ReplayResource> Resources { get; }

    /// <summary>The captured views that did not fit their resource (<see cref="ViewFit"/>), written as null views.</summary>
    public IReadOnlyList<string> Skipped => _skipped;

    /// <summary>Sets the heaps, the root signature and its two parameters (user data, fallback table) on the list.</summary>
    public void Bind(ID3D12GraphicsCommandList list, ID3D12RootSignature root)
    {
        list.SetDescriptorHeaps([_views, _samplers]);
        list.SetComputeRootSignature(root);

        if (_userData is not null)
        {
            list.SetComputeRootConstantBufferView(BindlessRootSignature.UserDataParameter, _userData.GPUVirtualAddress);
        }
        else
        {
            var constants = _capture.UserData.Take(BindlessRootSignature.UserConstants).ToArray();
            list.SetComputeRoot32BitConstants(BindlessRootSignature.UserDataParameter, constants, 0);
        }

        var increment = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        list.SetComputeRootDescriptorTable(BindlessRootSignature.FallbackTableParameter, _views.GetGPUDescriptorHandleForHeapStart() + (int)(_capture.FallbackTable * increment));
    }

    public void Dispose()
    {
        foreach (var resource in Resources.Values)
        {
            resource.Dispose();
        }

        _userData?.Dispose();
        _samplers.Dispose();
        _views.Dispose();
    }

    private void WriteViews()
    {
        var increment = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView);
        var start = _views.GetCPUDescriptorHandleForHeapStart();

        foreach (var view in _capture.Views)
        {
            if (view.Index >= _capture.HeapSize)
            {
                throw new InvalidDataException($"view at heap index {view.Index} past the heap of {_capture.HeapSize}");
            }

            var bytes = Convert.FromHexString(view.Description);
            var handle = start + (int)(view.Index * increment);
            ID3D12Resource? resource = null;

            if (view.Resource is not null)
            {
                var captured = Resources[view.Resource].Captured;
                var problem = ViewFit.Problem(captured, view.Kind, bytes);
                resource = Resources[view.Resource].Resource;

                if (problem is not null)
                {
                    _skipped.Add($"{view.Kind} at heap index {view.Index} of {view.Resource} ({captured.Label}): {problem}; a null view instead");
                    bytes = ViewFit.Null(captured, view.Kind, bytes);
                    resource = null;
                }
            }

            if (view.Kind == CapturedViewKind.UnorderedAccess)
            {
                _device.CreateUnorderedAccessView(resource, null, Description<UnorderedAccessViewDescription>(bytes), handle);
            }
            else
            {
                _device.CreateShaderResourceView(resource, Description<ShaderResourceViewDescription>(bytes), handle);
            }

            CheckDevice($"writing the {view.Kind} at heap index {view.Index} of {view.Resource ?? "null"} (desc {view.Description})");
        }
    }

    /// <summary>Throws naming <paramref name="step"/> when the device is removed after it, so a removal names the call that caused it.</summary>
    private void CheckDevice(string step)
    {
        var reason = _device.DeviceRemovedReason;

        if (reason.Failure)
        {
            throw new InvalidOperationException($"device removed after {step}: {reason}");
        }
    }

    /// <summary>Every sampler slot a linear clamp sampler: the capture keeps no sampler descriptors.</summary>
    private void WriteSamplers()
    {
        var increment = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Sampler);
        var start = _samplers.GetCPUDescriptorHandleForHeapStart();
        var description = new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp);

        for (uint slot = 0; slot < _capture.SamplerHeapSize; slot++)
        {
            _device.CreateSampler(ref description, start + (int)(slot * increment));
        }
    }

    /// <summary>A view desc from its d3d12.h bytes (the Vortice struct has the native layout); null for none: the whole resource.</summary>
    private static T? Description<T>(byte[] bytes)
        where T : unmanaged
    {
        if (bytes.Length == 0)
        {
            return null;
        }

        if (bytes.Length !=CapturedView.DescriptionBytes || Unsafe.SizeOf<T>() != CapturedView.DescriptionBytes)
        {
            throw new InvalidDataException($"a view desc of {bytes.Length} bytes, {typeof(T).Name} takes {Unsafe.SizeOf<T>()}");
        }

        return MemoryMarshal.Read<T>(bytes);
    }

    private static ID3D12Resource UserDataBuffer(ID3D12Device device, IReadOnlyList<uint> words)
    {
        var buffer = device.CreateCommittedResource(new HeapProperties(HeapType.Upload), HeapFlags.None, ResourceDescription.Buffer(UserDataBytes), ResourceStates.GenericRead);
        void* data;
        buffer.Map(0, null, &data).CheckError();
        var span = new Span<uint>(data, UserDataDwords);
        span.Clear();

        for (var index = 0; index < Math.Min(words.Count, UserDataDwords); index++)
        {
            span[index] = words[index];
        }

        buffer.Unmap(0);

        return buffer;
    }
}
