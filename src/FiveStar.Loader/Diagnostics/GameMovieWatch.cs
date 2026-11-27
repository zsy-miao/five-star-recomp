// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace FiveStar.Loader.Diagnostics;

/// <summary>
/// <c>--watch-movies</c>: follows the game's Bink movies. Its movie thread (0x143a1a180) walks a list of slot indices
/// (a vector at 0x14ab42e20) into a table of 2368-byte slots (0x149165b98); a slot holds its movie object at +8 and
/// its handle at +1224, the movie object its BINK at +80 and the playback state 0x1402ef0e0 advances it by. The watch
/// logs each movie when its state changes and its frame number every few seconds. It reads game memory only.
/// </summary>
internal sealed unsafe class GameMovieWatch
{
    /// <summary>Game globals (1.0.2.1).</summary>
    private const ulong SlotTableAddress = 0x149165B98;
    private const ulong ActiveListAddress = 0x14AB42E20;
    private const int SlotSize = 2368;
    private const int SlotMovieOffset = 8;
    private const int SlotHandleOffset = 1224;

    /// <summary>The movie object: its BINK, state (9 plays), flags and frame targets.</summary>
    private const int MovieBinkOffset = 80;
    private const int MovieStoppedOffset = 408;
    private const int MovieStateOffset = 452;
    private const int MovieLoopOffset = 485;
    private const int MovieGotoOffset = 488;
    private const int MovieSyncedOffset = 492;
    private const int MovieSyncFrameOffset = 496;
    private const int MovieEndFrameOffset = 520;
    private const int MovieSize = 528;

    /// <summary>BINK (bink.h): Width, Height, Frames, FrameNum, LastFrameNum, FrameRate, FrameRateDiv.</summary>
    private const int BinkHeightOffset = 4;
    private const int BinkFramesOffset = 8;
    private const int BinkFrameOffset = 12;
    private const int BinkLastFrameOffset = 16;
    private const int BinkRateOffset = 20;
    private const int BinkRateDivOffset = 24;
    private const int BinkHeaderSize = 28;

    private const int MaxSlots = 64;
    private const int PollMilliseconds = 250;
    private const double FrameLineSeconds = 5;
    private const string Prefix = "  [movies] ";

    private readonly LoaderLog _log;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<uint, string> _states = [];
    private readonly Dictionary<uint, uint> _frames = [];

    private GameMovieWatch(LoaderLog log)
    {
        _log = log;
    }

    /// <summary>Starts the watch thread.</summary>
    public static void Start(LoaderLog log)
    {
        var watch = new GameMovieWatch(log);
        var thread = new Thread(watch.Run) { IsBackground = true, Name = "fivestar movie watch" };
        thread.Start();
        log.Info($"{Prefix}watching the game's Bink movies");
    }

    private void Run()
    {
        double framesAt = 0;
        var seen = new HashSet<uint>();

        while (true)
        {
            Thread.Sleep(PollMilliseconds);
            double now = _clock.Elapsed.TotalSeconds;
            bool frameLine = now - framesAt >= FrameLineSeconds;
            seen.Clear();

            if (!GameHangWatch.IsReadable(ActiveListAddress))
            {
                continue;
            }

            uint* first = *(uint**)ActiveListAddress;
            uint* end = *(uint**)(ActiveListAddress + (ulong)sizeof(ulong));
            long count = end - first;

            if (first == null || count <= 0 || count > MaxSlots || !GameHangWatch.IsReadable((ulong)first) || !GameHangWatch.IsReadable((ulong)(end - 1)))
            {
                count = 0;
            }

            for (int i = 0; i < count; i++)
            {
                uint slot = first[i];

                if (slot < MaxSlots && seen.Add(slot))
                {
                    Poll(slot, now, frameLine);
                }
            }

            foreach (uint gone in _states.Keys.Where(slot => !seen.Contains(slot)).ToArray())
            {
                _log.Info(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{now:F1} s: slot {gone} left the list at frame {_frames.GetValueOrDefault(gone)}"));
                _states.Remove(gone);
                _frames.Remove(gone);
            }

            if (frameLine)
            {
                framesAt = now;
            }
        }
    }

    /// <summary>Logs one slot's movie when its state changed, or its frame moved and a frame line is due.</summary>
    private void Poll(uint slot, double now, bool frameLine)
    {
        ulong entry = SlotTableAddress + ((ulong)slot * SlotSize);
        ulong movie = *(ulong*)(entry + SlotMovieOffset);
        uint handle = *(uint*)(entry + SlotHandleOffset);

        if (!GameHangWatch.IsReadable(movie) || !GameHangWatch.IsReadable(movie + MovieSize))
        {
            Note(slot, now, $"handle {handle}, no movie object", 0, frameLine);

            return;
        }

        ulong bink = *(ulong*)(movie + MovieBinkOffset);
        var state = new StringBuilder();
        state.Append(CultureInfo.InvariantCulture, $"handle {handle}, state {*(byte*)(movie + MovieStateOffset)}, stopped {*(byte*)(movie + MovieStoppedOffset)}, loop {*(byte*)(movie + MovieLoopOffset)}, synced {*(byte*)(movie + MovieSyncedOffset)}, end frame {*(uint*)(movie + MovieEndFrameOffset)}");
        uint frame = 0;
        string position = "no BINK";

        if (GameHangWatch.IsReadable(bink) && GameHangWatch.IsReadable(bink + BinkHeaderSize))
        {
            frame = *(uint*)(bink + BinkFrameOffset);
            state.Append(CultureInfo.InvariantCulture, $", {*(uint*)bink}x{*(uint*)(bink + BinkHeightOffset)}, {*(uint*)(bink + BinkFramesOffset)} frames at {*(uint*)(bink + BinkRateOffset)}/{*(uint*)(bink + BinkRateDivOffset)}");
            position = string.Create(CultureInfo.InvariantCulture, $"frame {frame} (last decoded {*(uint*)(bink + BinkLastFrameOffset)}), sync frame {*(uint*)(movie + MovieSyncFrameOffset)}, goto {*(uint*)(movie + MovieGotoOffset)}");
        }

        Note(slot, now, state.ToString(), frame, frameLine, position);
    }

    private void Note(uint slot, double now, string state, uint frame, bool frameLine, string position = "")
    {
        bool known = _states.TryGetValue(slot, out string? last);
        bool changed = !known || last != state;
        bool moved = frameLine && _frames.GetValueOrDefault(slot) != frame;

        if (changed)
        {
            _states[slot] = state;
        }

        if (changed || moved)
        {
            _frames[slot] = frame;
            _log.Info(string.Create(CultureInfo.InvariantCulture, $"{Prefix}{now:F1} s: slot {slot} {state}; {position}"));
        }
    }
}
