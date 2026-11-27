// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

namespace FiveStar.Loader.Shims.D3D12X.Present;

/// <summary>
/// When the back buffers may be resized again after IDXGISwapChain::ResizeBuffers failed. A failed resize left the
/// swap chain without buffers of ours, the next frame was recorded against null targets, and D3D12 removed the device
/// at its ExecuteCommandLists (DXGI_ERROR_INVALID_CALL: issue #64, where the window was being closed and ResizeBuffers
/// returned E_ACCESSDENIED). Now the frame of a failed resize is not shown, the old buffers are taken again, and the
/// resize is tried again <see cref="RetryFrames"/> frames later, each wait twice the last up to
/// <see cref="LongestWait"/>; frames in between are shown at the old size.
/// </summary>
internal struct ResizeRetry
{
    /// <summary>Frames before the first retry, and the longest wait between two.</summary>
    public const long RetryFrames = 30;
    public const long LongestWait = 960;

    private const int Doubling = 2;

    private long _notBefore;
    private long _wait;

    /// <summary>Resizes that failed so far.</summary>
    public long Failures { get; private set; }

    /// <summary>Whether a resize may be tried at <paramref name="frame"/>.</summary>
    public readonly bool MayTry(long frame)
    {
        return frame >= _notBefore;
    }

    /// <summary>A resize failed at <paramref name="frame"/>; true the first time since the last success (to log it once).</summary>
    public bool Failed(long frame)
    {
        bool first = _wait == 0;
        _wait = first ? RetryFrames : Math.Min(LongestWait, _wait * Doubling);
        _notBefore = frame + _wait;
        Failures++;

        return first;
    }

    /// <summary>A resize worked: the next one is tried at once.</summary>
    public void Succeeded()
    {
        _wait = 0;
        _notBefore = 0;
    }
}
