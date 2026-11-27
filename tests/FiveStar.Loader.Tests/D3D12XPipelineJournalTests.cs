// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.Pipelines;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// The journal of the pipelines the game asked for: kept across boots in first-use order, each description once, read
/// back as warm-up targets that make the same desktop pipeline.
/// </summary>
public sealed class D3D12XPipelineJournalTests
{
    [Fact]
    public void Descriptions_come_back_once_each_in_first_use_order_across_boots()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-journal-");

        try
        {
            var graphics = Graphics();
            var compute = new ComputePipelineDescription(0x1234, Shader(7));

            using (var boot = PipelineJournal.Open(folder.FullName))
            {
                boot.Record(graphics);
                boot.Record(graphics with { GameRootSignature = 0x5678 });
            }

            using (var boot = PipelineJournal.Open(folder.FullName))
            {
                boot.Record(compute);
                boot.Record(graphics);
                Assert.Equal(2, boot.Count);
            }

            var targets = PipelineJournal.Read(folder.FullName);

            Assert.Equal(2, targets.Count);
            var read = targets[0].Graphics!;
            Assert.Equal(graphics.Shaders.Select(shader => shader.Bytes), read.Shaders.Select(shader => shader.Bytes));
            Assert.Equal(graphics.Blend, read.Blend);

            Assert.Equal(graphics.InputLayout, read.InputLayout);
            Assert.Equal(graphics.RenderTargetFormats, read.RenderTargetFormats);
            Assert.Equal(graphics.DepthStencilFormat, read.DepthStencilFormat);
            Assert.Equal(compute.Shader.Bytes, targets[1].Compute!.Shader.Bytes);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Boot w353's journal after a full disk: a graphics record without its shader count, whose first "length" read
    /// back negative and ended every later boot at its start. It names nothing; the records around it stay.
    /// </summary>
    [Fact]
    public void A_damaged_record_names_nothing_and_ends_no_boot()
    {
        var folder = Directory.CreateTempSubdirectory("fivestar-journal-");

        try
        {
            using (var boot = PipelineJournal.Open(folder.FullName))
            {
                boot.Record(Graphics());
            }

            byte[] damaged = [0, 0x18, 0, 0, 0, 0xdd, 0x03, 0x80, 0xd5, 0xf4, 0x15, 0xff, 0x03];
            var path = Path.Combine(folder.FullName, PipelineJournal.FileName);

            using (var file = new FileStream(path, FileMode.Append))
            {
                Assert.True(PipelineJournal.Append(file, damaged, out _));
                Assert.True(PipelineJournal.Append(file, [0, 0xff, 0xff, 0xff, 0xff], out _));
            }

            using (var boot = PipelineJournal.Open(folder.FullName))
            {
                boot.Record(new ComputePipelineDescription(0x1234, Shader(7)));
            }

            var targets = PipelineJournal.Read(folder.FullName);

            Assert.Equal(2, targets.Count);
            Assert.NotNull(targets[0].Graphics);
            Assert.NotNull(targets[1].Compute);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>A write that fails part way (a full disk) leaves the file ending where the record started.</summary>
    [Fact]
    public void A_record_that_cannot_be_written_is_cut_back()
    {
        using var file = new FullDisk(room: 20);

        Assert.True(PipelineJournal.Append(file, [1, 2, 3, 4], out bool intact));
        Assert.True(intact);
        Assert.False(PipelineJournal.Append(file, new byte[32], out intact));
        Assert.True(intact);
        Assert.Equal(8, file.Length);
        Assert.Equal(8, file.Position);

        Assert.True(PipelineJournal.Append(file, [5, 6], out _));
        Assert.Equal([4, 0, 0, 0, 1, 2, 3, 4, 2, 0, 0, 0, 5, 6], file.ToArray());
    }

    /// <summary>A stream that takes <c>room</c> bytes and then fails, keeping the part of a write that fitted, as a full disk does.</summary>
    private sealed class FullDisk(int room) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Write(buffer.ToArray(), 0, buffer.Length);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            int fits = (int)Math.Max(0, Math.Min(count, room - Position));
            base.Write(buffer, offset, fits);

            if (fits < count)
            {
                throw new IOException("There is not enough space on the disk.");
            }
        }
    }

    private static GraphicsPipelineDescription Graphics()
    {
        return GraphicsPipelineDescription.Defaults(0x1234) with
        {
            Shaders = [Shader(1), Shader(2)],
            InputLayout = [new VertexElement("POSITION", 0, 6, 0, 0, 0, 0)],
            RenderTargetFormats = [28, 10],
            DepthStencilFormat = 20,
        };
    }

    private static ShaderSource Shader(byte seed)
    {
        return new ShaderSource(ShaderSourceKind.CacheCode, Enumerable.Repeat(seed, 24).ToArray());
    }
}
