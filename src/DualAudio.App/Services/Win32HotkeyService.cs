using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using DualAudio.Core.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DualAudio.App.Services;

/// <summary>
/// Global shortcuts via RegisterHotKey/UnregisterHotKey on a hidden message-only window (HwndSource with parent
/// HWND_MESSAGE). Windows delivers WM_HOTKEY to that window; no keyboard hook of any kind is installed, and nothing
/// here ever shows a window. RegisterHotKey binds a hotkey to the calling thread's window, so every call is marshalled
/// to the UI dispatcher that owns the HwndSource. <see cref="HotkeyPressed"/> is raised on that thread.
/// </summary>
public sealed partial class Win32HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly HashSet<HotkeyAction> _registered = []; // UI thread only
    private HwndSource? _source;
    private bool _disposed;

    public Win32HotkeyService(Dispatcher dispatcher, ILogger<Win32HotkeyService>? logger = null)
    {
        _dispatcher = dispatcher;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    public event EventHandler<HotkeyPressedEventArgs>? HotkeyPressed;

    public bool TryRegister(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return OnUiThread(
            () =>
            {
                if (_disposed || EnsureWindow() is not { } hwnd)
                {
                    return false;
                }

                var id = IdFor(binding.Action);
                if (_registered.Remove(binding.Action))
                {
                    UnregisterHotKey(hwnd, id);
                }

                if (!RegisterHotKey(hwnd, id, ToNativeModifiers(binding), (uint)binding.VirtualKey))
                {
                    var error = Marshal.GetLastPInvokeError();
                    _logger.LogWarning(
                        "RegisterHotKey({Action}) failed: {Error}",
                        binding.Action,
                        error == ErrorHotkeyAlreadyRegistered ? "combination already registered by another app" : $"Win32 error {error}");
                    return false;
                }

                _registered.Add(binding.Action);
                return true;
            },
            fallback: false);
    }

    public void Unregister(HotkeyAction action) =>
        OnUiThread(
            () =>
            {
                if (_source is not null && _registered.Remove(action))
                {
                    UnregisterHotKey(_source.Handle, IdFor(action));
                }

                return true;
            },
            fallback: false);

    public void UnregisterAll() =>
        OnUiThread(
            () =>
            {
                foreach (var action in _registered.ToList())
                {
                    if (_source is not null)
                    {
                        UnregisterHotKey(_source.Handle, IdFor(action));
                    }
                }

                _registered.Clear();
                return true;
            },
            fallback: false);

    public void Dispose() =>
        OnUiThread(
            () =>
            {
                if (_disposed)
                {
                    return true;
                }

                _disposed = true;
                if (_source is { } source)
                {
                    foreach (var action in _registered)
                    {
                        UnregisterHotKey(source.Handle, IdFor(action));
                    }

                    _registered.Clear();
                    source.RemoveHook(WndProc);
                    source.Dispose();
                    _source = null;
                }

                return true;
            },
            fallback: false);

    /// <summary>Win32 MOD_* values; MOD_NOREPEAT for mute so holding the keys toggles once. Volume may auto-repeat.</summary>
    internal static uint ToNativeModifiers(HotkeyBinding binding)
    {
        var mods = 0u;
        mods |= binding.Modifiers.HasFlag(HotkeyModifiers.Alt) ? ModAlt : 0;
        mods |= binding.Modifiers.HasFlag(HotkeyModifiers.Control) ? ModControl : 0;
        mods |= binding.Modifiers.HasFlag(HotkeyModifiers.Shift) ? ModShift : 0;
        mods |= binding.Modifiers.HasFlag(HotkeyModifiers.Windows) ? ModWin : 0;
        return binding.Action == HotkeyAction.ToggleMute ? mods | ModNoRepeat : mods;
    }

    /// <summary>Hotkey ids for apps must be in 0x0000..0xBFFF.</summary>
    internal static int IdFor(HotkeyAction action) => 0x0100 + (int)action;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    private IntPtr? EnsureWindow()
    {
        if (_source is null)
        {
            var parameters = new HwndSourceParameters("DualAudio.Hotkeys")
            {
                ParentWindow = HwndMessage, // message-only: never visible, never takes focus
                WindowStyle = 0,
                Width = 0,
                Height = 0,
            };
            _source = new HwndSource(parameters);
            _source.AddHook(WndProc);
        }

        return _source.Handle;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        var id = wParam.ToInt32();
        foreach (var action in _registered)
        {
            if (IdFor(action) == id)
            {
                handled = true;
                try
                {
                    HotkeyPressed?.Invoke(this, new HotkeyPressedEventArgs(action));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "HotkeyPressed subscriber threw");
                }

                break;
            }
        }

        return IntPtr.Zero;
    }

    private T OnUiThread<T>(Func<T> action, T fallback)
    {
        try
        {
            if (_dispatcher.CheckAccess())
            {
                return action();
            }

            return _dispatcher.HasShutdownStarted ? fallback : _dispatcher.Invoke(action);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hotkey call failed");
            return fallback;
        }
    }
}
