// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

// Ported from victory-fork (github.com/fxceless/victory-fork, commit 578d459: crates/victory-gpu/src/resource.rs
// `alias_of`, texture.rs `alias`, draw.rs), itself built on VIctory. Copyright (c) the VIctory and victory-fork
// contributors; GPL-3.0-or-later.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X.Binding;
using FiveStar.Loader.Shims.D3D12X.States;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// The game packs light as R9G9B9E5 into R32_UINT textures with compute programs (which cannot write R9G9B9E5) and
/// reads it through R9G9B9E5 views placed at those textures' addresses (sky, sky and light probes, bounced light). On
/// the console that is one memory; here each such view reads an R9G9B9E5 shadow of the R32_UINT texture, made at the
/// first view, and the texels are copied into the shadow before a draw or dispatch reads it: at a list's first read and
/// again after any barrier the list put on the source. The hardware then loads, samples, gathers and filters the light
/// at every level, layer, cube face and slice (no program decodes it). On by default; <c>--no-shared-exponent-copies</c>
/// keeps the class-3 decode in the programs (<see cref="Binding.ImageViewClasses"/>).
/// </summary>
internal static unsafe class SharedExponentAliases
{
    /// <summary>DXGI_FORMAT_R32_UINT and R9G9B9E5_SHAREDEXP.</summary>
    private const int R32Uint = 42;
    private const int SharedExponent = 67;

    /// <summary>The most levels times layers copied (a cube of nine levels), as the fork.</summary>
    private const int MaxParts = 54;

    /// <summary>Records of one table searched for a shadow's view at a draw (a program that indexes its T#s reaches 32768).</summary>
    private const uint ScanLimit = 4096;

    private const int BitsPerWord = 32;
    private const int GrowthFactor = 2;

    private static readonly ConcurrentDictionary<nint, SharedExponentAlias?> BySource = new();
    private static readonly ConcurrentDictionary<uint, SharedExponentAlias> ByIndex = new();
    private static readonly object Gate = new();
    private static uint[] _indices = [];
    private static volatile bool _any;

    public static bool Enabled { get; set; } = true;

    /// <summary><c>--trace-shadows</c>: each shadow's first <see cref="TracedCopies"/> copies are logged with their states.</summary>
    public static bool Trace { get; set; }

    /// <summary>Copies logged per shadow with <see cref="Trace"/>.</summary>
    private const int TracedCopies = 6;

    /// <summary>The sample line: shadows made, copies recorded and copies a list's type refused (its states cannot name them).</summary>
    public static string Summary
    {
        get
        {
            var aliases = BySource.Values.Where(alias => alias is not null).ToList();

            return $"R9G9B9E5 shadows: {aliases.Count}, {aliases.Sum(alias => alias!.Copies)} copies, {aliases.Sum(alias => alias!.Refused)} refused by a list's legal states";
        }
    }

    /// <summary>Whether any shadow exists (the draw and barrier hooks do nothing before).</summary>
    public static bool Any => _any;

    /// <summary>Whether a view in <paramref name="viewFormat"/> of a texture read in <paramref name="readFormat"/> goes to a shadow.</summary>
    public static bool Takes(int viewFormat, int readFormat)
    {
        return Enabled && viewFormat == SharedExponent && readFormat == R32Uint;
    }

    /// <summary>The shadow of the GPU-written R32_UINT <paramref name="source"/>, made on first use; null when it cannot be.</summary>
    public static SharedExponentAlias? For(nint device, GpuTexture source)
    {
        if (!source.GpuWritten || source.DesktopFormat != R32Uint)
        {
            return null;
        }

        if (BySource.TryGetValue(source.Desktop, out var made) && (made is null || made.Source == source))
        {
            return made;
        }

        lock (Gate)
        {
            if (BySource.TryGetValue(source.Desktop, out made) && (made is null || made.Source == source))
            {
                return made;
            }

            made = Make(device, source);
            BySource[source.Desktop] = made;
            _any |= made is not null;

            return made;
        }
    }

    /// <summary>The view at game descriptor heap index <paramref name="index"/> reads <paramref name="alias"/>'s shadow.</summary>
    public static void NoteView(uint index, SharedExponentAlias alias)
    {
        ByIndex[index] = alias;

        lock (Gate)
        {
            int word = (int)(index / BitsPerWord);

            if (word >= _indices.Length)
            {
                var grown = new uint[Math.Max(word + 1, _indices.Length * GrowthFactor)];
                _indices.CopyTo(grown, 0);
                Volatile.Write(ref _indices, grown);
            }

            _indices[word] |= 1u << (int)(index % BitsPerWord);
        }

        ShimContext.ActiveRuntime?.D3D12X.Bindings.Window.Records.Watch(index);
    }

    /// <summary>
    /// <paramref name="source"/> is about to be released: its shadow is no longer copied into (the copy would read the
    /// released texture). The shadow itself stays, as views of it may still be held. Whether it had one.
    /// </summary>
    public static bool Released(GpuTexture source)
    {
        if (!_any || !BySource.TryGetValue(source.Desktop, out var alias) || alias is null || alias.Source != source)
        {
            return false;
        }

        BySource.TryRemove(source.Desktop, out _);

        foreach (var (index, held) in ByIndex)
        {
            if (held == alias)
            {
                ByIndex.TryRemove(index, out _);
            }
        }

        return true;
    }

    /// <summary>A barrier of <paramref name="list"/> named the desktop <paramref name="resource"/>: if it is a source, its next read copies.</summary>
    [HotPath]
    public static void Used(nint list, nint resource)
    {
        if (BySource.TryGetValue(resource, out var alias) && alias is not null)
        {
            alias.Used(list);
        }
    }

    /// <summary><paramref name="list"/> closed: its copies count for nothing in its next recording.</summary>
    public static void Closed(nint list)
    {
        foreach (var alias in BySource.Values)
        {
            alias?.Used(list);
        }
    }

    /// <summary>
    /// Before a draw or dispatch on <paramref name="list"/>: each shadow whose view the tables the pipeline reaches hold
    /// (<paramref name="userData"/> pointers into the window, read through <paramref name="records"/>) gets the source's texels unless the list
    /// copied them since it last used the source.
    /// </summary>
    [HotPath]
    public static void BeforeWork(nint list, ListResourceStates states, uint* userData, WindowRecordIndices records, TableReachEntry[] tables)
    {
        var indices = Volatile.Read(ref _indices);

        if (!records.Any)
        {
            return;
        }

        foreach (var entry in tables)
        {
            if (entry.Sgpr >= CommandListBindings.UserDataDwords || userData[entry.Sgpr] % DescriptorWindow.ViewRecordBytes != 0)
            {
                continue;
            }

            // Only the records naming a watched index (the window is write-combined: see WindowRecordIndices).
            uint first = userData[entry.Sgpr] / DescriptorWindow.ViewRecordBytes;
            uint end = WindowRecordIndices.End(first, entry.Records, ScanLimit);

            for (uint slot = records.NextMarked(first, end); slot < end; slot = records.NextMarked(slot + 1, end))
            {
                uint? index = records.HeapIndex(slot);

                if (index is uint at && at / BitsPerWord < indices.Length && (indices[at / BitsPerWord] & (1u << (int)(at % BitsPerWord))) != 0)
                {
                    Read(list, states, at);
                }
            }
        }
    }

    private static void Read(nint list, ListResourceStates states, uint index)
    {
        if (ByIndex.TryGetValue(index, out var alias) && !alias.CurrentOn(list))
        {
            Copy(list, states, alias, "a read");
        }
    }

    /// <summary>
    /// The desktop <paramref name="resource"/> was written on <paramref name="list"/> and is written no more for now: a
    /// barrier took it out of a write state (<see cref="States.ResourceBarrierSlots.Move"/>), or the loader itself
    /// filled it (a ray-tracing stand-in, which no barrier announces). If it is a source, its shadow gets the texels
    /// here, on the writer's list, so every list that reads the light afterwards sees them. Whether it is a source.
    /// <para>
    /// This is what fills the shadows. The copy at a reader's draw (<see cref="BeforeWork"/>) hardly ever runs: boot
    /// w355 made 59 shadows and in 780 s recorded 4 copies for a read (18,918 for the stand-in's fills). The views that
    /// read a shadow are records with a console base, which name no heap index for the window's marks
    /// (<see cref="WindowRecordIndices.Written"/>), and they are noted here by the game's heap index where the window
    /// names desktop ones. So the sky view, the probes and the bounced light read a frame from long ago, or black.
    /// </para>
    /// </summary>
    public static bool Written(nint list, ListResourceStates states, nint resource, string why = "the end of its writes")
    {
        if (!BySource.TryGetValue(resource, out var alias) || alias is null)
        {
            return false;
        }

        Copy(list, states, alias, why);

        return true;
    }

    /// <summary>Records the copy of <paramref name="alias"/>'s source into its shadow on <paramref name="list"/>, counted and (with <see cref="Trace"/>) logged.</summary>
    private static void Copy(nint list, ListResourceStates states, SharedExponentAlias alias, string why)
    {
        var source = alias.Source;
        int sourceState = states.Current(source.Desktop, source.State);
        int shadowState = states.Current(alias.Shadow, SharedExponentCopy.Common);
        int after = SharedExponentCopy.Record(list, alias, sourceState, shadowState, states.LegalStates);
        long count = alias.Count(after >= 0);

        if (Trace && count <= TracedCopies)
        {
            ShimContext.Log?.Info($"  [shadows] copy {count} of source 0x{source.Desktop:x} into shadow 0x{alias.Shadow:x} for {why} on list 0x{list:x} (present {ShimContext.ActiveRuntime?.D3D12X.Presenter.Presents}): source state 0x{sourceState:x}, shadow state 0x{shadowState:x}, legal 0x{states.LegalStates:x}, recorded {after >= 0}; so far {alias.Copies} copied, {alias.Refused} refused");
        }

        if (after < 0)
        {
            return;
        }

        states.Set(alias.Shadow, SharedExponentCopy.Common, after);
        alias.Copied(list);
    }

    private static SharedExponentAlias? Make(nint device, GpuTexture source)
    {
        Marshal.AddRef(device);
        using var desktop = new ID3D12Device(device);
        Marshal.AddRef(source.Desktop);
        using var texture = new ID3D12Resource(source.Desktop);
        var description = texture.Description;

        int layers = description.Dimension == ResourceDimension.Texture3D ? 1 : description.DepthOrArraySize;
        int parts = description.MipLevels * layers;

        if (description.SampleDescription.Count > 1 || parts == 0 || parts > MaxParts || description.Dimension == ResourceDimension.Buffer)
        {
            return null;
        }

        var shadowDescription = description;
        shadowDescription.Format = Format.R9G9B9E5_SharedExp;
        shadowDescription.Flags = ResourceFlags.None;
        shadowDescription.Layout = TextureLayout.Unknown;
        shadowDescription.Alignment = 0;

        var written = Footprints(desktop, description, parts, out ulong bytes);
        var read = Footprints(desktop, shadowDescription, parts, out ulong readBytes);

        if (bytes == 0 || bytes != readBytes || !Alike(written, read))
        {
            return null;
        }

        var shadow = desktop.CreateCommittedResource(HeapType.Default, shadowDescription, Vortice.Direct3D12.ResourceStates.Common);
        var buffer = desktop.CreateCommittedResource(HeapType.Default, Vortice.Direct3D12.ResourceDescription.Buffer(bytes), Vortice.Direct3D12.ResourceStates.Common);
        nint shadowPointer = shadow.NativePointer;
        nint bufferPointer = buffer.NativePointer;
        Marshal.AddRef(shadowPointer);
        Marshal.AddRef(bufferPointer);

        shadow.Dispose();
        buffer.Dispose();

        ShimContext.Log?.Info($"[d3d12_xs] R9G9B9E5 views of the R32_UINT texture at 0x{source.Address:x} ({description.Width}x{description.Height}, {parts} parts) read a shadow the texels are copied into");

        return new SharedExponentAlias(source, shadowPointer, bufferPointer, written, read);
    }

    private static CopyFootprint[] Footprints(ID3D12Device device, Vortice.Direct3D12.ResourceDescription description, int parts, out ulong bytes)
    {
        var footprints = new PlacedSubresourceFootPrint[parts];
        var rows = new uint[parts];
        var rowBytes = new ulong[parts];
        device.GetCopyableFootprints(description, 0, (uint)parts, 0, footprints, rows, rowBytes, out bytes);

        return [.. footprints.Select(part => new CopyFootprint(part.Offset, (int)part.Footprint.Format, part.Footprint.Width, part.Footprint.Height, part.Footprint.Depth, part.Footprint.RowPitch))];
    }

    private static bool Alike(CopyFootprint[] written, CopyFootprint[] read)
    {
        for (int part = 0; part < written.Length; part++)
        {
            if (written[part] with { Format = 0 } != read[part] with { Format = 0 })
            {
                return false;
            }
        }

        return true;
    }
}
