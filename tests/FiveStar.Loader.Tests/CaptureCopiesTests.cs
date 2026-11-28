// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>
/// A capture's texture copy moves the texture out of its state and back: a write state combined with any other bit
/// cannot begin a transition (the desktop Close fails and the game's list never runs), read states may combine.
/// </summary>
public sealed class CaptureCopiesTests
{
    [Theory]
    [InlineData(0x0, true)]
    [InlineData(0x80, true)]
    [InlineData(0xC0, true)]
    [InlineData(0x8, true)]
    [InlineData(0x10, true)]
    [InlineData(0x20 | 0x80, true)]
    [InlineData(0x8 | 0x40, false)]
    [InlineData(0x4 | 0x8, false)]
    [InlineData(0x10 | 0x20, false)]
    public void A_write_state_stands_alone(int state, bool transitionable)
    {
        Assert.Equal(transitionable, CaptureCopies.Transitionable(state));
    }

    /// <summary>A page copy ends at its buffer's end: a copy out of range makes the desktop Close fail.</summary>
    [Theory]
    [InlineData(0x0UL, 0x10000UL, 0x30000UL, 0x10000UL)]
    [InlineData(0x28000UL, 0x10000UL, 0x30000UL, 0x8000UL)]
    [InlineData(0x30000UL, 0x10000UL, 0x30000UL, 0x0UL)]
    [InlineData(0x40000UL, 0x10000UL, 0x30000UL, 0x0UL)]
    public void A_copy_stays_inside_its_buffer(ulong offset, ulong bytes, ulong width, ulong copied)
    {
        Assert.Equal(copied, CaptureCopies.InRange(offset, bytes, width));
    }
}
