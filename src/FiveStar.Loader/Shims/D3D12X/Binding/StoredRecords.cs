// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Shaders.Disassembly;
using FiveStar.Shaders.Translation;
using FiveStar.Shaders.Translation.Core;

namespace FiveStar.Loader.Shims.D3D12X.Binding;

/// <summary>
/// The table records a program writes textures through: the entry SGPR and record of each T# an <c>image_store*</c> or
/// <c>image_atomic*</c> takes, loaded by an <c>s_load</c> at an immediate offset through a table pointer (an entry
/// SGPR, copied by <c>s_mov_b32</c> or not, with a high dword set to 0), as <see cref="TableReach"/> follows them. These
/// are a pass's own outputs; the other records in its reach are inputs and other passes' textures (the tiled ray-traced
/// sun shadows read the global table in s4 up to record 70, which holds the cascade shadow textures at 62 and 63, and
/// store only through record 10 of their own table in s5). A T# loaded at an offset the program computes is not listed.
/// The scan is linear, as TableReach's.
/// </summary>
internal static class StoredRecords
{
    private const int DestinationOperand = 0;
    private const int BaseOperand = 1;
    private const int OffsetOperand = 2;
    private const int LoadOperands = 3;

    /// <summary>An image store's T# operand (after the value and the address).</summary>
    private const int ResourceOperand = 2;
    private const string LoadPrefix = "s_load_";
    private const string Copy = "s_mov_b32";

    private static readonly string[] Stores = ["image_store", "image_atomic"];

    /// <summary>Scalar instructions whose first operand is read, not written (TableReach's list).</summary>
    private static readonly string[] Readers = ["s_cmp", "s_bitcmp", "s_cbranch", "s_branch", "s_setpc", "s_store", "s_buffer_store", "s_atomic", "s_sendmsg", "s_waitcnt", "s_setreg"];

    /// <summary>The (entry SGPR, record) pairs <paramref name="instructions"/> store through, each once.</summary>
    public static IReadOnlyList<(int Sgpr, uint Record)> Find(IReadOnlyList<Instruction> instructions)
    {
        var state = new Scan();

        foreach (var instruction in instructions)
        {
            var (mnemonic, arguments) = Split(instruction);

            if (Stores.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
            {
                state.Store(arguments);

                continue;
            }

            var destination = Destination(mnemonic, arguments);

            if (destination is not null)
            {
                state.Write(mnemonic, arguments, destination);
            }
        }

        return [.. state.Stored];
    }

    /// <summary>The SGPRs a scalar instruction writes; null for a reader, a vector instruction or an operand that does not parse.</summary>
    private static Operand? Destination(string mnemonic, List<string> arguments)
    {
        bool scalar = mnemonic.StartsWith("s_", StringComparison.Ordinal) || mnemonic.StartsWith("v_read", StringComparison.Ordinal)
            || mnemonic.StartsWith("v_cmp", StringComparison.Ordinal);

        if (arguments.Count == 0 || !scalar || Readers.Any(prefix => mnemonic.StartsWith(prefix, StringComparison.Ordinal)))
        {
            return null;
        }

        var destination = Parse(arguments[DestinationOperand]);

        if (destination is null || destination.Kind != OperandKind.Scalar)
        {
            return null;
        }

        return destination;
    }

    private static Operand? Parse(string text)
    {
        try
        {
            return OperandParser.Parse(text);
        }
        catch (UnsupportedProgramException)
        {
            return null;
        }
    }

    /// <summary>The mnemonic and the operands, each without the modifiers after it (<c>s[8:15] dmask:0xf</c>).</summary>
    private static (string Mnemonic, List<string> Arguments) Split(Instruction instruction)
    {
        var line = instruction.Text.Split(';')[0].Split("//")[0].Trim();
        var space = line.IndexOfAny([' ', '\t']);

        if (space < 0)
        {
            return (line, []);
        }

        var arguments = line[space..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)[0])
            .ToList();

        return (line[..space], arguments);
    }

    /// <summary>What the SGPRs hold along the scan: a copied entry SGPR, 0, or the record a T# was loaded from.</summary>
    private sealed class Scan
    {
        private readonly Dictionary<int, int> _origin = [];
        private readonly HashSet<int> _zero = [];
        private readonly HashSet<int> _written = [];
        private readonly Dictionary<int, (int Sgpr, uint Record)> _loaded = [];

        /// <summary>The records stored through so far.</summary>
        public HashSet<(int Sgpr, uint Record)> Stored { get; } = [];

        /// <summary>An image store: its T#'s record, when one is known.</summary>
        public void Store(List<string> arguments)
        {
            if (arguments.Count <= ResourceOperand)
            {
                return;
            }

            var resource = Parse(arguments[ResourceOperand]);

            if (resource is not null && resource.Kind == OperandKind.Scalar && _loaded.TryGetValue(resource.Low, out var record))
            {
                Stored.Add(record);
            }
        }

        /// <summary>A scalar instruction wrote <paramref name="destination"/>: a load's record, a copied pointer, a zero, or nothing known.</summary>
        public void Write(string mnemonic, List<string> arguments, Operand destination)
        {
            var record = Record(mnemonic, arguments);
            int? copied = null;
            bool isZero = false;

            if (mnemonic == Copy && arguments.Count > 1)
            {
                var source = Parse(arguments[1]);
                bool register = source is not null && source.Kind == OperandKind.Scalar && source.Low == source.High;
                copied = register ? Entry(source!.Low) : null;
                isZero = source is not null && source.IsImmediate && source.Value == 0;
            }

            for (int sgpr = destination.Low; sgpr <= destination.High; sgpr++)
            {
                _written.Add(sgpr);
                _origin.Remove(sgpr);
                _zero.Remove(sgpr);
                _loaded.Remove(sgpr);
            }

            if (copied is not null)
            {
                _origin[destination.Low] = copied.Value;
            }

            if (isZero)
            {
                _zero.Add(destination.Low);
            }

            if (record is not null)
            {
                _loaded[destination.Low] = record.Value;
            }
        }

        /// <summary>The entry SGPR and record an <c>s_load</c> reads at an immediate offset through a table pointer; null for anything else.</summary>
        private (int Sgpr, uint Record)? Record(string mnemonic, List<string> arguments)
        {
            if (!mnemonic.StartsWith(LoadPrefix, StringComparison.Ordinal) || arguments.Count < LoadOperands)
            {
                return null;
            }

            var pair = Parse(arguments[BaseOperand]);
            var offset = Parse(arguments[OffsetOperand]);

            if (pair is null || offset is null || pair.Kind != OperandKind.Scalar || !_zero.Contains(pair.High) || !offset.IsImmediate)
            {
                return null;
            }

            int? entry = Entry(pair.Low);

            if (entry is null)
            {
                return null;
            }

            return (entry.Value, offset.Value / DescriptorWindow.ViewRecordBytes);
        }

        /// <summary>The entry SGPR whose value <paramref name="sgpr"/> holds now: itself until written, a copied one, or none.</summary>
        private int? Entry(int sgpr)
        {
            if (!_written.Contains(sgpr))
            {
                return sgpr;
            }

            if (_origin.TryGetValue(sgpr, out int copied))
            {
                return copied;
            }

            return null;
        }
    }
}
