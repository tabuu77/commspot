using System.Runtime.InteropServices;

namespace CommSpot.Client;

/// <summary>
/// Reads the state of one key or mouse button system-wide, so push-to-talk works while a game has focus.
/// It polls the key state instead of installing a keyboard hook: nothing is logged, and only the bound
/// key is read, except while the user is picking a key.
/// Bindings are strings: "vk:5" (Windows virtual key), "kc:37" (X11 keycode) or "btn:2" (X11 mouse button).
/// </summary>
internal abstract class GlobalInput : IDisposable
{
    public static GlobalInput? Create()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return new WindowsInput();
            }
            if (OperatingSystem.IsLinux())
            {
                return X11Input.TryCreate();
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
        return null;
    }

    public abstract bool IsDown(string binding);

    /// <summary>The first key or button held right now (left/right click excluded), or null.</summary>
    public abstract string? FindPressed();

    public abstract bool IsEscape(string binding);

    public abstract string Describe(string binding);

    public virtual void Dispose()
    {
    }

    protected static int? Parse(string binding, string prefix) =>
        binding.StartsWith(prefix, StringComparison.Ordinal)
        && int.TryParse(binding.AsSpan(prefix.Length), System.Globalization.CultureInfo.InvariantCulture, out int v)
            ? v : null;
}

internal sealed partial class WindowsInput : GlobalInput
{
    private const int VkEscape = 0x1B;

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial short GetAsyncKeyState(int vKey);

    [LibraryImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint MapVirtualKey(uint code, uint mapType);

    [LibraryImport("user32.dll", EntryPoint = "GetKeyNameTextW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetKeyNameText(int lParam, char* buffer, int size);

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public override bool IsDown(string binding) => Parse(binding, "vk:") is int vk && Down(vk);

    public override string? FindPressed()
    {
        for (int vk = 0x03; vk <= 0xFE; vk++)
        {
            // Skip the generic Shift/Ctrl/Alt codes; the left/right versions (0xA0-0xA5) are reported instead.
            if (vk is 0x10 or 0x11 or 0x12)
            {
                continue;
            }
            if (Down(vk))
            {
                return "vk:" + vk.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    public override bool IsEscape(string binding) => Parse(binding, "vk:") == VkEscape;

    public override string Describe(string binding)
    {
        if (Parse(binding, "vk:") is not int vk)
        {
            return "None";
        }
        switch (vk)
        {
            case 0x04: return "Mouse 3 (middle)";
            case 0x05: return "Mouse 4 (side back)";
            case 0x06: return "Mouse 5 (side front)";
        }
        uint scan = MapVirtualKey((uint)vk, 0);
        if (scan != 0)
        {
            int lParam = (int)(scan << 16);
            // Keys on the extended part of the keyboard need bit 24 for the right name.
            if (vk is (>= 0x21 and <= 0x2E) or 0xA3 or 0xA5 or 0x5B or 0x5C or 0x6F or 0x90)
            {
                lParam |= 1 << 24;
            }
            unsafe
            {
                char* buf = stackalloc char[64];
                int n = GetKeyNameText(lParam, buf, 64);
                if (n > 0)
                {
                    return new string(buf, 0, n);
                }
            }
        }
        return "Key " + vk.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Linux (X11). Keyboard keys and the middle mouse button; X11 can't poll the side buttons.</summary>
internal sealed partial class X11Input : GlobalInput
{
    private const string Lib = "libX11.so.6";
    private const ulong KeysymEscape = 0xff1b;
    private readonly IntPtr _display;
    private readonly nuint _root;
    private readonly Lock _gate = new();

    [LibraryImport(Lib)] private static partial IntPtr XOpenDisplay(IntPtr name);
    [LibraryImport(Lib)] private static partial int XCloseDisplay(IntPtr display);
    [LibraryImport(Lib)] private static partial nuint XDefaultRootWindow(IntPtr display);
    [LibraryImport(Lib)] private static unsafe partial int XQueryKeymap(IntPtr display, byte* keys);
    [LibraryImport(Lib)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool XQueryPointer(IntPtr display, nuint w, out nuint root, out nuint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);
    [LibraryImport(Lib)] private static partial nuint XkbKeycodeToKeysym(IntPtr display, byte keycode, int group, int level);
    [LibraryImport(Lib)] private static partial IntPtr XKeysymToString(nuint keysym);

    private X11Input(IntPtr display)
    {
        _display = display;
        _root = XDefaultRootWindow(display);
    }

    public static X11Input? TryCreate()
    {
        IntPtr d = XOpenDisplay(IntPtr.Zero);
        return d == IntPtr.Zero ? null : new X11Input(d);
    }

    private unsafe bool KeyDown(int keycode, byte* keys) => keycode is > 0 and < 256 && (keys[keycode / 8] & (1 << (keycode % 8))) != 0;

    private uint Buttons()
    {
        XQueryPointer(_display, _root, out _, out _, out _, out _, out _, out _, out uint mask);
        return mask;
    }

    public override unsafe bool IsDown(string binding)
    {
        lock (_gate)
        {
            if (Parse(binding, "btn:") is int b)
            {
                return (Buttons() & (1u << (7 + b))) != 0;
            }
            if (Parse(binding, "kc:") is int kc)
            {
                byte* keys = stackalloc byte[32];
                _ = XQueryKeymap(_display, keys);
                return KeyDown(kc, keys);
            }
            return false;
        }
    }

    public override unsafe string? FindPressed()
    {
        lock (_gate)
        {
            if ((Buttons() & (1u << 9)) != 0)
            {
                return "btn:2";
            }
            byte* keys = stackalloc byte[32];
            _ = XQueryKeymap(_display, keys);
            for (int kc = 8; kc < 256; kc++)
            {
                if (KeyDown(kc, keys))
                {
                    return "kc:" + kc.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            return null;
        }
    }

    private nuint Keysym(int kc)
    {
        lock (_gate)
        {
            return XkbKeycodeToKeysym(_display, (byte)kc, 0, 0);
        }
    }

    public override bool IsEscape(string binding) => Parse(binding, "kc:") is int kc && Keysym(kc) == KeysymEscape;

    public override string Describe(string binding)
    {
        if (Parse(binding, "btn:") is int b)
        {
            return b == 2 ? "Mouse 3 (middle)" : "Mouse " + b.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        if (Parse(binding, "kc:") is int kc)
        {
            IntPtr name = XKeysymToString(Keysym(kc));
            return name != IntPtr.Zero && Marshal.PtrToStringAnsi(name) is { Length: > 0 } n ? (n.Length == 1 ? n.ToUpperInvariant() : n) : "Key " + kc.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        return "None";
    }

    public override void Dispose()
    {
        lock (_gate)
        {
            _ = XCloseDisplay(_display);
        }
        base.Dispose();
    }
}

/// <summary>Polls the push-to-talk key on a background thread and reports changes.</summary>
internal sealed class PushToTalkWatcher : IDisposable
{
    private readonly GlobalInput _input;
    private readonly Action<bool> _changed;
    private readonly ManualResetEventSlim _stop = new();
    private readonly Thread _thread;
    private volatile string? _binding;

    public PushToTalkWatcher(GlobalInput input, Action<bool> changed)
    {
        _input = input;
        _changed = changed;
        _thread = new Thread(Run) { IsBackground = true, Name = "push-to-talk" };
        _thread.Start();
    }

    /// <summary>The key to watch, or null to watch nothing.</summary>
    public string? Binding
    {
        get => _binding;
        set => _binding = value;
    }

    private void Run()
    {
        bool last = false;
        while (!_stop.Wait(8))
        {
            string? b = _binding;
            bool down = b is not null && _input.IsDown(b);
            if (down != last)
            {
                last = down;
                _changed(down);
            }
        }
        if (last)
        {
            _changed(false);
        }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(500);
        _stop.Dispose();
    }
}
