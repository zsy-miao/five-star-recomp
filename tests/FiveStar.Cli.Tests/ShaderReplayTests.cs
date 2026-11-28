// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Cli.Shader;
using FiveStar.Graphics;
using FiveStar.Graphics.Replay;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Xunit;

namespace FiveStar.Cli.Tests;

/// <summary>
/// `fivestar shader replay`: a small bindless dispatch written through the capture writer (a raw input buffer, a raw
/// output buffer and an R32_UINT texture it writes, the user data in the root CBV) replays bit for bit on WARP, and a
/// changed HLSL candidate is reported as differing.
/// </summary>
public sealed class ShaderReplayTests : IDisposable
{
    private const int Threads = 256;
    private const int Side = 8;
    private const uint Multiplier = 3;
    private const uint InputIndex = 10;
    private const uint OutputIndex = 11;
    private const uint ImageIndex = 12;
    private const uint FallbackTable = 2;

    /// <summary>A console address for the buffers, which makes them ranges of game memory.</summary>
    private const ulong ConsoleAddress = 0x2_0000_0000;

    private const string Program = """
        cbuffer UserData : register(b0) { uint4 user[32]; };

        [numthreads(64, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            ByteAddressBuffer input = ResourceDescriptorHeap[user[1].x];
            RWByteAddressBuffer output = ResourceDescriptorHeap[user[1].y];
            RWTexture2D<uint> image = ResourceDescriptorHeap[user[1].z];
            uint value = input.Load(id.x * 4) * user[0].x + id.x;
            output.Store(id.x * 4, value);

            if (id.x < 64)
            {
                image[uint2(id.x % 8, id.x / 8)] = value;
            }
        }
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fivestar-replay-" + Guid.NewGuid().ToString("N"));

    [DxcFact]
    public void A_captured_dispatch_replays_bit_exact_on_warp()
    {
        using var device = GpuDevice.CreateWarp(debugLayer: false);
        var (folder, capture) = WriteCapture(device);

        var report = ReplayCommand.Replay(device, folder, capture, ReplayCandidate.Captured(folder, capture), runs: 3, tolerance: null, shown: 4);

        Assert.Equal(3, report.Outcome.Milliseconds.Count);
        Assert.Equal(3, report.Differences.Count);
        Assert.All(report.Differences, difference => Assert.True(difference.Same, $"{difference.Resource}: {difference.Differing} dwords differ"));
    }

    /// <summary><c>--game-memory system</c>: the ranges of game memory in imported system memory replay the same writes.</summary>
    [DxcFact]
    public void Game_memory_in_imported_system_memory_replays_bit_exact_on_warp()
    {
        using var device = GpuDevice.CreateWarp(debugLayer: false);
        var (folder, capture) = WriteCapture(device, ConsoleAddress);

        var report = ReplayCommand.Replay(device, folder, capture, ReplayCandidate.Captured(folder, capture), runs: 1, tolerance: null, shown: 4, importGameMemory: true);

        Assert.Equal(3, report.Differences.Count);
        Assert.All(report.Differences, difference => Assert.True(difference.Same, $"{difference.Resource}: {difference.Differing} dwords differ"));
    }

    [DxcFact]
    public void A_changed_hlsl_candidate_is_reported_as_differing()
    {
        using var device = GpuDevice.CreateWarp(debugLayer: false);
        var (folder, capture) = WriteCapture(device);
        var changed = Path.Combine(_folder, "changed.hlsl");
        File.WriteAllText(changed, Program.Replace("+ id.x;", "+ id.x + 1;", StringComparison.Ordinal));
        var candidate = ReplayCandidate.FromHlsl(folder, capture, changed, Compiler());

        var report = ReplayCommand.Replay(device, folder, capture, candidate, runs: 1, tolerance: null, shown: 4);

        var output = report.Differences.Single(difference => difference.Resource == "output");
        Assert.Equal(Threads, output.Differing);
        Assert.Equal([0UL, 4UL, 8UL, 12UL], output.FirstOffsets);
        Assert.Equal(Side * Side, report.Differences.Single(difference => difference.Resource == "image").Differing);
        Assert.True(report.Differences.Single(difference => difference.Resource == "input").Same);
        Assert.NotNull(candidate.CompileMilliseconds);
    }

    /// <summary>
    /// w162's captures held a BC7_UNORM_SRGB SRV over a BC5_UNORM texture: creating it removed the RTX 4090 device
    /// (DXGI_ERROR_INVALID_CALL, no debug message), so the replay writes a null view for a view that does not fit.
    /// </summary>
    [Fact]
    public void A_view_that_does_not_fit_its_texture_is_found_before_it_is_created()
    {
        var bc5 = new CapturedResource("t", "texture", (int)ResourceDimension.Texture2D, 256, 256, 1, 7, (int)Format.BC5_UNorm, 0, 0, null, null);
        var bc7 = bc5 with { Format = (int)Format.BC7_Typeless };

        byte[] Srv(Format format, uint most, uint mips) => Bytes(new ShaderResourceViewDescription
        {
            Format = format,
            ViewDimension = ShaderResourceViewDimension.Texture2D,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Texture2D = new Texture2DShaderResourceView { MostDetailedMip = most, MipLevels = mips },
        });

        Assert.NotNull(ViewFit.Problem(bc5, CapturedViewKind.ShaderResource, Srv(Format.BC7_UNorm_SRgb, 1, 6)));
        Assert.Null(ViewFit.Problem(bc7, CapturedViewKind.ShaderResource, Srv(Format.BC7_UNorm_SRgb, 1, 6)));
        Assert.NotNull(ViewFit.Problem(bc7, CapturedViewKind.ShaderResource, Srv(Format.BC7_UNorm_SRgb, 2, 6)));
        Assert.Null(ViewFit.Problem(bc5, CapturedViewKind.ShaderResource, Srv(Format.BC5_UNorm, 0, uint.MaxValue)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static Vortice.Direct3D12.ResourceDescription ImageDescription()
    {
        return Vortice.Direct3D12.ResourceDescription.Texture2D(Format.R32_UInt, Side, Side, 1, 1, 1, 0, ResourceFlags.AllowUnorderedAccess);
    }

    /// <summary>The capture of one 4-group dispatch: inputs, the outputs the CPU expects, the views and the compiled program.</summary>
    private (CaptureFolder Folder, DispatchCapture Capture) WriteCapture(GpuDevice device, ulong? consoleAddress = null)
    {
        var folder = new CaptureFolder(_folder);
        var writer = new CaptureWriter(folder);
        var input = Enumerable.Range(0, Threads).Select(index => (uint)(index * 7)).ToArray();
        var expected = input.Select((value, index) => (value * Multiplier) + (uint)index).ToArray();
        var inputBytes = MemoryMarshal.AsBytes(input.AsSpan());

        writer.Add(BufferShape("input", inputBytes.Length) with { ConsoleAddress = consoleAddress }, inputBytes, inputBytes);
        writer.Add(BufferShape("output", inputBytes.Length) with { ConsoleAddress = consoleAddress }, [], MemoryMarshal.AsBytes(expected.AsSpan()));
        AddImage(device, writer, expected);
        writer.View(InputIndex, CapturedViewKind.ShaderResource, "input", Bytes(RawRead(Threads)));
        writer.View(OutputIndex, CapturedViewKind.UnorderedAccess, "output", Bytes(RawWrite(Threads)));
        writer.View(ImageIndex, CapturedViewKind.UnorderedAccess, "image", Bytes(new UnorderedAccessViewDescription { Format = Format.R32_UInt, ViewDimension = UnorderedAccessViewDimension.Texture2D }));

        var hlsl = folder.WriteText("program/program.hlsl", Program);
        var program = new CapturedProgram("CS_Synthetic", "cs", null, hlsl, null, null);
        var dxil = ReplayCandidate.FromHlsl(folder, PlaceholderCapture(program), folder.PathOf(hlsl), Compiler()).Dxil;
        program = program with { Dxil = folder.WriteFile("program/program.dxil", dxil) };
        uint[] userData = [Multiplier, 0, 0, 0, InputIndex, OutputIndex, ImageIndex];

        return (folder, writer.Finish("CS_Synthetic", program, userConstants: false, userData, [new CapturedDispatch(Threads / 64, 1, 1)], FallbackTable));
    }

    private static void AddImage(GpuDevice device, CaptureWriter writer, uint[] expected)
    {
        var description = ImageDescription();
        var footprints = new PlacedSubresourceFootPrint[1];
        var rows = new uint[1];
        var rowBytes = new ulong[1];
        device.Device.GetCopyableFootprints(description, 0, 1, 0, footprints, rows, rowBytes, out ulong total);

        var footprint = footprints[0];
        var after = new byte[total];

        for (var row = 0; row < Side; row++)
        {
            MemoryMarshal.AsBytes(expected.AsSpan(row * Side, Side)).CopyTo(after.AsSpan((int)footprint.Offset + (row * (int)footprint.Footprint.RowPitch)));
        }

        var captured = new CapturedFootprint(footprint.Offset, (int)Format.R32_UInt, Side, Side, 1, footprint.Footprint.RowPitch, rows[0], rowBytes[0]);
        var shape = new CapturedResource("image", "test image", (int)ResourceDimension.Texture2D, Side, Side, 1, 1, (int)Format.R32_UInt, (int)ResourceFlags.AllowUnorderedAccess, total, null, null)
        {
            Footprints = [captured],
        };
        writer.Add(shape, [], after);
    }

    private static CapturedResource BufferShape(string id, int bytes)
    {
        return new CapturedResource(id, "test buffer", CapturedResource.BufferDimension, (ulong)bytes, 1, 1, 1, 0, 0, (ulong)bytes, null, null);
    }

    private static DispatchCapture PlaceholderCapture(CapturedProgram program)
    {
        return new DispatchCapture("CS_Synthetic", program, false, [], [], 0, FallbackTable, [], [], []);
    }

    private static ShaderResourceViewDescription RawRead(int dwords)
    {
        return new ShaderResourceViewDescription
        {
            Format = Format.R32_Typeless,
            ViewDimension = ShaderResourceViewDimension.Buffer,
            Shader4ComponentMapping = ShaderComponentMapping.Default,
            Buffer = new BufferShaderResourceView { NumElements = (uint)dwords, Flags = BufferShaderResourceViewFlags.Raw },
        };
    }

    private static UnorderedAccessViewDescription RawWrite(int dwords)
    {
        return new UnorderedAccessViewDescription
        {
            Format = Format.R32_Typeless,
            ViewDimension = UnorderedAccessViewDimension.Buffer,
            Buffer = new BufferUnorderedAccessView { NumElements = (uint)dwords, Flags = BufferUnorderedAccessViewFlags.Raw },
        };
    }

    private static byte[] Bytes<T>(T description)
        where T : unmanaged
    {
        return MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref description, 1)).ToArray();
    }

    private static FiveStar.Shaders.Compilation.IShaderCompiler Compiler()
    {
        return ShaderTools.Compiler(new CommandArguments([], new HashSet<string>()), retry: false);
    }
}
