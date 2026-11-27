// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;

namespace FiveStar.Loader.Native;

/// <summary>The kernel32 functions the loader calls. Every signature is blittable; strings travel as pointers.</summary>
internal static unsafe class Kernel32
{
    private const string Library = "kernel32";

    [DllImport(Library, SetLastError = true)]
    public static extern nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [DllImport(Library, SetLastError = true)]
    public static extern int VirtualFree(nint address, nuint size, uint freeType);

    [DllImport(Library, SetLastError = true)]
    public static extern int VirtualProtect(nint address, nuint size, uint newProtect, uint* oldProtect);

    [DllImport(Library)]
    public static extern nuint VirtualQueryEx(nint process, nint address, MemoryBasicInformation* info, nuint length);

    [DllImport(Library)]
    public static extern int FlushInstructionCache(nint process, nint address, nuint size);

    [DllImport(Library)]
    public static extern nint GetCurrentProcess();

    [DllImport(Library)]
    public static extern uint GetCurrentThreadId();

    /// <summary>SetProcessInformation(process, PROCESS_INFORMATION_CLASS, info, size): nonzero on success.</summary>
    [DllImport(Library)]
    public static extern int SetProcessInformation(nint process, int informationClass, void* information, uint size);

    [DllImport(Library)]
    public static extern int TerminateProcess(nint process, uint exitCode);

    /// <summary>CreateJobObjectW(attributes, name): the job, or 0.</summary>
    [DllImport(Library)]
    public static extern nint CreateJobObjectW(nint attributes, nint name);

    /// <summary>SetInformationJobObject(job, JOBOBJECTINFOCLASS, info, size): nonzero on success.</summary>
    [DllImport(Library)]
    public static extern int SetInformationJobObject(nint job, int informationClass, void* information, uint size);

    /// <summary>QueryInformationJobObject(job, JOBOBJECTINFOCLASS, info, size, returned): nonzero on success.</summary>
    [DllImport(Library)]
    public static extern int QueryInformationJobObject(nint job, int informationClass, void* information, uint size, uint* returned);

    /// <summary>AssignProcessToJobObject(job, process): nonzero on success.</summary>
    [DllImport(Library)]
    public static extern int AssignProcessToJobObject(nint job, nint process);

    [DllImport(Library)]
    public static extern int SwitchToThread();

    [DllImport(Library)]
    public static extern uint RemoveVectoredExceptionHandler(nint handle);

    [DllImport(Library, SetLastError = true)]
    public static extern nint OpenThread(uint access, int inherit, uint threadId);

    /// <summary>The CPU cycles the thread has run so far; needs THREAD_QUERY_LIMITED_INFORMATION.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern int QueryThreadCycleTime(nint thread, ulong* cycles);

    /// <summary>GetThreadDescription(thread, PWSTR*): an HRESULT; the text is freed with <see cref="LocalFree"/>.</summary>
    [DllImport(Library)]
    public static extern int GetThreadDescription(nint thread, char** description);

    [DllImport(Library)]
    public static extern nint LocalFree(nint memory);

    [DllImport(Library, SetLastError = true)]
    public static extern uint SuspendThread(nint thread);

    [DllImport(Library, SetLastError = true)]
    public static extern uint ResumeThread(nint thread);

    [DllImport(Library)]
    public static extern void OutputDebugStringA(byte* text);

    [DllImport(Library)]
    public static extern void OutputDebugStringW(char* text);

    /// <summary>GetThreadContext: <paramref name="context"/> is a 16-byte aligned CONTEXT with ContextFlags set.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern int GetThreadContext(nint thread, byte* context);

    /// <summary>SetThreadContext: <paramref name="context"/> is a 16-byte aligned CONTEXT; its ContextFlags name the registers set.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern int SetThreadContext(nint thread, byte* context);

    /// <summary>AddVectoredExceptionHandler: <paramref name="handler"/> is a PVECTORED_EXCEPTION_HANDLER.</summary>
    [DllImport(Library)]
    public static extern nint AddVectoredExceptionHandler(uint first, nint handler);

    [DllImport(Library, EntryPoint = "LoadLibraryExW", SetLastError = true)]
    public static extern nint LoadLibraryExW(char* name, nint file, uint flags);

    [DllImport(Library, EntryPoint = "LoadLibraryExA")]
    public static extern nint LoadLibraryExA(byte* name, nint file, uint flags);

    [DllImport(Library, SetLastError = true)]
    public static extern int FreeLibrary(nint module);

    /// <summary>GetProcAddress: <paramref name="name"/> is an ANSI name, or an ordinal in its low word.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern nint GetProcAddress(nint module, byte* name);

    [DllImport(Library)]
    public static extern nint GetModuleHandleW(char* name);

    /// <summary>PowerCreateRequest: <paramref name="context"/> is a REASON_CONTEXT.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern nint PowerCreateRequest(byte* context);

    [DllImport(Library, SetLastError = true)]
    public static extern int PowerSetRequest(nint request, int requestType);

    [DllImport(Library, SetLastError = true)]
    public static extern int PowerClearRequest(nint request, int requestType);

    [DllImport(Library, SetLastError = true)]
    public static extern int CloseHandle(nint handle);

    [DllImport(Library)]
    public static extern nint GetModuleHandleA(byte* name);

    [DllImport(Library)]
    public static extern int GetModuleHandleExW(uint flags, char* name, nint* module);

    [DllImport(Library)]
    public static extern int GetModuleHandleExA(uint flags, byte* name, nint* module);

    [DllImport(Library)]
    public static extern uint GetModuleFileNameW(nint module, char* buffer, uint size);

    [DllImport(Library)]
    public static extern uint GetModuleFileNameA(nint module, byte* buffer, uint size);

    [DllImport(Library)]
    public static extern int WideCharToMultiByte(uint codePage, uint flags, char* source, int sourceLength, byte* destination, int destinationLength, nint defaultChar, nint usedDefaultChar);

    [DllImport(Library)]
    public static extern int K32GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx* counters, uint size);

    [DllImport(Library)]
    public static extern int ReadProcessMemory(nint process, nint address, void* buffer, nuint size, nuint* read);

    /// <summary>GetProcessHeaps: the number of heaps; fills at most <paramref name="count"/> handles.</summary>
    [DllImport(Library)]
    public static extern uint GetProcessHeaps(uint count, nint* heaps);

    [DllImport(Library, SetLastError = true)]
    public static extern nint HeapCreate(uint options, nuint initialSize, nuint maximumSize);

    [DllImport(Library, SetLastError = true)]
    public static extern int HeapDestroy(nint heap);

    [DllImport(Library)]
    public static extern nint GetProcessHeap();

    /// <summary>HeapSummary: <paramref name="summary"/> is a HEAP_SUMMARY with cb set.</summary>
    [DllImport(Library)]
    public static extern int HeapSummary(nint heap, uint flags, HeapSummaryInfo* summary);

    [DllImport(Library)]
    public static extern uint TlsAlloc();

    [DllImport(Library)]
    public static extern int TlsFree(uint index);

    [DllImport(Library)]
    public static extern int TlsSetValue(uint index, nint value);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateFileW(char* name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateFileA(byte* name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateFile2(char* name, uint access, uint share, uint disposition, nint parameters);

    /// <summary>ReadFile with an OVERLAPPED: the read starts at the overlapped offset and signals its event.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern int ReadFile(nint file, byte* buffer, uint bytes, uint* read, FileOverlapped* overlapped);

    [DllImport(Library, SetLastError = true)]
    public static extern int GetOverlappedResult(nint file, FileOverlapped* overlapped, uint* transferred, int wait);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateEventW(nint security, int manualReset, int initialState, char* name);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateEventA(nint security, int manualReset, int initialState, byte* name);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateSemaphoreExW(nint security, int initial, int maximum, char* name, uint flags, uint access);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateEventExW(nint security, char* name, uint flags, uint access);

    [DllImport(Library, SetLastError = true)]
    public static extern nint CreateSemaphoreW(nint security, int initial, int maximum, char* name);

    [DllImport(Library)]
    public static extern uint GetThreadId(nint thread);

    [DllImport(Library)]
    public static extern nint GetCurrentThread();

    [DllImport(Library, SetLastError = true)]
    public static extern int DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, nint* target, uint access, int inherit, uint options);

    [DllImport(Library, SetLastError = true)]
    public static extern int SetEvent(nint handle);

    [DllImport(Library, SetLastError = true)]
    public static extern int ReleaseSemaphore(nint handle, int count, int* previous);

    [DllImport(Library, SetLastError = true)]
    public static extern uint WaitForSingleObjectEx(nint handle, uint milliseconds, int alertable);

    [DllImport(Library, SetLastError = true)]
    public static extern int ResetEvent(nint handle);

    [DllImport(Library, SetLastError = true)]
    public static extern uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    /// <summary>GetCurrentThreadStackLimits: the calling thread's stack, lowest and highest address.</summary>
    [DllImport(Library)]
    public static extern void GetCurrentThreadStackLimits(nuint* low, nuint* high);

    /// <summary>RtlCaptureStackBackTrace: the return addresses of the calling thread, innermost first.</summary>
    [DllImport(Library)]
    public static extern ushort RtlCaptureStackBackTrace(uint skip, uint count, nint* frames, uint* hash);

    /// <summary>GetFileInformationByHandle: <paramref name="information"/> is a BY_HANDLE_FILE_INFORMATION.</summary>
    [DllImport(Library, SetLastError = true)]
    public static extern int GetFileInformationByHandle(nint file, void* information);

    [DllImport(Library, SetLastError = true)]
    public static extern uint GetFileAttributesW(char* name);

    [DllImport(Library, SetLastError = true)]
    public static extern int GetFileAttributesExW(char* name, int level, void* information);
}
