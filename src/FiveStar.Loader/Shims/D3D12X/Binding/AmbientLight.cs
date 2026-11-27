// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// What the screen-probe light's stand-in writes (<see cref="RtStandIns"/>): by default a share of the light the scene
/// sends out, else a constant grey. The game keeps the lit image of the frame before for its own screen traces
/// (tiled_lighting~CS_CacheScreenSpaceRadiance); after that pass runs, the mean of a 32x18 grid of its output is taken
/// on the GPU (<see cref="UavFill.Measure"/>), and the fill after CS_ScreenProbes_TemporalResolve writes that mean
/// times the share: as a grey of its luminance by default, with its colour by a tint. So the stand-in's level follows
/// the shot (a sunset yard, a lamp-lit room, a dark street). Taking the level from that image is the idea of
/// VIctory's 7900 XTX fork (its crates/victory-gpu/src/ambient.rs, VICTORY_AMBIENT: there per screen region, by depth
/// and normal, bounded by what the resolve left; GPL-3.0); this is one mean for the frame and none of its code.
/// <c>--rt-ambient scene[:share[:tint]]</c> (the default, share 1, tint 0) or <c>constant[:level]</c> (0.6, as before).
/// </summary>
internal static unsafe class AmbientLight
{
    /// <summary>The pass whose output is measured.</summary>
    private const string CacheName = "CS_CacheScreenSpaceRadiance";

    /// <summary>ID3D12Device::GetDescriptorHandleIncrementSize and the CBV_SRV_UAV heap type (d3d12.h).</summary>
    private const int IncrementSlot = 15;
    private const int ViewHeapType = 0;

    /// <summary>The pass's own table is short (its output is record 2); the shared table it also reads is not searched.</summary>
    private const uint MaxRecords = 8;

    /// <summary>
    /// A sample counts up to this much (the images are pre-exposed: a sunlit wall is 1 to 10): a lamp's filament or
    /// the sun's disc in a few texels does not set the level.
    /// </summary>
    private const float SampleLimit = 16;

    /// <summary>The least the share writes per channel: a frame that measured black is not left without any light.</summary>
    private const float Least = 0.02f;

    /// <summary>DXGI R32G32B32A32_FLOAT, R16G16B16A16_FLOAT and R11G11B10_FLOAT: a light image.</summary>
    private static readonly int[] LightFormats = [2, 10, 26];

    private static readonly ConcurrentDictionary<nint, byte> Caches = new();
    private static long _measurements;
    private static int _logged;

    /// <summary>The share of the measured level the stand-in writes; 0 for the constant (<c>--rt-ambient</c>).</summary>
    public static float Share { get; set; } = 1;

    /// <summary>The constant grey: without a share, and until a level was measured.</summary>
    public static float Constant { get; set; } = RtStandIns.AmbientLevel;

    /// <summary>
    /// How much of the measured colour the share keeps: 0 (the default) writes a grey of the mean's luminance, 1 the
    /// mean itself. The mean of a sunset yard is orange, and with the sun's term leaking through walls on the tiles the
    /// game hands to rays it fed back into itself: boot w360 (tint 1) turned the grass, the concrete and the cell wall
    /// tan where the grey of w357 had left them their colours.
    /// </summary>
    public static float Tint { get; set; }

    /// <summary>The fill of the screen-probe light now.</summary>
    public static FillValue Value
    {
        get
        {
            var constant = FillValue.Shared(Constant);

            if (Share > 0 && UavFill.Measured)
            {
                return constant with { Share = Share, Least = Least, Tint = Tint };
            }

            return constant;
        }
    }

    /// <summary>The sample line: which light the stand-in writes.</summary>
    public static string Summary
    {
        get
        {
            if (Share > 0)
            {
                return FormattableString.Invariant($"screen-probe light {Share} x the cached screen radiance's mean, tint {Tint} ({Interlocked.Read(ref _measurements)} measurements; grey {Constant} before the first)");
            }

            return FormattableString.Invariant($"screen-probe light a constant grey {Constant}");
        }
    }

    /// <summary>A desktop pipeline was built from <paramref name="names"/>: the cache pass's is measured after it runs.</summary>
    public static void Named(nint pipeline, string names)
    {
        if (RtStandIns.Enabled && RtStandIns.ScreenProbeLight && Share > 0 && names.Contains(CacheName, StringComparison.Ordinal))
        {
            Caches[pipeline] = 0;

            return;
        }

        Caches.TryRemove(pipeline, out _);
    }

    /// <summary>After a dispatch that ran: the cache pass's output is measured.</summary>
    [HotPath]
    public static void AfterDispatch(XboxObject* list, nint pipeline)
    {
        if (!Caches.IsEmpty && Caches.ContainsKey(pipeline))
        {
            Measure(list, QueueProgress.PipelineName(pipeline));
        }
    }

    /// <summary>Measures the light image the pass named <paramref name="name"/> just wrote, then sets the list's compute state again.</summary>
    private static void Measure(XboxObject* list, string name)
    {
        var runtime = ShimContext.Runtime.D3D12X;
        var bindings = CommandListBindings.Of(list);
        var window = runtime.Bindings.Window;

        // Without the game's compute root signature set there is nothing to restore after the measurement's own.
        if (bindings->ComputeRoot == 0 || bindings->ComputeLayout == null || window.Data == null || window.ViewHeap == 0)
        {
            return;
        }

        foreach (var entry in TableReaches.OfNames(name))
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords || entry.Records > MaxRecords)
            {
                continue;
            }

            uint pointer = bindings->Compute[entry.Sgpr];
            runtime.DescriptorHeaps.MirrorTable(pointer, entry.Records);

            if (MeasureTable(list->Inner, window, pointer, entry.Records, name))
            {
                Interlocked.Increment(ref _measurements);
                CommandListBindingMethods.RestoreCompute(list);

                return;
            }
        }
    }

    /// <summary>Measures the first 2D light image among the UAVs of the table at window offset <paramref name="pointer"/>; whether there was one.</summary>
    private static bool MeasureTable(nint list, DescriptorWindow window, uint pointer, uint records, string name)
    {
        nint device = ShimContext.Runtime.D3D12X.Objects.DesktopDevice;
        nint cpuStart = DesktopCalls.HeapCpuStart(window.ViewHeap);
        uint increment = ((delegate* unmanaged<nint, int, uint>)ComObject.Slot(device, IncrementSlot))(device, ViewHeapType);

        for (uint record = 0; record < records; record++)
        {
            ulong offset = pointer + ((ulong)record * DescriptorWindow.ViewRecordBytes);

            if (offset + DescriptorWindow.ViewRecordBytes > DescriptorWindow.NullTableBase)
            {
                break;
            }

            uint index = ((uint*)(window.Data + offset))[DescriptorWindow.MirroredDesktopDword];

            if (!UavViews.TryFind(cpuStart + (nint)((ulong)index * increment), out var view) || view.Dimension != UavViews.Texture2D || Array.IndexOf(LightFormats, view.Format) < 0)
            {
                continue;
            }

            // The pass's writes before the measurement, the measured level before the fills that read it.
            RtStandIns.Barrier(list);

            if (!UavFill.Measure(device, list, DesktopCalls.HeapGpuStart(window.ViewHeap), index, view, SampleLimit))
            {
                return false;
            }

            RtStandIns.Barrier(list);
            Log(name, record, view);

            return true;
        }

        return false;
    }

    /// <summary>The first measurement is logged with what it reads.</summary>
    private static void Log(string name, uint record, UavView view)
    {
        if (Interlocked.Exchange(ref _logged, 1) == 0)
        {
            ShimContext.Log?.Warn(FormattableString.Invariant($"[d3d12_xs] ray-tracing stand-in (approximate): the screen-probe light is {Share} x the mean of {name}'s output (record {record}, {view.Width}x{view.Height}, DXGI format {view.Format}; tint {Tint}), measured each frame; --rt-ambient constant writes the grey {Constant} instead"));
        }
    }
}
