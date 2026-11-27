// Copyright (c) 2026 Five Star Recomp contributors.
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using FiveStar.Loader.Shims.D3D12X;
using FiveStar.Loader.Shims.D3D12X.Present;
using FiveStar.Loader.Tests.Support;
using Xunit;

namespace FiveStar.Loader.Tests;

/// <summary>Queue slot 32 (PresentX) and device slots 78 and 80 (frame interval, frame-event wait), called as the game calls them.</summary>
public sealed unsafe class D3D12XPresentTests
{
    private const int PresentSlot = 32;
    private const int SetFrameIntervalSlot = 78;
    private const int ScheduleFrameEventSlot = 79;
    private const int WaitFrameEventSlot = 80;
    private const int CreateCommittedResourceSlot = 27;
    private const int GetDeviceRemovedReasonSlot = 37;
    private const int DefaultHeap = 1;
    private const int Texture2D = 3;
    private const int Rgba8Unorm = 28;
    private const int AllowRenderTarget = 1;
    private const int RenderTargetState = 4;
    private const uint PopupStyle = 0x80000000;
    private const uint NoMoveNoZOrder = 0x0002 | 0x0004;
    private const uint Infinite = 0xFFFFFFFF;
    private static readonly Guid ResourceIid = new("696442be-a72e-4059-bc79-5b5c98040fad");

    [Fact]
    public void Present_copies_a_render_target_into_a_swap_chain_on_a_hidden_window()
    {
        nint window = CreateHiddenWindow();

        try
        {
            using var test = TestRuntime.Create();
            var device = new XboxDevice(test);
            nint queue = device.Queue();
            nint texture = RenderTarget(device, 64, 32);
            test.Runtime.D3D12X.Presenter.Window = window;

            nint* resources = stackalloc nint[] { texture };
            var rect = new PlaneRect { Right = 64, Bottom = 32 };
            float* destination = stackalloc float[] { 0, 0, 1, 1 };
            var plane = new PresentPlane { Token = 1, ResourceCount = 1, Resources = resources, SourceRect = &rect, DestinationRect = destination };
            uint* parameters = stackalloc uint[8];
            var present = (delegate* unmanaged<nint, uint, PresentPlane*, uint*, int>)XboxDevice.Vtable(queue)[PresentSlot];

            int first = present(queue, 1, &plane, parameters);
            int second = present(queue, 1, &plane, parameters);
            int shutdown = present(queue, 0, null, null);

            Assert.Equal(0, first);
            Assert.Equal(0, second);
            Assert.Equal(0, shutdown);
            Assert.Equal(2, test.Runtime.D3D12X.Presenter.Presents);
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    [Fact]
    public void Present_scales_the_plane_into_a_window_of_another_size_and_follows_its_resizes()
    {
        nint window = CreateHiddenWindow(100, 80);
        var log = new StringWriter();

        try
        {
            using var test = TestRuntime.Create(log: log);
            var device = new XboxDevice(test);
            nint queue = device.Queue();
            nint texture = RenderTarget(device, 64, 32);
            test.Runtime.D3D12X.Presenter.Window = window;

            nint* resources = stackalloc nint[] { texture };
            var rect = new PlaneRect { Right = 64, Bottom = 32 };
            float* destination = stackalloc float[] { 0, 0, 1, 1 };
            var plane = new PresentPlane { Token = 1, ResourceCount = 1, Resources = resources, SourceRect = &rect, DestinationRect = destination };
            uint* parameters = stackalloc uint[8];
            var present = (delegate* unmanaged<nint, uint, PresentPlane*, uint*, int>)XboxDevice.Vtable(queue)[PresentSlot];

            Assert.Equal(0, present(queue, 1, &plane, parameters));
            Assert.True(SetWindowPos(window, 0, 0, 0, 40, 90, NoMoveNoZOrder) != 0);
            Assert.Equal(0, present(queue, 1, &plane, parameters));
            Assert.Equal(2, test.Runtime.D3D12X.Presenter.Presents);

            string text = log.ToString();
            Assert.Contains("100x80 (plane 64x32)", text);
            Assert.Contains("back buffers now 40x90", text);
            Assert.DoesNotContain("[present] no scal", text);
        }
        finally
        {
            _ = DestroyWindow(window);
        }
    }

    /// <summary>
    /// Issue #64: the window was closed while the game presented. ResizeBuffers on the destroyed window returned
    /// E_ACCESSDENIED, the next frame was recorded against no back buffers and D3D12 removed the device at its
    /// ExecuteCommandLists (DXGI_ERROR_INVALID_CALL). Now nothing is shown once the window is gone, and the device stays.
    /// </summary>
    [Fact]
    public void Presents_after_the_window_was_destroyed_show_nothing_and_keep_the_device()
    {
        nint window = CreateHiddenWindow(100, 80);
        var log = new StringWriter();
        using var test = TestRuntime.Create(log: log);

        var device = new XboxDevice(test);
        nint queue = device.Queue();
        nint texture = RenderTarget(device, 64, 32);
        test.Runtime.D3D12X.Presenter.Window = window;

        nint* resources = stackalloc nint[] { texture };
        var rect = new PlaneRect { Right = 64, Bottom = 32 };
        float* destination = stackalloc float[] { 0, 0, 1, 1 };
        var plane = new PresentPlane { Token = 1, ResourceCount = 1, Resources = resources, SourceRect = &rect, DestinationRect = destination };
        uint* parameters = stackalloc uint[8];
        var present = (delegate* unmanaged<nint, uint, PresentPlane*, uint*, int>)XboxDevice.Vtable(queue)[PresentSlot];

        Assert.Equal(0, present(queue, 1, &plane, parameters));
        Assert.True(DestroyWindow(window) != 0);
        Assert.Equal(0, present(queue, 1, &plane, parameters));
        Assert.Equal(0, present(queue, 1, &plane, parameters));

        Assert.Equal(1, test.Runtime.D3D12X.Presenter.Presents);
        nint desktop = test.Runtime.D3D12X.Objects.DesktopDevice;
        Assert.Equal(0, ((delegate* unmanaged<nint, int>)(*(nint**)desktop)[GetDeviceRemovedReasonSlot])(desktop));
        Assert.Contains("is gone (closed): frames are not shown", log.ToString());
        Assert.DoesNotContain("ResizeBuffers", log.ToString());
    }

    /// <summary>A failed resize is tried again 30 frames later, then at twice the wait each time up to 960; a success ends the waits.</summary>
    [Fact]
    public void A_failed_resize_is_retried_later_and_later()
    {
        var retry = default(ResizeRetry);
        Assert.True(retry.MayTry(100));

        Assert.True(retry.Failed(100));
        Assert.Equal((false, true), (retry.MayTry(129), retry.MayTry(130)));
        Assert.False(retry.Failed(130));
        Assert.Equal((false, true), (retry.MayTry(189), retry.MayTry(190)));

        for (int i = 0; i < 10; i++)
        {
            retry.Failed(1000);
        }

        Assert.Equal((false, true, 12L), (retry.MayTry(1000 + ResizeRetry.LongestWait - 1), retry.MayTry(1000 + ResizeRetry.LongestWait), retry.Failures));

        retry.Succeeded();
        Assert.True(retry.MayTry(1001));
        Assert.True(retry.Failed(1001));
    }

    [Fact]
    public void A_window_larger_than_the_work_area_is_fitted_at_the_planes_aspect_ratio()
    {
        // The game's 3840x2160 window (frame included) on a 4K monitor whose taskbar leaves 2088 rows.
        var outer = new PlaneRect { Left = 26, Top = 26, Right = 3866, Bottom = 2186 };
        var client = new PlaneRect { Right = 3814, Bottom = 2113 };
        var work = new PlaneRect { Right = 3840, Bottom = 2088 };

        var placed = GameWindowFit.Place(outer, client, work, 3840, 2160);

        Assert.NotNull(placed);
        var (x, y, width, height) = placed.Value;
        Assert.True(width <= 3840 && height <= 2088);
        Assert.Equal(16.0 / 9.0, (width - 26) / (double)(height - 47), 2);
        Assert.InRange(3840 - (x + width) - x, -1, 1);
        Assert.Null(GameWindowFit.Place(new PlaneRect { Right = 1000, Bottom = 600 }, client, work, 3840, 2160));
    }

    [Fact]
    public void Frame_event_waits_return_successive_tokens_one_interval_apart()
    {
        using var test = TestRuntime.Create();
        var device = new XboxDevice(test);
        var setInterval = (delegate* unmanaged<nint, nint, uint, uint, uint, int>)XboxDevice.Vtable(device.Device)[SetFrameIntervalSlot];
        var wait = (delegate* unmanaged<nint, uint, uint, nint, uint, ulong*, int>)XboxDevice.Vtable(device.Device)[WaitFrameEventSlot];
        // 50 ms, so a busy machine rarely misses an origin between the two waits; one missed origin is still allowed.
        const uint IntervalMicroseconds = 50000;
        Assert.Equal(0, setInterval(device.Device, 0, IntervalMicroseconds, 3, 0));

        ulong current = 0;
        ulong next = 0;
        ulong after = 0;

        // No wait (flags 2, timeout 0) for the current token, then two blocking waits for the next origins.
        Assert.Equal(0, wait(device.Device, 0, 0, 0, 2, &current));
        Assert.Equal(0, wait(device.Device, 0, Infinite, 0, 0, &next));
        long start = Stopwatch.GetTimestamp();
        Assert.Equal(0, wait(device.Device, 0, Infinite, 0, 0, &after));
        var waited = Stopwatch.GetElapsedTime(start);

        Assert.True(current >= 1);
        Assert.InRange(after, next + 1, next + 2);
        Assert.InRange(waited.TotalMilliseconds, 1, 3 * IntervalMicroseconds / 1000.0);
    }

    [Fact]
    public void The_render_threads_loop_presents_once_per_interval()
    {
        using var test = TestRuntime.Create();
        var device = new XboxDevice(test);
        var setInterval = (delegate* unmanaged<nint, nint, uint, uint, uint, int>)XboxDevice.Vtable(device.Device)[SetFrameIntervalSlot];

        var schedule = (delegate* unmanaged<nint, uint, uint, nint, uint, int>)XboxDevice.Vtable(device.Device)[ScheduleFrameEventSlot];
        var wait = (delegate* unmanaged<nint, uint, uint, nint, uint, ulong*, int>)XboxDevice.Vtable(device.Device)[WaitFrameEventSlot];
        const uint IntervalMicroseconds = 20000;
        const int Frames = 6;

        // As at device creation (0x142478de4): event 2 at the origin. Waiting for an unscheduled event is refused.
        Assert.Equal(unchecked((int)0x80070057), wait(device.Device, 1, 0, 0, 0, null));
        Assert.Equal(0, schedule(device.Device, 2, 0, 0, 0));
        Assert.Equal(0, setInterval(device.Device, 0, IntervalMicroseconds, 3, 0));

        // As at 0x142482448 and 0x142482405: take the current token, wait for event 2 of the previous frame, present.
        ulong previous = 0;
        ulong first = 0;
        ulong token = 0;
        long start = Stopwatch.GetTimestamp();

        for (int frame = 0; frame < Frames; frame++)
        {
            token = 0;
            Assert.Equal(0, wait(device.Device, 0, 0, 0, 2, &token));
            Assert.Equal(0, wait(device.Device, 2, Infinite, 0, 0, &previous));
            test.Runtime.D3D12X.Pacer.Presented(token);
            first = frame == 0 ? token : first;
            previous = token;
        }

        var elapsed = Stopwatch.GetElapsedTime(start);

        Assert.Equal(first + Frames - 1, token);
        Assert.True(elapsed.TotalMilliseconds >= (Frames - 3) * IntervalMicroseconds / 1000.0, $"{Frames} frames in {elapsed.TotalMilliseconds:F1} ms");
    }

    private static nint RenderTarget(XboxDevice device, ulong width, uint height)
    {
        int* properties = stackalloc int[] { DefaultHeap, 0, 0, 0, 0 };
        var desc = new ResourceDescription
        {
            Dimension = Texture2D,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Rgba8Unorm,
            SampleCount = 1,
            Flags = AllowRenderTarget,
        };
        Guid iid = ResourceIid;
        nint resource = 0;
        var create = (delegate* unmanaged<nint, int*, int, ResourceDescription*, int, void*, Guid*, nint*, int>)XboxDevice.Vtable(device.Device)[CreateCommittedResourceSlot];
        Assert.Equal(0, create(device.Device, properties, 0, &desc, RenderTargetState, null, &iid, &resource));

        return resource;
    }

    private static nint CreateHiddenWindow(int width = 64, int height = 32)
    {
        fixed (char* className = "STATIC")
        {
            nint window = CreateWindowExW(0, className, null, PopupStyle, 0, 0, width, height, 0, 0, 0, 0);
            Assert.NotEqual(0, window);

            return window;
        }
    }

    [DllImport("user32")]
    private static extern int SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32")]
    private static extern nint CreateWindowExW(uint exStyle, char* className, char* title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32")]
    private static extern int DestroyWindow(nint window);
}
