// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Diagnostics;
using FiveStar.Loader.Native;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>The write breakpoints' handler code, run on a made-up exception.</summary>
public sealed unsafe class WriteBreakpointsTests
{
    private const int ContextBytes = 0x4D0;
    private const int Dr6 = 0x68;
    private const int Rsp = 0x98;
    private const int Rip = 0xF8;
    private const uint SingleStep = 0x80000004;
    private const uint AccessViolation = 0xC0000005;
    private const int FellThrough = 7;

    [Fact]
    public void A_breakpoint_single_step_is_noted_and_continued_and_other_exceptions_fall_through()
    {
        // The entry, then what stands for the rest of the handler: mov eax,7; ret.
        byte[] code = [.. WriteBreakpoints.EntryCode(), 0xB8, FellThrough, 0, 0, 0, 0xC3];
        nint memory = Kernel32.VirtualAlloc(0, (nuint)code.Length, Win32Constants.MemReserveCommit, Win32Constants.PageExecuteReadWrite);
        Marshal.Copy(code, 0, memory, code.Length);
        var entry = (delegate* unmanaged<void**, int>)memory;

        ulong* stack = stackalloc ulong[16];
        stack[0] = 0x1111;
        stack[11] = 0x2222;

        byte* context = (byte*)NativeMemory.AllocZeroed(ContextBytes);
        uint* record = stackalloc uint[8];
        void** pointers = stackalloc void*[2];
        pointers[0] = record;
        pointers[1] = context;

        *(ulong*)(context + Dr6) = 0x4;
        *(ulong*)(context + Rsp) = (ulong)stack;
        *(ulong*)(context + Rip) = 0x140001234;

        uint before = *(uint*)WriteBreakpoints.RingStart;
        record[0] = AccessViolation;
        int fault = entry(pointers);
        record[0] = SingleStep;
        int step = entry(pointers);
        ulong* noted = (ulong*)(WriteBreakpoints.RingStart + 64 + ((before % 256) * 128));

        Assert.Equal(FellThrough, fault);
        Assert.Equal(-1, step);
        Assert.Equal(before + 1, *(uint*)WriteBreakpoints.RingStart);

        Assert.Equal(0x4UL, noted[0]);
        Assert.Equal(0x140001234UL, noted[1]);
        Assert.Equal((ulong)Kernel32.GetCurrentThreadId(), noted[2]);
        Assert.Equal((ulong)stack, noted[3]);

        Assert.Equal(0x1111UL, noted[4]);
        Assert.Equal(0x2222UL, noted[15]);
        Assert.Equal(0UL, *(ulong*)(context + Dr6));

        // A single step with no breakpoint in DR6 (cleared above) is someone else's.
        Assert.Equal(FellThrough, entry(pointers));

        NativeMemory.Free(context);
        _ = Kernel32.VirtualFree(memory, 0, Win32Constants.MemRelease);
    }

    [Fact]
    public void An_armed_thread_that_writes_the_cell_is_logged_and_goes_on()
    {
        // The handler stays registered: the test host's threads keep the breakpoint on the cell.
        byte[] code = [.. WriteBreakpoints.EntryCode(), 0x31, 0xC0, 0xC3];
        nint memory = Kernel32.VirtualAlloc(0, (nuint)code.Length, Win32Constants.MemReserveCommit, Win32Constants.PageExecuteReadWrite);
        Marshal.Copy(code, 0, memory, code.Length);
        _ = Kernel32.AddVectoredExceptionHandler(1, memory);
        var text = new StringWriter();
        var log = new LoaderLog(text, null, false);

        WriteBreakpoints.Arm([], log);
        WriteBreakpoints.Drain(log);

        Assert.Contains("wrote the self-test cell", text.ToString(), StringComparison.Ordinal);
    }
}
