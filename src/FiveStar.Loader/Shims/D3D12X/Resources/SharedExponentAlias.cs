// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;

namespace FiveStar.Loader.Shims.D3D12X.Resources;

/// <summary>
/// An R32_UINT texture the GPU writes shared-exponent light into and its R9G9B9E5 shadow, which the game's R9G9B9E5 views
/// of that memory read (<see cref="SharedExponentAliases"/>): the shadow, the buffer the texels pass through and both
/// textures' footprints in it, and the lists whose copy is still current.
/// </summary>
internal sealed class SharedExponentAlias(GpuTexture source, nint shadow, nint buffer, CopyFootprint[] written, CopyFootprint[] read)
{
    private readonly ConcurrentDictionary<nint, byte> _current = new();

    public GpuTexture Source { get; } = source;

    /// <summary>The R9G9B9E5 texture (one reference owned here); it rests in COMMON between lists.</summary>
    public nint Shadow { get; } = shadow;

    /// <summary>The buffer both copies go through (owned here); it rests in COMMON.</summary>
    public nint Buffer { get; } = buffer;

    /// <summary>Every level of every layer as the source lays it out in <see cref="Buffer"/>.</summary>
    public CopyFootprint[] Written { get; } = written;

    /// <summary>The same parts as the shadow lays them out (the same bytes: the texels are as wide).</summary>
    public CopyFootprint[] Read { get; } = read;

    private long _copies;
    private long _refused;

    /// <summary>Copies recorded so far.</summary>
    public long Copies => Interlocked.Read(ref _copies);

    /// <summary>Copies not recorded: a state involved was one the list's type cannot name.</summary>
    public long Refused => Interlocked.Read(ref _refused);

    /// <summary>Counts a copy that was <paramref name="recorded"/> or refused; how many of either so far.</summary>
    public long Count(bool recorded)
    {
        if (recorded)
        {
            return Interlocked.Increment(ref _copies) + Refused;
        }

        return Interlocked.Increment(ref _refused) + Copies;
    }

    /// <summary>Whether <paramref name="list"/> copied the source since it last used it otherwise.</summary>
    public bool CurrentOn(nint list)
    {
        return _current.ContainsKey(list);
    }

    public void Copied(nint list)
    {
        _current[list] = 0;
    }

    /// <summary><paramref name="list"/> used the source (a barrier on it): its next read through the shadow copies again.</summary>
    public void Used(nint list)
    {
        _current.TryRemove(list, out _);
    }
}
