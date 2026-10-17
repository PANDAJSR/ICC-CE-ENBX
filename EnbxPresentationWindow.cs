using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ICC.CE.ENBX;

/// <summary>
/// Hosts the ENBX web content in a separate borderless window that is anchored
/// directly below the ICC-CE main window.
///
/// ICC-CE's main window is a layered transparent window (AllowsTransparency=true)
/// so it can annotate on top of other applications. Inside such a window WebView2
/// cannot be combined with the host's own WPF controls, so the web content lives
/// in its own window underneath and is seen through the transparent canvas.
/// Anchoring below ICC-CE keeps the host toolbars and PPT navigation bars on top
/// and clickable.
/// </summary>
internal sealed class EnbxPresentationWindow : IDisposable
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const int GwlExStyle = -20;
    private const int WsExTopMost = 0x00000008;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int WmWindowPosChanging = 0x0046;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmSysCommand = 0x0112;
    private const int ScMove = 0xF010;
    private const int HtCaption = 2;
    private const int MaNoActivate = 3;

    private static readonly IntPtr HwndTop = IntPtr.Zero;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly EnbxWebViewLayer _layer;
    private Window? _window;
    private Window? _host;
    private bool _allowClose;
    private bool _disposed;

    public event EventHandler? CloseRequested;
    public event EventHandler<string>? PageClickRequested;

    public EnbxPresentationWindow(EnbxWebViewLayer layer)
    {
        _layer = layer;
    }

    public bool IsVisible => _window?.IsVisible == true;

    public void ShowFor(Window host)
    {
        if (_disposed) return;

        if (_window == null)
        {
            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false,
                ShowInTaskbar = false,
                AllowsTransparency = false,
                Background = Brushes.Black,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Content = _layer,
                Tag = "ExcludeFromTopmostManager"
            };
            _window.Closing += OnWindowClosing;
            _window.SourceInitialized += OnWindowSourceInitialized;
            _window.PreviewKeyDown += OnWindowPreviewKeyDown;
            _window.LocationChanged += OnWindowLocationChanged;
            _window.Show();
            ApplyNoActivateStyle();
        }

        if (!ReferenceEquals(_host, host))
        {
            DetachHost();
            _host = host;
            _host.LocationChanged += OnHostBoundsChanged;
            _host.SizeChanged += OnHostBoundsChanged;
            _host.StateChanged += OnHostBoundsChanged;
            HookNavBarPageClicks();
        }
        else
        {
            HookNavBarPageClicks();
        }

        TryUnregisterFromHostTopmostManager();
        _window.Show();
        UpdateBounds();
        _window.UpdateLayout();
        Anchor();
    }

    public void Hide()
    {
        DetachHost();
        _window?.Hide();
    }

    private void OnWindowLocationChanged(object? sender, EventArgs e)
    {
        if (_host != null && _window != null)
        {
            if (Math.Abs(_window.Left - _host.Left) >= 0.5 || Math.Abs(_window.Top - _host.Top) >= 0.5)
            {
                UpdateBounds();
            }
        }
    }

    private void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (_window == null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        var source = HwndSource.FromHwnd(handle);
        source?.AddHook(WndProc);

        ApplyNoActivateStyle();
        TryUnregisterFromHostTopmostManager();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            // 点击课件不激活窗口，保留宿主工具栏为当前激活焦点，同时正常将点击分发给 WebView2
            handled = true;
            return new IntPtr(MaNoActivate);
        }

        // 拦截通过标题栏拖动或系统命令移动窗口的操作，确保全屏课件窗口绝对不可被拖动
        if (msg == WmSysCommand && ((int)wParam.ToInt64() & 0xFFF0) == ScMove)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmNcLButtonDown && wParam.ToInt64() == HtCaption)
        {
            handled = true;
            return IntPtr.Zero;
        }

        if (msg == WmWindowPosChanging)
        {
            var pos = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            // 确保本窗口永远保持非置顶（不能插入到 HWND_TOPMOST 或被提升为 TOPMOST）
            if (pos.hwndInsertAfter == HwndTopMost)
            {
                pos.hwndInsertAfter = HwndNotTopMost;
                Marshal.StructureToPtr(pos, lParam, false);
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Marks the window as a non-activating tool window. Without this a click on
    /// the web content raises it above ICC-CE and swallows toolbar clicks.
    /// </summary>
    private void ApplyNoActivateStyle()
    {
        if (_window == null) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        var exStyle = GetWindowLong32(handle, GwlExStyle);
        var next = exStyle | WsExNoActivate | WsExToolWindow;
        if (next != exStyle)
        {
            SetWindowLong32(handle, GwlExStyle, next);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
        }
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _window?.Hide();
    }

    private void DetachHost()
    {
        if (_host == null) return;
        UnhookNavBarPageClicks();
        _host.LocationChanged -= OnHostBoundsChanged;
        _host.SizeChanged -= OnHostBoundsChanged;
        _host.StateChanged -= OnHostBoundsChanged;
        _host = null;
    }

    private readonly List<(EventInfo Event, object Target, Delegate Handler)> _hookedEvents = new();

    private void HookNavBarPageClicks()
    {
        UnhookNavBarPageClicks();
        if (_host == null) return;

        HookBar("LeftBottomPanelForPPTNavigation", "left");
        HookBar("LeftSidePanelForPPTNavigation", "left");
        HookBar("RightBottomPanelForPPTNavigation", "right");
        HookBar("RightSidePanelForPPTNavigation", "right");
    }

    private void HookBar(string name, string side)
    {
        var bar = FindElement(name);
        if (bar == null) return;

        var evt = bar.GetType().GetEvent("PageClick");
        if (evt == null) return;

        EventHandler handler = (s, e) =>
        {
            PageClickRequested?.Invoke(this, side);
        };

        evt.AddEventHandler(bar, handler);
        _hookedEvents.Add((evt, bar, handler));
    }

    private void UnhookNavBarPageClicks()
    {
        foreach (var (evt, target, handler) in _hookedEvents)
        {
            try { evt.RemoveEventHandler(target, handler); } catch { }
        }
        _hookedEvents.Clear();
    }

    private FrameworkElement? FindElement(string name)
    {
        if (_host == null) return null;
        if (_host.FindName(name) is FrameworkElement found) return found;
        return FindVisualChildByName<FrameworkElement>(_host, name);
    }

    private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T element && element.Name == name)
                return element;

            var result = FindVisualChildByName<T>(child, name);
            if (result != null) return result;
        }
        return null;
    }

    private void OnHostBoundsChanged(object? sender, EventArgs e)
    {
        UpdateBounds();
        Anchor();
    }

    private void UpdateBounds()
    {
        if (_window == null || _host == null) return;

        var width = _host.ActualWidth > 0 ? _host.ActualWidth : _host.Width;
        var height = _host.ActualHeight > 0 ? _host.ActualHeight : _host.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;

        if (Math.Abs(_window.Left - _host.Left) < 0.5 &&
            Math.Abs(_window.Top - _host.Top) < 0.5 &&
            Math.Abs(_window.Width - width) < 0.5 &&
            Math.Abs(_window.Height - height) < 0.5)
            return;

        _window.Left = _host.Left;
        _window.Top = _host.Top;
        _window.Width = width;
        _window.Height = height;
    }

    /// <summary>
    /// Keeps the web window directly behind ICC-CE's main window in the Z-order.
    /// Both windows reside in the topmost band so ordinary desktop applications
    /// cannot slip between them, while the host window remains strictly in front
    /// to receive toolbar and ink interactions.
    /// </summary>
    private void Anchor()
    {
        if (_window == null || !_window.IsVisible) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        var hostHandle = _host != null ? new WindowInteropHelper(_host).Handle : IntPtr.Zero;
        if (hostHandle == IntPtr.Zero) return;

        TryUnregisterFromHostTopmostManager();

        // 1. 确保 host 拥有 TOPMOST 样式
        ForceTopMost(_host!, true);

        // 2. 确保 _window 保持普通非置顶（HWND_NOTOPMOST），绝不占用 WS_EX_TOPMOST
        ForceTopMost(_window, false);

        // 3. 将 host 设置在 TOPMOST 最前
        SetWindowPos(
            hostHandle,
            HwndTopMost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    private void OnWindowPreviewKeyDown(object? sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void TryUnregisterFromHostTopmostManager()
    {
        if (_window == null || _host == null) return;
        try
        {
            var wtmType = _host.GetType().Assembly.GetType("Ink_Canvas.Helpers.WindowTopmostManager");
            var unreg = wtmType?.GetMethod("UnregisterWindow", BindingFlags.Public | BindingFlags.Static);
            unreg?.Invoke(null, new object[] { _window });

            var exc = wtmType?.GetMethod("ExcludeWindow", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Window) }, null);
            exc?.Invoke(null, new object[] { _window });

            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                var excHwnd = wtmType?.GetMethod("ExcludeWindow", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(IntPtr) }, null);
                excHwnd?.Invoke(null, new object[] { handle });
            }
        }
        catch { }
    }

    /// <summary>Forces a window into or out of the topmost band.</summary>
    public static void ForceTopMost(Window window, bool topMost)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var exStyle = GetWindowLong32(handle, GwlExStyle);
        if (topMost)
        {
            if ((exStyle & WsExTopMost) == 0)
                SetWindowLong32(handle, GwlExStyle, exStyle | WsExTopMost);
        }
        else
        {
            if ((exStyle & WsExTopMost) != 0)
                SetWindowLong32(handle, GwlExStyle, exStyle & ~WsExTopMost);
        }

        SetWindowPos(
            handle,
            topMost ? HwndTopMost : HwndNotTopMost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpFrameChanged);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachHost();
        if (_window != null)
        {
            _allowClose = true;
            var handle = new WindowInteropHelper(_window).Handle;
            if (handle != IntPtr.Zero)
            {
                var source = HwndSource.FromHwnd(handle);
                source?.RemoveHook(WndProc);
            }
            _window.Closing -= OnWindowClosing;
            _window.SourceInitialized -= OnWindowSourceInitialized;
            _window.PreviewKeyDown -= OnWindowPreviewKeyDown;
            _window.Content = null;
            _window.Close();
            _window = null;
        }
    }
}
