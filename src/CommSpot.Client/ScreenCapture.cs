using System.Runtime.InteropServices;
using SkiaSharp;

namespace CommSpot.Client;

/// <summary>A screen or window that can be shared.</summary>
internal sealed record ShareSource(string Name, bool IsWindow, IntPtr Handle, int X, int Y, int Width, int Height)
{
    public override string ToString() => Name;
}

/// <summary>Grabs pixels from a screen or window. Windows only for now (GDI, no extra dependencies).</summary>
internal static partial class ScreenCapture
{
    public static bool Supported => OperatingSystem.IsWindows();

    public static IReadOnlyList<ShareSource> ListSources(IntPtr ownWindow)
    {
        if (!Supported)
        {
            return [];
        }
        var list = new List<ShareSource>();
        var monitors = new List<RECT>();
        var h = GCHandle.Alloc(monitors);
        try
        {
            unsafe
            {
                _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, &OnMonitor, GCHandle.ToIntPtr(h));
            }
        }
        finally
        {
            h.Free();
        }
        for (int i = 0; i < monitors.Count; i++)
        {
            var r = monitors[i];
            list.Add(new ShareSource($"Screen {i + 1}  ({r.Right - r.Left}x{r.Bottom - r.Top})", false, IntPtr.Zero,
                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }

        var windows = new List<IntPtr>();
        h = GCHandle.Alloc(windows);
        try
        {
            unsafe
            {
                _ = EnumWindows(&OnWindow, GCHandle.ToIntPtr(h));
            }
        }
        finally
        {
            h.Free();
        }
        foreach (var w in windows)
        {
            if (w == ownWindow || !IsWindowVisible(w) || IsIconic(w)
                || (GetWindowLongW(w, GwlExStyle) & WsExToolWindow) != 0 || IsCloaked(w))
            {
                continue;
            }
            string title = Title(w);
            if (title.Length == 0 || !GetWindowRect(w, out var r) || r.Right - r.Left < 100 || r.Bottom - r.Top < 100)
            {
                continue;
            }
            list.Add(new ShareSource("Window: " + title, true, w, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }
        return list;
    }

    /// <summary>Captures the source as opaque BGRA pixels, or null if it's gone (window closed).</summary>
    public static SKBitmap? Capture(ShareSource s)
    {
        int x = s.X, y = s.Y, w = s.Width, hgt = s.Height;
        if (s.IsWindow)
        {
            if (!IsWindow(s.Handle) || IsIconic(s.Handle) || !GetWindowRect(s.Handle, out var r))
            {
                return null;
            }
            w = r.Right - r.Left;
            hgt = r.Bottom - r.Top;
        }
        if (w <= 0 || hgt <= 0 || w > 8192 || hgt > 8192)
        {
            return null;
        }

        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, w, hgt);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            bool ok = s.IsWindow
                ? PrintWindow(s.Handle, mem, PwRenderFullContent)
                : BitBlt(mem, 0, 0, w, hgt, screen, x, y, SrcCopy | CaptureBlt);
            if (!ok)
            {
                return null;
            }
            var bitmap = new SKBitmap(new SKImageInfo(w, hgt, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var info = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = w,
                biHeight = -hgt, // top-down rows
                biPlanes = 1,
                biBitCount = 32,
            };
            SelectObject(mem, old);
            int lines = GetDIBits(mem, bmp, 0, (uint)hgt, bitmap.GetPixels(), ref info, 0);
            old = IntPtr.Zero;
            if (lines != hgt)
            {
                bitmap.Dispose();
                return null;
            }
            return bitmap;
        }
        finally
        {
            if (old != IntPtr.Zero)
            {
                SelectObject(mem, old);
            }
            DeleteObject(bmp);
            DeleteDC(mem);
            _ = ReleaseDC(IntPtr.Zero, screen);
        }
    }

    /// <summary>Scales to at most <paramref name="maxHeight"/> and encodes as JPEG.</summary>
    public static byte[] Encode(SKBitmap source, int maxHeight, int quality)
    {
        int th = Math.Min(maxHeight, source.Height);
        int tw = Math.Max(2, (int)Math.Round(source.Width * (double)th / source.Height) & ~1);
        th &= ~1;
        using var scaled = th == source.Height && tw == source.Width
            ? source.Copy()
            : source.Resize(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Opaque), new SKSamplingOptions(SKFilterMode.Linear));
        using var img = SKImage.FromBitmap(scaled);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data.ToArray();
    }

    private static string Title(IntPtr w)
    {
        int len = GetWindowTextLengthW(w);
        if (len <= 0)
        {
            return string.Empty;
        }
        var buf = new char[Math.Min(len + 1, 512)];
        unsafe
        {
            fixed (char* p = buf)
            {
                int n = GetWindowTextW(w, p, buf.Length);
                return new string(p, 0, Math.Max(0, n));
            }
        }
    }

    private static bool IsCloaked(IntPtr w)
    {
        int cloaked = 0;
        unsafe
        {
            return DwmGetWindowAttribute(w, DwmwaCloaked, &cloaked, sizeof(int)) == 0 && cloaked != 0;
        }
    }

    [UnmanagedCallersOnly]
    private static int OnMonitor(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
    {
        var list = (List<RECT>)GCHandle.FromIntPtr(data).Target!;
        list.Add(Marshal.PtrToStructure<RECT>(rect));
        return 1;
    }

    [UnmanagedCallersOnly]
    private static int OnWindow(IntPtr hwnd, IntPtr data)
    {
        ((List<IntPtr>)GCHandle.FromIntPtr(data).Target!).Add(hwnd);
        return 1;
    }

    private const int SrcCopy = 0x00CC0020;
    private const int CaptureBlt = 0x40000000;
    private const uint PwRenderFullContent = 2;
    private const int GwlExStyle = -20;
    private const int WsExToolWindow = 0x80;
    private const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        public int bmiColors; // room for one palette entry, unused for 32 bpp
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int EnumDisplayMonitors(IntPtr hdc, IntPtr clip, delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, int> cb, IntPtr data);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int EnumWindows(delegate* unmanaged<IntPtr, IntPtr, int> cb, IntPtr data);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr w);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(IntPtr w);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(IntPtr w);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowLongW(IntPtr w, int index);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetWindowTextLengthW(IntPtr w);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetWindowTextW(IntPtr w, char* buf, int max);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr w, out RECT rect);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(IntPtr w, IntPtr hdc, uint flags);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr GetDC(IntPtr w);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReleaseDC(IntPtr w, IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines, IntPtr bits, ref BITMAPINFOHEADER info, uint usage);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(IntPtr obj);

    [LibraryImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(IntPtr hdc);

    [LibraryImport("dwmapi.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int DwmGetWindowAttribute(IntPtr w, int attr, void* value, int size);
}

/// <summary>Captures, encodes and sends frames on a background thread until stopped.</summary>
internal sealed class ScreenSharer : IDisposable
{
    private const int BudgetBytesPerSecond = 1_200_000; // ~10 Mbit/s; the server allows a bit more
    private readonly Core.VoiceClient _client;
    private readonly ShareSource _source;
    private readonly int _maxHeight;
    private readonly int _fps;
    private readonly int _maxQuality;
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _thread;

    public ScreenSharer(Core.VoiceClient client, ShareSource source, int maxHeight, int fps)
    {
        _client = client;
        _source = source;
        _maxHeight = maxHeight;
        _fps = Math.Clamp(fps, 1, 30);
        _maxQuality = maxHeight >= 1080 ? 75 : 70;
        _thread = new Thread(Run) { IsBackground = true, Name = "screen-share", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Raised (background thread) when the shared window disappears.</summary>
    public event Action? SourceLost;

    private void Run()
    {
        int quality = _maxQuality;
        byte[]? last = null;
        long lastSent = 0;
        var interval = TimeSpan.FromMilliseconds(1000.0 / _fps);
        while (!_cts.IsCancellationRequested)
        {
            long start = Environment.TickCount64;
            using (var frame = ScreenCapture.Capture(_source))
            {
                if (frame is null)
                {
                    SourceLost?.Invoke();
                    return;
                }
                byte[] jpeg = ScreenCapture.Encode(frame, _maxHeight, quality);
                bool same = last is not null && jpeg.AsSpan().SequenceEqual(last);
                // Unchanged screens are re-sent only every 2 s, so new watchers still get a picture.
                if (!same || start - lastSent > 2000)
                {
                    if (jpeg.Length <= Protocol.VoiceConstants.MaxVideoFragments * Protocol.VoiceConstants.MaxVideoFragmentData)
                    {
                        _client.SendVideoFrame(jpeg);
                        lastSent = start;
                    }
                    else
                    {
                        quality = 30;
                    }
                }
                last = jpeg;
                // Keep within the bandwidth budget by adjusting JPEG quality.
                long rate = (long)jpeg.Length * _fps;
                if (rate > BudgetBytesPerSecond)
                {
                    quality = Math.Max(25, quality - 5);
                }
                else if (rate < BudgetBytesPerSecond / 2)
                {
                    quality = Math.Min(_maxQuality, quality + 2);
                }
            }
            int wait = (int)(interval.TotalMilliseconds - (Environment.TickCount64 - start));
            if (wait > 0 && _cts.Token.WaitHandle.WaitOne(wait))
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread.Join(1000);
        _cts.Dispose();
    }
}
