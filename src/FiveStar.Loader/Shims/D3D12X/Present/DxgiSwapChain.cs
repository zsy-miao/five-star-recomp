// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// A flip-model swap chain on a window, through raw IDXGIFactory2 / IDXGISwapChain3 slots (dxgi1_2.h, dxgi1_4.h).
/// The presenter keeps the buffers at the window's client size (<see cref="Resize"/>); STRETCH covers the frames between a
/// window resize and the next present.
/// </summary>
internal sealed unsafe class DxgiSwapChain : IDisposable
{
    public const uint BufferCount = 3;

    /// <summary>IDXGIFactory::MakeWindowAssociation, IDXGIFactory2::CreateSwapChainForHwnd.</summary>
    private const int MakeWindowAssociationSlot = 8;
    private const int CreateSwapChainForHwndSlot = 15;

    /// <summary>IDXGISwapChain Present, GetBuffer, ResizeBuffers; IDXGISwapChain2/3 latency, index, color space.</summary>
    private const int PresentSlot = 8;
    private const int GetBufferSlot = 9;
    private const int ResizeBuffersSlot = 13;
    private const int SetMaximumFrameLatencySlot = 31;
    private const int GetFrameLatencyWaitableObjectSlot = 33;
    private const int GetCurrentBackBufferIndexSlot = 36;
    private const int CheckColorSpaceSupportSlot = 37;
    private const int SetColorSpace1Slot = 38;

    /// <summary>DXGI_USAGE_RENDER_TARGET_OUTPUT, DXGI_SCALING_STRETCH, DXGI_SWAP_EFFECT_FLIP_DISCARD.</summary>
    private const uint RenderTargetOutput = 0x20;
    private const uint ScalingStretch = 0;
    private const uint FlipDiscard = 4;

    /// <summary>DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT.</summary>
    private const uint WaitableFlag = 0x40;

    /// <summary>DXGI_MWA_NO_ALT_ENTER: the game owns its window's messages.</summary>
    private const uint NoAltEnter = 2;

    /// <summary>DXGI_SWAP_CHAIN_COLOR_SPACE_SUPPORT_FLAG_PRESENT.</summary>
    private const uint ColorSpacePresentSupport = 1;

    /// <summary>Frames the CPU may queue ahead of the display.</summary>
    private const uint MaximumFrameLatency = 2;

    /// <summary>How long a present waits for the latency object before going on.</summary>
    private const uint LatencyTimeoutMilliseconds = 1000;

    private static readonly Guid Factory2Id = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
    private static readonly Guid SwapChain3Id = new("94d99bdb-f1f8-4ab0-b236-7da0170edab1");
    private static readonly Guid ResourceId = new("696442be-a72e-4059-bc79-5b5c98040fad");

    private readonly nint[] _buffers = new nint[BufferCount];
    private readonly nint _swapChain;
    private readonly nint _waitable;

    private DxgiSwapChain(nint swapChain, nint waitable, uint width, uint height, int format)
    {
        _swapChain = swapChain;
        _waitable = waitable;
        Width = width;
        Height = height;
        Format = format;
    }

    public uint Width { get; private set; }

    public uint Height { get; private set; }

    public int Format { get; private set; }

    public uint ColorSpace { get; private set; }

    /// <summary>Whether every back buffer is held: false after a resize that failed and left none to take again.</summary>
    public bool Usable => Array.TrueForAll(_buffers, buffer => buffer != 0);

    /// <summary>The back buffers (desktop ID3D12Resource), valid until the next <see cref="Resize"/>.</summary>
    public ReadOnlySpan<nint> Buffers => _buffers;

    /// <summary>A swap chain on <paramref name="window"/> presenting from <paramref name="queue"/> (a desktop direct queue).</summary>
    public static int Create(nint queue, nint window, uint width, uint height, int format, out DxgiSwapChain? swapChain)
    {
        swapChain = null;
        Guid factoryId = Factory2Id;
        nint factory = 0;
        int hr = Dxgi.CreateDXGIFactory1(&factoryId, &factory);

        if (HResult.Failed(hr))
        {
            return hr;
        }

        var description = Description(width, height, format);
        nint chain1 = 0;
        var create = (delegate* unmanaged<nint, nint, nint, SwapChainDescription*, void*, nint, nint*, int>)ComObject.Slot(factory, CreateSwapChainForHwndSlot);
        hr = create(factory, queue, window, &description, null, 0, &chain1);

        if (!HResult.Failed(hr))
        {
            _ = ((delegate* unmanaged<nint, nint, uint, int>)ComObject.Slot(factory, MakeWindowAssociationSlot))(factory, window, NoAltEnter);
        }

        ComObject.Release(factory);

        if (HResult.Failed(hr))
        {
            return hr;
        }

        return Finish(chain1, width, height, format, out swapChain);
    }

    /// <summary>The index of the buffer the next present shows.</summary>
    [HotPath]
    public uint CurrentIndex()
    {
        return ((delegate* unmanaged<nint, uint>)ComObject.Slot(_swapChain, GetCurrentBackBufferIndexSlot))(_swapChain);
    }

    /// <summary>Blocks until the swap chain takes another frame (at most <see cref="MaximumFrameLatency"/> queued).</summary>
    [HotPath]
    public void WaitForFrame()
    {
        using var wait = BlockingWaits.Enter("swap chain frame latency");
        _ = PresentNative.WaitForSingleObject(_waitable, LatencyTimeoutMilliseconds);
    }

    [HotPath]
    public int Present(uint syncInterval)
    {
        return ((delegate* unmanaged<nint, uint, uint, int>)ComObject.Slot(_swapChain, PresentSlot))(_swapChain, syncInterval, 0);
    }

    /// <summary>New buffer size or format; the GPU must be done with the old buffers.</summary>
    public int Resize(uint width, uint height, int format)
    {
        ReleaseBuffers();
        var resize = (delegate* unmanaged<nint, uint, uint, uint, int, uint, int>)ComObject.Slot(_swapChain, ResizeBuffersSlot);
        int hr = resize(_swapChain, BufferCount, width, height, format, WaitableFlag);

        if (HResult.Failed(hr))
        {
            // The swap chain keeps its old buffers: ours again, so that the next frame has targets (Usable says whether).
            _ = GetBuffers();

            return hr;
        }

        Width = width;
        Height = height;
        Format = format;

        return GetBuffers();
    }

    /// <summary>Sets the color space when the display path supports it; false when it does not.</summary>
    public bool SetColorSpace(uint colorSpace)
    {
        uint support = 0;
        var check = (delegate* unmanaged<nint, uint, uint*, int>)ComObject.Slot(_swapChain, CheckColorSpaceSupportSlot);

        if (HResult.Failed(check(_swapChain, colorSpace, &support)) || (support & ColorSpacePresentSupport) == 0)
        {
            return false;
        }

        var set = (delegate* unmanaged<nint, uint, int>)ComObject.Slot(_swapChain, SetColorSpace1Slot);

        if (HResult.Failed(set(_swapChain, colorSpace)))
        {
            return false;
        }

        ColorSpace = colorSpace;

        return true;
    }

    public void Dispose()
    {
        ReleaseBuffers();
        _ = PresentNative.CloseHandle(_waitable);
        ComObject.Release(_swapChain);
    }

    private static SwapChainDescription Description(uint width, uint height, int format)
    {
        return new SwapChainDescription
        {
            Width = width,
            Height = height,
            Format = format,
            SampleCount = 1,
            BufferUsage = RenderTargetOutput,
            BufferCount = BufferCount,
            Scaling = ScalingStretch,
            SwapEffect = FlipDiscard,
            Flags = WaitableFlag,
        };
    }

    /// <summary>IDXGISwapChain3 from the new IDXGISwapChain1, its latency object and buffers.</summary>
    private static int Finish(nint chain1, uint width, uint height, int format, out DxgiSwapChain? swapChain)
    {
        swapChain = null;
        Guid chain3Id = SwapChain3Id;
        nint chain3 = 0;
        int hr = ((delegate* unmanaged<nint, Guid*, nint*, int>)ComObject.Slot(chain1, ComObject.QueryInterfaceSlot))(chain1, &chain3Id, &chain3);
        ComObject.Release(chain1);

        if (HResult.Failed(hr))
        {
            return hr;
        }

        _ = ((delegate* unmanaged<nint, uint, int>)ComObject.Slot(chain3, SetMaximumFrameLatencySlot))(chain3, MaximumFrameLatency);
        nint waitable = ((delegate* unmanaged<nint, nint>)ComObject.Slot(chain3, GetFrameLatencyWaitableObjectSlot))(chain3);
        var created = new DxgiSwapChain(chain3, waitable, width, height, format);
        hr = created.GetBuffers();

        if (HResult.Failed(hr))
        {
            created.Dispose();

            return hr;
        }

        swapChain = created;

        return HResult.Ok;
    }

    private int GetBuffers()
    {
        Guid resourceId = ResourceId;
        var get = (delegate* unmanaged<nint, uint, Guid*, nint*, int>)ComObject.Slot(_swapChain, GetBufferSlot);

        for (uint i = 0; i < BufferCount; i++)
        {
            nint buffer = 0;
            int hr = get(_swapChain, i, &resourceId, &buffer);

            if (HResult.Failed(hr))
            {
                ReleaseBuffers();

                return hr;
            }

            _buffers[i] = buffer;
        }

        return HResult.Ok;
    }

    private void ReleaseBuffers()
    {
        for (int i = 0; i < _buffers.Length; i++)
        {
            ComObject.Release(_buffers[i]);
            _buffers[i] = 0;
        }
    }
}
