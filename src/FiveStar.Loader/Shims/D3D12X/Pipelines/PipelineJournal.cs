// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Buffers.Binary;
using System.Security.Cryptography;
using FiveStar.Shaders.Cache;

namespace FiveStar.Loader.Shims.D3D12X.Pipelines;

/// <summary>
/// The pipelines the game used, in the order it first used them, across boots: <c>pipelines-used.bin</c> in the
/// pipeline cache folder (<see cref="PipelineJournalFile"/>). Each record is a game description (every creation slot,
/// derives included) with its shaders as code-block prefixes, so it names the pipeline whatever the translation: the
/// next boot builds these at creation, the warm-up builds them first. A record that cannot be written is dropped and
/// the file cut back to its start, and a damaged record read back names nothing: the journal is a hint, never a
/// reason for a draw or a boot to fail.
/// </summary>
internal sealed class PipelineJournal : IDisposable
{
    /// <summary>The journal's file name in the pipeline cache folder.</summary>
    public const string FileName = PipelineJournalFile.FileName;

    private const byte GraphicsKind = PipelineJournalFile.GraphicsKind;
    private const byte ComputeKind = PipelineJournalFile.ComputeKind;

    /// <summary>A FileStream buffer size that buffers nothing: a record goes to the file in one write, or not at all.</summary>
    private const int Unbuffered = 1;

    private readonly Lock _gate = new();
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly FileStream _file;

    /// <summary>Set when a failed append could not be cut back: nothing more is appended in this run.</summary>
    private bool _closed;

    /// <summary>Whether a dropped record was logged (once per run).</summary>
    private bool _warned;

    private PipelineJournal(FileStream file, IEnumerable<byte[]> records)
    {
        _file = file;

        foreach (var record in records)
        {
            _known.Add(Hash(record));
        }
    }

    /// <summary>Descriptions recorded so far, this run's included.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _known.Count;
            }
        }
    }

    /// <summary>The journal in <paramref name="folder"/> for appending; a damaged one starts over.</summary>
    public static PipelineJournal Open(string folder)
    {
        var path = Path.Combine(folder, FileName);
        var records = PipelineJournalFile.Records(path);
        var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, Unbuffered);

        if (records is null)
        {
            file.SetLength(0);
            var header = new BinaryWriter(file);
            header.Write(PipelineJournalFile.Magic);
            header.Write(PipelineJournalFile.Version);
            header.Flush();
            records = [];
        }
        else
        {
            // A record cut off by a crash is dropped, so the next one starts where it started.
            file.SetLength(PipelineJournalFile.HeaderSize + records.Sum(record => (long)sizeof(int) + record.Length));
        }

        file.Seek(0, SeekOrigin.End);

        return new PipelineJournal(file, records);
    }

    /// <summary>The descriptions of the journal in <paramref name="folder"/> in first-use order; none without one.</summary>
    public static List<WarmTarget> Read(string folder)
    {
        var targets = new List<WarmTarget>();

        foreach (var record in PipelineJournalFile.Records(Path.Combine(folder, FileName)) ?? [])
        {
            var target = Decode(record, targets.Count);

            if (target is not null)
            {
                targets.Add(target);
            }
        }

        return targets;
    }

    /// <summary>Appends a graphics description the journal does not have yet.</summary>
    public void Record(GraphicsPipelineDescription description)
    {
        Append(Encode(description));
    }

    /// <summary>Appends a compute description the journal does not have yet.</summary>
    public void Record(ComputePipelineDescription description)
    {
        Append(Encode(description));
    }

    /// <summary>Whether an earlier boot (or this one) asked for the description.</summary>
    public bool Contains(GraphicsPipelineDescription description)
    {
        return Known(Encode(description));
    }

    /// <summary>Whether an earlier boot (or this one) asked for the description.</summary>
    public bool Contains(ComputePipelineDescription description)
    {
        return Known(Encode(description));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _file.Dispose();
        }
    }

    /// <summary>
    /// Writes one record (its length, then its bytes) at the end of <paramref name="file"/> in a single write. When the
    /// write fails (a full disk: boot w353, whose half-written record then crashed every later boot at its start) the
    /// file is cut back to where the record started, so the next record starts on a record boundary. Whether it was
    /// written; <paramref name="intact"/> is false when it was not and the file could not be cut back either.
    /// </summary>
    internal static bool Append(Stream file, byte[] record, out bool intact)
    {
        long start = file.Position;
        var framed = new byte[sizeof(int) + record.Length];
        BinaryPrimitives.WriteInt32LittleEndian(framed, record.Length);
        record.CopyTo(framed, sizeof(int));
        intact = true;

        try
        {
            file.Write(framed);
            file.Flush();

            return true;
        }
        catch (IOException)
        {
            intact = CutBack(file, start);

            return false;
        }
    }

    /// <summary>Ends <paramref name="file"/> at <paramref name="start"/> again; whether that worked.</summary>
    private static bool CutBack(Stream file, long start)
    {
        try
        {
            file.SetLength(start);
            file.Position = start;

            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static byte[]? Encode(GraphicsPipelineDescription description)
    {
        return Encode(GraphicsKind, writer => Write(writer, description));
    }

    private static byte[]? Encode(ComputePipelineDescription description)
    {
        return Encode(ComputeKind, writer => WriteShader(writer, description.Shader));
    }

    /// <summary>The record of a description; null when a shader has no readable code block (the creation fails anyway).</summary>
    private static byte[]? Encode(byte kind, Action<BinaryWriter> write)
    {
        try
        {
            using var buffer = new MemoryStream();
            using var writer = new BinaryWriter(buffer);
            writer.Write(kind);
            write(writer);
            writer.Flush();

            return buffer.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private bool Known(byte[]? record)
    {
        if (record is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _known.Contains(Hash(record));
        }
    }

    private void Append(byte[]? record)
    {
        if (record is null)
        {
            return;
        }

        string hash = Hash(record);

        lock (_gate)
        {
            if (_closed || !_known.Add(hash))
            {
                return;
            }

            // Written per record: a boot that crashes keeps what it asked for.
            if (Append(_file, record, out bool intact))
            {
                return;
            }

            // The pipeline is not journalled (a later boot builds it at first use); the game's call goes on.
            _known.Remove(hash);
            _closed = !intact;

            if (_warned)
            {
                return;
            }

            _warned = true;
            ShimContext.Log?.Warn($"[d3d12_xs] pipeline journal: a record could not be written (disk full?) and was dropped; file cut back to its start: {intact} (if not, nothing more is journalled in this run)");
        }
    }

    private static void Write(BinaryWriter writer, GraphicsPipelineDescription description)
    {
        writer.Write(description.Shaders.Count);

        foreach (var shader in description.Shaders)
        {
            WriteShader(writer, shader);
        }

        WriteBytes(writer, description.Blend);
        writer.Write(description.SampleMask);
        WriteBytes(writer, description.Rasterizer);
        WriteBytes(writer, description.DepthStencil);
        writer.Write(description.InputLayout.Count);

        foreach (var element in description.InputLayout)
        {
            writer.Write(element.Semantic);
            writer.Write(element.SemanticIndex);
            writer.Write(element.Format);

            writer.Write(element.InputSlot);
            writer.Write(element.Offset);
            writer.Write(element.Classification);
            writer.Write(element.StepRate);
        }

        writer.Write(description.StripCut);
        writer.Write(description.TopologyType);
        writer.Write(description.RenderTargetFormats.Count);

        foreach (var format in description.RenderTargetFormats)
        {
            writer.Write(format);
        }

        writer.Write(description.DepthStencilFormat);
        writer.Write(description.SampleCount);
        writer.Write(description.SampleQuality);
    }

    /// <summary>A shader as the code-block prefix the translated programs are found by.</summary>
    private static void WriteShader(BinaryWriter writer, ShaderSource shader)
    {
        WriteBytes(writer, ProgramKeys.Prefix(shader));
    }

    private static void WriteBytes(BinaryWriter writer, byte[] bytes)
    {
        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static WarmTarget? Decode(byte[] record, int index)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(record));

            if (reader.ReadByte() == ComputeKind)
            {
                return new WarmTarget(index, null, new ComputePipelineDescription(0, ReadShader(reader)));
            }

            return new WarmTarget(index, ReadGraphics(reader), null);
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException or FormatException)
        {
            // A damaged record (a count or length that is not one) names nothing.
            return null;
        }
    }

    private static GraphicsPipelineDescription ReadGraphics(BinaryReader reader)
    {
        var shaders = Enumerable.Range(0, reader.ReadInt32()).Select(_ => ReadShader(reader)).ToList();
        var blend = ReadBytes(reader);
        var sampleMask = reader.ReadUInt32();
        var rasterizer = ReadBytes(reader);
        var depthStencil = ReadBytes(reader);

        var elements = Enumerable.Range(0, reader.ReadInt32())
            .Select(_ => new VertexElement(reader.ReadString(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()))
            .ToList();
        var stripCut = reader.ReadInt32();
        var topology = reader.ReadInt32();
        var formats = Enumerable.Range(0, reader.ReadInt32()).Select(_ => reader.ReadInt32()).ToList();

        return new GraphicsPipelineDescription(0, shaders, blend, sampleMask, rasterizer, depthStencil, elements, stripCut, topology, formats, reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
    }

    private static ShaderSource ReadShader(BinaryReader reader)
    {
        return new ShaderSource(ShaderSourceKind.CacheCode, ReadBytes(reader));
    }

    private static byte[] ReadBytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();

        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new EndOfStreamException();
        }

        return reader.ReadBytes(length);
    }

    private static string Hash(byte[] record)
    {
        return Convert.ToHexString(SHA256.HashData(record));
    }
}
