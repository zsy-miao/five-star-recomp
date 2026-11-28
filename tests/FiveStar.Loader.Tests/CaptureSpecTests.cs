// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using FiveStar.Loader.Shims.D3D12X.DebugLayer;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>`--capture-dispatch` entries: an occurrence counted from boot, or from a present on (`@pN[:n]`).</summary>
public sealed class CaptureSpecTests
{
    [Fact]
    public void Entries_count_from_boot_or_from_a_present()
    {
        var specs = DispatchCaptures.Parse("CS_A,CS_B@7,CS_C@p3200,CS_D@p3200:4");

        Assert.Equal(
            [new CaptureSpec("CS_A", 1, 0), new CaptureSpec("CS_B", 7, 0), new CaptureSpec("CS_C", 1, 3200), new CaptureSpec("CS_D", 4, 3200)],
            specs);
    }
}
