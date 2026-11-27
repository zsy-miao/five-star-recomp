// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using FiveStar.Loader.Codegen;
using FiveStar.Loader.Cpu;
using FiveStar.Loader.Loading;
using FiveStar.Loader.Native;
using FiveStar.Loader.Shims;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// A first vectored exception handler that reports hardware faults (access violations, illegal instructions,
/// breakpoints, ...) whose stack passes through a mapped image. It only reports: every exception continues to the
/// image's own SEH and C++ handlers, so a fault the game handles itself shows up in the log and nothing else changes.
/// </summary>
internal sealed unsafe class FaultReporter
{
    /// <summary>EXCEPTION_CONTINUE_SEARCH (excpt.h).</summary>
    private const int ContinueSearch = 0;

    /// <summary>EXCEPTION_CONTINUE_EXECUTION (excpt.h): resume the thread with its (changed) CONTEXT.</summary>
    private const int ContinueExecution = -1;

    /// <summary>AddVectoredExceptionHandler's First argument: call this handler before the others, or after them.</summary>
    private const uint CallFirst = 1;
    private const uint CallLast = 0;

    /// <summary>NTSTATUS severity bits 30..31 = 3: an error, the class of hardware faults (ntstatus.h).</summary>
    private const uint ErrorSeverity = 0xC0000000;

    /// <summary>NTSTATUS bit 29: a customer code, such as a C++ (0xE06D7363) or CLR exception (winnt.h).</summary>
    private const uint CustomerBit = 0x20000000;

    /// <summary>STATUS_BREAKPOINT (ntstatus.h): int3 and __debugbreak.</summary>
    private const uint StatusBreakpoint = 0x80000003;

    /// <summary>Frames searched for a mapped image before a fault counts as foreign (runtime-internal).</summary>
    private const int MappedFrameSearchDepth = 12;

    /// <summary>Faults reported with registers, stack and calls; later ones get one line each.</summary>
    private const int FullReports = 3;

    /// <summary>Faults reported at all, but for the first at each new address (<see cref="MaxSites"/>).</summary>
    private const int MaxReports = 64;

    /// <summary>Fault addresses remembered: a fault at one not seen before is reported past <see cref="MaxReports"/>.</summary>
    private const int MaxSites = 1024;

    /// <summary>EXCEPTION_RECORD.ExceptionAddress (winnt.h).</summary>
    private const int AddressOffset = 0x10;

    /// <summary>Whether this thread is writing a report: a fault in the report is not reported again.</summary>
    [ThreadStatic]
    private static bool t_reporting;

    /// <summary><c>mov rax,gs:[58h]; test rax,rax</c>: the thread's implicit-TLS array (TEB.ThreadLocalStoragePointer).</summary>
    private static readonly byte[] TestTlsArray = [0x65, 0x48, 0x8B, 0x04, 0x25, ThreadEnvironmentBlock.ThreadLocalStoragePointer, 0, 0, 0, 0x48, 0x85, 0xC0];

    private readonly ImageTable _images;
    private readonly FaultRecordWriter _writer;
    private readonly CodeHeap _heap;
    private readonly LoaderLog _log;
    private readonly AmdInstructionHandler _cpu;
    private readonly object _registration = new();
    private nint _entry;
    private nint _backstopEntry;
    private nint _backstop;
    private int _reported;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<nint, byte> _sites = new();
    private nint _handle;

    public FaultReporter(ImageTable images, CallTrace trace, CodeHeap heap, AmdInstructionHandler cpu, LoaderLog log)
    {
        _cpu = cpu;
        _images = images;
        _writer = new FaultRecordWriter(images, trace, log);
        _heap = heap;
        _log = log;
    }

    /// <summary>Faults reported so far.</summary>
    public int Reported => Volatile.Read(ref _reported);

    /// <summary>
    /// Registers the handler first, and a second handler last that only emulates the AMD-only instructions (the
    /// backstop); once per process. A handler a game module adds first (Bink at every video) runs before ours until
    /// <see cref="KeepFirst"/> moves ours back; a fault whose dispatch passed the head of the list before ours went back
    /// there, and reached our old registration only after it left, met no emulation (0xC000001D at a CLWB, then at a
    /// CLZERO 18 minutes into a boot, issue #13). The backstop is never removed, so every handler that continues the
    /// search leads to it, whatever the order at the head.
    /// </summary>
    public void Install()
    {
        if (_handle != 0)
        {
            return;
        }

        _entry = EmitEntry(&OnException);
        _backstopEntry = EmitEntry(&OnEmulate);
        _heap.Seal();
        _handle = Kernel32.AddVectoredExceptionHandler(CallFirst, _entry);
        _backstop = Kernel32.AddVectoredExceptionHandler(CallLast, _backstopEntry);
        _log.Info("fault reporter: vectored exception handler installed, with the AMD-only instruction emulation last as well");
    }

    /// <summary>
    /// Moves the handler back to the front after mapped code added its own first handler (the game's crash handler
    /// would otherwise end the process before the fault is reported). The new registration goes in before the old one
    /// leaves: Bink adds a first handler at every video, and an AMD-only instruction that faulted on another thread in
    /// between found no emulation and ended the process (0xC000001D at a CLWB the handler had emulated before).
    /// </summary>
    public void KeepFirst()
    {
        if (_handle == 0)
        {
            return;
        }

        lock (_registration)
        {
            nint previous = _handle;
            _handle = Kernel32.AddVectoredExceptionHandler(CallFirst, _entry);
            _ = Kernel32.RemoveVectoredExceptionHandler(previous);
        }
    }

    /// <summary>Reports a suspended thread's CONTEXT like a fault: where it is, its stack and the last calls.</summary>
    public void WriteThreadState(string title, uint threadId, byte* context)
    {
        _writer.WriteThreadState(title, threadId, context);
    }

    public void WriteRecentCalls(string title)
    {
        _writer.WriteRecentCalls(title);
    }

    /// <summary>
    /// The handler's native entry: <see cref="OnException"/>, except on a thread whose TLS array is gone, and for a
    /// fault in managed code (<see cref="FaultEntryCode"/>). A thread that faults while it exits (after ntdll's
    /// LdrpFreeTls, as on a corrupted heap) cannot enter managed code: the runtime finds its thread through that array,
    /// and the entry would fault again inside coreclr.
    /// </summary>
    private nint EmitEntry(delegate* unmanaged<nint, int> handler)
    {
        var code = new X64Emitter();

        // A write breakpoint's single step is noted and continued here, before any managed code (WriteBreakpoints).
        code.Data(WriteBreakpoints.EntryCode());
        code.Data(TestTlsArray);
        int toNoThread = code.JzForward();
        code.Data(FaultEntryCode.Body((nint)handler, PcToModule(), ModuleBase("coreclr.dll"), ModuleBase("clrjit.dll"), _images.Ranges));

        code.PatchJumpHere(toNoThread);
        code.MovEax((uint)ContinueSearch);
        code.Ret();

        return _heap.Write(code.ToArray());
    }

    /// <summary>ntdll's RtlPcToFileHeader.</summary>
    private static nint PcToModule()
    {
        return NativeLibrary.GetExport(NativeLibrary.Load("ntdll.dll"), "RtlPcToFileHeader");
    }

    /// <summary>
    /// The base of a loaded module, 0 when it is not loaded. GetModuleHandleW never loads: a bare name given to a load
    /// would search the current folder and PATH, where a planted coreclr.dll or clrjit.dll (absent from a NativeAOT
    /// build) would run.
    /// </summary>
    private static nint ModuleBase(string name)
    {
        fixed (char* text = name)
        {
            return Kernel32.GetModuleHandleW(text);
        }
    }

    [UnmanagedCallersOnly]
    private static int OnException(nint pointers)
    {
        try
        {
            return ShimContext.Runtime.Faults.Handle(pointers);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "FaultReporter.OnException");

            return default;
        }
    }

    /// <summary>The backstop: an AMD-only instruction's fault is performed and the thread goes on; anything else searches on.</summary>
    [UnmanagedCallersOnly]
    private static int OnEmulate(nint pointers)
    {
        try
        {
            var runtime = ShimContext.ActiveRuntime;

            if (runtime is null)
            {
                return ContinueSearch;
            }

            return runtime.Faults.EmulateOnly(pointers);
        }
        catch (Exception exception)
        {
            UnmanagedGuard.Caught(exception, "FaultReporter.OnEmulate");

            return ContinueSearch;
        }
    }

    /// <summary>What the backstop answers for a fault: continue execution when it was an AMD-only instruction now performed.</summary>
    internal int EmulateOnly(nint pointers)
    {
        if (_cpu.TryEmulate(pointers))
        {
            return ContinueExecution;
        }

        return ContinueSearch;
    }

    private static bool IsFault(uint code)
    {
        if ((code & CustomerBit) != 0)
        {
            return false;
        }

        return code == StatusBreakpoint || (code & ErrorSeverity) == ErrorSeverity;
    }

    /// <summary>EXCEPTION_POINTERS: ExceptionRecord at +0, ContextRecord at +8 (winnt.h). AMD-only instructions are emulated first.</summary>
    private int Handle(nint pointers)
    {
        if (_cpu.TryEmulate(pointers))
        {
            return ContinueExecution;
        }

        byte* record = *(byte**)pointers;
        byte* context = *(byte**)(pointers + sizeof(nint));

        // Another thread's fault is reported meanwhile; only one inside this thread's report is not (it would recur).
        if (!IsFault(*(uint*)record) || t_reporting)
        {
            return ContinueSearch;
        }

        t_reporting = true;

        try
        {
            Report(record, context);

            // The game ends the process at its own int3 after it sees the loss: the crash dump comes first.
            Shims.D3D12X.DebugLayer.AftermathCrashDumps.Active?.BeforeFaultEnds();
        }
        finally
        {
            t_reporting = false;
        }

        return ContinueSearch;
    }

    private void Report(byte* record, byte* context)
    {
        var frames = NativeStack.WalkFrom(_images, context, MappedFrameSearchDepth);
        bool mapped = frames.Any(f => _images.FindByAddress((nint)f.Pc) is not null);

        if (!mapped)
        {
            return;
        }

        int number = Interlocked.Increment(ref _reported);
        nint site = *(nint*)(record + AddressOffset);
        bool newSite = _sites.Count < MaxSites && _sites.TryAdd(site, 0);

        // Past the quota a fault at a new address still gets its line: the boot's last fault is often one.
        if (number > MaxReports && !newSite)
        {
            return;
        }

        if (number > FullReports)
        {
            _writer.WriteBrief(number, record);

            return;
        }

        _writer.WriteFull(number, record, context);
    }
}
