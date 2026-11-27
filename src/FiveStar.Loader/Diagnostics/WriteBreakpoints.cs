// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using FiveStar.Loader.Native;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// Hardware write breakpoints on up to <see cref="Slots"/> game addresses, to name a CPU writer where a page cannot be
/// made read-only (VirtualProtect fails on the game's graphics heap once a D3D12 heap is opened over it). Every thread
/// gets the addresses in DR0 to DR2 (8 bytes each) and a cell of our own in DR3; a write raises a single-step
/// exception after the storing instruction, which <see cref="EntryCode"/>, the first bytes of the fault reporter's
/// vectored handler, notes in a ring without entering managed code (the writer may be our own C#) and continues.
/// The 4 GiB line watch arms new threads and logs the ring: the writer's address, thread and the code addresses on its
/// stack. A write by the GPU, or by the kernel for a read into the buffer, raises nothing.
/// </summary>
internal static unsafe class WriteBreakpoints
{
    /// <summary>Game addresses watched at once; the fourth debug register watches the self-test cell.</summary>
    public const int Slots = 3;

    private const int RingEntries = 256;
    private const int EntryBytes = 128;
    private const int HeaderBytes = 64;
    private const int StackWords = 12;
    private const int CellOffset = 8;

    // CONTEXT (winnt.h, AMD64): size, alignment, ContextFlags, Dr0 to Dr3, Dr6, Dr7; CONTEXT_AMD64 | CONTEXT_DEBUG_REGISTERS.
    private const int ContextBytes = 0x4D0;
    private const int ContextAlignment = 16;
    private const int ContextFlagsOffset = 0x30;
    private const int Dr0Offset = 0x48;
    private const int Dr7Offset = 0x70;
    private const uint ContextDebugRegisters = 0x100010;

    /// <summary>DR7 for register n: its local enable bit, and at 16 + 4n a write breakpoint (1) of 8 bytes (2 &lt;&lt; 2).</summary>
    private const ulong WriteEightBytes = 0x9;
    private const int ConditionShift = 16;
    private const int ConditionBits = 4;

    /// <summary>THREAD_SET_CONTEXT | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION.</summary>
    private const uint ThreadAccess = 0x10 | 0x8 | 0x40;

    private const int PerSite = 3;
    private const int MaxSites = 48;
    private const int ShownSites = 8;
    private const ulong RegisterMask = 0xF;

    private static readonly byte* Ring = (byte*)NativeMemory.AllocZeroed(HeaderBytes + (RingEntries * EntryBytes));
    private static readonly ulong[] Addresses = new ulong[Slots];
    private static readonly HashSet<uint> Armed = [];
    private static readonly Dictionary<ulong, long> Sites = [];
    private static ulong _dr7;
    private static uint _seen;
    private static long _hits;
    private static long _failed;

    /// <summary>Whether addresses are set.</summary>
    public static bool Active => _dr7 != 0;

    /// <summary>The ring's start, for a test of <see cref="EntryCode"/>: its count, then at + 64 the entries of 128 bytes.</summary>
    public static byte* RingStart => Ring;

    /// <summary>
    /// The handler's first instructions (rcx is the EXCEPTION_POINTERS): a single-step exception whose DR6 names one of
    /// the four breakpoints appends {DR6, Rip, thread id, Rsp, 12 stack qwords} to the ring, clears DR6 and returns
    /// EXCEPTION_CONTINUE_EXECUTION; anything else falls through to the code that follows, rcx unchanged.
    /// </summary>
    public static byte[] EntryCode()
    {
        var code = new List<byte>();
        code.AddRange([0x48, 0x8B, 0x01]);                                  // mov rax,[rcx]
        code.AddRange([0x81, 0x38, 0x04, 0x00, 0x00, 0x80]);                // cmp dword ptr [rax],80000004h
        code.AddRange([0x75, 0x00]);                                        // jne past
        int notStep = code.Count - 1;
        code.AddRange([0x48, 0x8B, 0x51, 0x08]);                            // mov rdx,[rcx+8]
        code.AddRange([0x48, 0x8B, 0x42, 0x68]);                            // mov rax,[rdx+68h] (Dr6)
        code.AddRange([0xA8, 0x0F]);                                        // test al,0Fh
        code.AddRange([0x74, 0x00]);                                        // jz past
        int notOurs = code.Count - 1;
        code.AddRange([0x49, 0xB8]);                                        // mov r8,ring
        code.AddRange(BitConverter.GetBytes((ulong)Ring));
        code.AddRange([0x41, 0xB9, 0x01, 0x00, 0x00, 0x00]);                // mov r9d,1
        code.AddRange([0xF0, 0x45, 0x0F, 0xC1, 0x08]);                      // lock xadd [r8],r9d
        code.AddRange([0x41, 0x81, 0xE1, 0xFF, 0x00, 0x00, 0x00]);          // and r9d,255
        code.AddRange([0x49, 0xC1, 0xE1, 0x07]);                            // shl r9,7
        code.AddRange([0x4F, 0x8D, 0x44, 0x08, 0x40]);                      // lea r8,[r8+r9+40h]
        code.AddRange([0x49, 0x89, 0x00]);                                  // mov [r8],rax
        code.AddRange([0x48, 0x8B, 0x82, 0xF8, 0x00, 0x00, 0x00]);          // mov rax,[rdx+0F8h] (Rip)
        code.AddRange([0x49, 0x89, 0x40, 0x08]);                            // mov [r8+8],rax
        code.AddRange([0x65, 0x48, 0x8B, 0x04, 0x25, 0x48, 0x00, 0x00, 0x00]);  // mov rax,gs:[48h] (thread id)
        code.AddRange([0x49, 0x89, 0x40, 0x10]);                            // mov [r8+10h],rax
        code.AddRange([0x4C, 0x8B, 0x92, 0x98, 0x00, 0x00, 0x00]);          // mov r10,[rdx+98h] (Rsp)
        code.AddRange([0x4D, 0x89, 0x50, 0x18]);                            // mov [r8+18h],r10
        code.AddRange([0x45, 0x31, 0xC9]);                                  // xor r9d,r9d
        code.AddRange([0x4B, 0x8B, 0x04, 0xCA]);                            // next: mov rax,[r10+r9*8]
        code.AddRange([0x4B, 0x89, 0x44, 0xC8, 0x20]);                      // mov [r8+r9*8+20h],rax
        code.AddRange([0x49, 0xFF, 0xC1]);                                  // inc r9
        code.AddRange([0x49, 0x83, 0xF9, StackWords]);                      // cmp r9,12
        code.AddRange([0x72, 0xEE]);                                        // jb next
        code.AddRange([0x48, 0xC7, 0x42, 0x68, 0x00, 0x00, 0x00, 0x00]);    // mov qword ptr [rdx+68h],0
        code.AddRange([0xB8, 0xFF, 0xFF, 0xFF, 0xFF]);                      // mov eax,-1
        code.Add(0xC3);                                                     // ret
        code[notStep] = (byte)(code.Count - (notStep + 1));
        code[notOurs] = (byte)(code.Count - (notOurs + 1));

        return [.. code];
    }

    /// <summary>
    /// Sets the addresses (rounded down to 8 bytes) and arms every thread; then writes the self-test cell, so the log
    /// shows one hit of our own when the breakpoints work.
    /// </summary>
    public static void Arm(ReadOnlySpan<ulong> addresses, LoaderLog log)
    {
        ulong dr7 = (1UL << (2 * Slots)) | (WriteEightBytes << (ConditionShift + (ConditionBits * Slots)));

        for (int i = 0; i < Slots && i < addresses.Length; i++)
        {
            Addresses[i] = addresses[i] & ~7UL;
            dr7 |= (1UL << (2 * i)) | (WriteEightBytes << (ConditionShift + (ConditionBits * i)));
        }

        _dr7 = dr7;
        Refresh();
        log.Info($"[write break] write breakpoints at {string.Join(", ", Addresses.Where(a => a != 0).Select(a => $"0x{a:x}"))} and a self-test cell, on {Armed.Count} threads ({Interlocked.Read(ref _failed)} refused)");
        *(ulong*)(Ring + CellOffset) = 1;
    }

    /// <summary>Arms the threads not armed yet (new ones since the last call).</summary>
    public static void Refresh()
    {
        if (_dr7 == 0)
        {
            return;
        }

        using var process = Process.GetCurrentProcess();
        var alive = new HashSet<uint>();
        byte* context = (byte*)NativeMemory.AlignedAlloc(ContextBytes, ContextAlignment);

        try
        {
            foreach (ProcessThread thread in process.Threads)
            {
                uint id = (uint)thread.Id;
                alive.Add(id);

                if (Armed.Add(id) && !Set(id, context))
                {
                    Interlocked.Increment(ref _failed);
                }
            }
        }
        finally
        {
            NativeMemory.AlignedFree(context);
        }

        Armed.IntersectWith(alive);
    }

    /// <summary>
    /// The debug registers of one thread, without suspending it: the kernel applies them in the thread itself, and a
    /// suspended thread that holds a lock the runtime needs would never be resumed (ThreadSnapshot).
    /// </summary>
    private static bool Set(uint id, byte* context)
    {
        nint thread = Kernel32.OpenThread(ThreadAccess, 0, id);

        if (thread == 0)
        {
            return false;
        }

        new Span<byte>(context, ContextBytes).Clear();
        *(uint*)(context + ContextFlagsOffset) = ContextDebugRegisters;
        ulong* registers = (ulong*)(context + Dr0Offset);

        for (int i = 0; i < Slots; i++)
        {
            registers[i] = Addresses[i];
        }

        registers[Slots] = (ulong)(Ring + CellOffset);
        *(ulong*)(context + Dr7Offset) = _dr7;
        bool done = Kernel32.SetThreadContext(thread, context) != 0;
        _ = Kernel32.CloseHandle(thread);

        return done;
    }

    /// <summary>Logs the ring's new hits: the first <see cref="PerSite"/> of each writing instruction in full, then counts.</summary>
    public static void Drain(LoaderLog log)
    {
        if (_dr7 == 0)
        {
            return;
        }

        uint count = Volatile.Read(ref *(uint*)Ring);

        if (count - _seen > RingEntries)
        {
            _seen = count - RingEntries;
        }

        for (; _seen != count; _seen++)
        {
            ulong* entry = (ulong*)(Ring + HeaderBytes + ((_seen % RingEntries) * EntryBytes));
            ulong site = entry[1];
            long hits = Interlocked.Increment(ref _hits);
            long seen = Sites.GetValueOrDefault(site) + 1;

            if (seen == 1 && Sites.Count >= MaxSites)
            {
                continue;
            }

            Sites[site] = seen;

            if (seen <= PerSite)
            {
                log.Warn(Describe(entry, hits));
            }
        }
    }

    /// <summary>Hits so far and the instructions with the most.</summary>
    public static void Summarize(StringBuilder text)
    {
        if (_dr7 == 0)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n      write breakpoint hits so far: {Interlocked.Read(ref _hits)}");

        foreach (var (site, hits) in Sites.OrderByDescending(pair => pair.Value).Take(ShownSites))
        {
            text.Append(CultureInfo.InvariantCulture, $"\n        {hits} before {Where((nint)site)}");
        }
    }

    private static string Describe(ulong* entry, long hits)
    {
        var text = new StringBuilder();
        ulong registers = entry[0] & RegisterMask;
        text.Append(CultureInfo.InvariantCulture, $"[write break] #{hits}: thread {entry[2]} wrote ");

        for (int i = 0; i <= Slots; i++)
        {
            if ((registers & (1UL << i)) == 0)
            {
                continue;
            }

            if (i == Slots)
            {
                text.Append("the self-test cell ");
            }
            else
            {
                text.Append(CultureInfo.InvariantCulture, $"0x{Addresses[i]:x} ");
            }
        }

        text.Append(CultureInfo.InvariantCulture, $"in the instruction before {Where((nint)entry[1])}; code addresses on its stack:");
        var images = Shims.ShimContext.ActiveRuntime?.Images;
        ulong* stack = entry + 4;

        for (int i = 0; i < StackWords; i++)
        {
            if (images?.FindByAddress((nint)stack[i]) is not null)
            {
                text.Append(CultureInfo.InvariantCulture, $" [rsp+0x{i * sizeof(ulong):x}] {images.Describe((nint)stack[i])}");
            }
        }

        text.Append("; stack:");

        for (int i = 0; i < StackWords; i++)
        {
            text.Append(CultureInfo.InvariantCulture, $" {stack[i]:x}");
        }

        return text.ToString();
    }

    private static string Where(nint address)
    {
        var images = Shims.ShimContext.ActiveRuntime?.Images;

        if (images is null)
        {
            return $"0x{address:x}";
        }

        return $"{images.Describe(address)} (0x{address:x})";
    }
}
