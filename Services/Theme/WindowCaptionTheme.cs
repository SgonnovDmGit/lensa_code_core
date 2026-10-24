using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LenSa.Core.Services.Theme;

/// <summary>Applies existing theme brushes to a system caption without replacing its chrome.</summary>
public static class WindowCaptionTheme
{
    private static readonly DependencyProperty AttachmentProperty = DependencyProperty.RegisterAttached(
        "Attachment", typeof(Attachment), typeof(WindowCaptionTheme), new PropertyMetadata(null));

    public static void Attach(Window window) => AttachCore(window, SetNativeAttribute);

    private static void AttachCore(Window window, Action<IntPtr, int, int> setAttribute)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.VerifyAccess();
        if (window.GetValue(AttachmentProperty) is Attachment) return;
        var attachment = new Attachment(window, setAttribute);
        window.SetValue(AttachmentProperty, attachment);
        attachment.Apply(); // Also supports an already initialized HWND.
    }

    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static bool IsDark(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B) < 0.5;
    }

    private static void SetNativeAttribute(IntPtr hwnd, int attribute, int value)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // HRESULT errors leave cosmetic theming optional on unsupported systems.
        try { _ = DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // Own windows carry no small mark in the system caption (T-000425): the brand lives in the
    // main window's header. A window shown in the taskbar keeps its big icon there.
    private static void HideCaptionIcon(IntPtr hwnd, bool keepTaskbarIcon)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if ((style & WsExDlgModalFrame) == 0)
            SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style | WsExDlgModalFrame));
        SendMessage(hwnd, WmSetIcon, new IntPtr(IconSmall), IntPtr.Zero);
        if (!keepTaskbarIcon)
            SendMessage(hwnd, WmSetIcon, new IntPtr(IconBig), IntPtr.Zero);
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private const int GwlExStyle = -20;
    private const long WsExDlgModalFrame = 0x0001;
    private const int WmSetIcon = 0x0080;
    private const int IconSmall = 0;
    private const int IconBig = 1;
    private const uint SwpNoSize = 0x0001, SwpNoMove = 0x0002, SwpNoZOrder = 0x0004, SwpNoActivate = 0x0010, SwpFrameChanged = 0x0020;

    private sealed class Attachment
    {
        private readonly Window _window;
        private readonly Action<IntPtr, int, int> _setAttribute;
        private bool _closed;

        public Attachment(Window window, Action<IntPtr, int, int> setAttribute)
        {
            _window = window;
            _setAttribute = setAttribute;
            window.SourceInitialized += OnSourceInitialized;
            window.Closed += OnClosed;
            ThemeManager.ThemeChanged += OnThemeChanged;
        }

        public void Apply()
        {
            if (_closed) return;
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd == IntPtr.Zero ||
                _window.TryFindResource("TitleBarBackground") is not SolidColorBrush background ||
                _window.TryFindResource("TitleBarForeground") is not SolidColorBrush foreground)
                return;

            HideCaptionIcon(hwnd, _window.ShowInTaskbar);
            var dark = IsDark(background.Color) ? 1 : 0;
            var caption = ToColorRef(background.Color);
            var text = ToColorRef(foreground.Color);
            _setAttribute(hwnd, 20, dark);
            _setAttribute(hwnd, 35, caption);
            _setAttribute(hwnd, 36, text);
        }

        private void OnSourceInitialized(object? sender, EventArgs e) => Apply();

        private void OnThemeChanged()
        {
            if (_closed || _window.Dispatcher.HasShutdownStarted) return;
            if (_window.Dispatcher.CheckAccess()) Apply();
            else _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(Apply));
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _closed = true;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            _window.SourceInitialized -= OnSourceInitialized;
            _window.Closed -= OnClosed;
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
