using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

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
    private const uint SwpNoActivate = 0x0010;
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private static readonly IntPtr HwndTopMost = new(-1);
    private static readonly IntPtr HwndNotTopMost = new(-2);
    private static readonly IntPtr HwndBottom = new(1);

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
    private DispatcherTimer? _anchorTimer;
    private bool _allowClose;
    private bool _disposed;

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
                Content = _layer
            };
            _window.Closing += OnWindowClosing;
            _window.Activated += OnWindowActivated;
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
        }

        _window.Show();
        UpdateBounds();
        _window.UpdateLayout();
        AnchorBelowHost();
        StartAnchorTimer();
    }

    public void Hide()
    {
        StopAnchorTimer();
        DetachHost();
        _window?.Hide();
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
        if (next != exStyle) SetWindowLong32(handle, GwlExStyle, next);
    }

    private void OnWindowActivated(object? sender, EventArgs e) => AnchorBelowHost();

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _window?.Hide();
    }

    private void StartAnchorTimer()
    {
        _anchorTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(1000),
            DispatcherPriority.Background,
            (_, _) => AnchorBelowHost(),
            Dispatcher.CurrentDispatcher);
        _anchorTimer.Start();
    }

    private void StopAnchorTimer() => _anchorTimer?.Stop();

    private void DetachHost()
    {
        if (_host == null) return;
        _host.LocationChanged -= OnHostBoundsChanged;
        _host.SizeChanged -= OnHostBoundsChanged;
        _host.StateChanged -= OnHostBoundsChanged;
        _host = null;
    }

    private void OnHostBoundsChanged(object? sender, EventArgs e)
    {
        UpdateBounds();
        AnchorBelowHost();
    }

    private void UpdateBounds()
    {
        if (_window == null || _host == null) return;

        var width = _host.ActualWidth > 0 ? _host.ActualWidth : _host.Width;
        var height = _host.ActualHeight > 0 ? _host.ActualHeight : _host.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;

        _window.Left = _host.Left;
        _window.Top = _host.Top;
        _window.Width = width;
        _window.Height = height;
    }

    /// <summary>
    /// Inserts this window directly below the ICC-CE window in the z-order.
    /// Being immediately below means the web content shows through ICC-CE's
    /// transparent canvas while ICC-CE keeps its toolbars and nav bars on top.
    /// </summary>
    private void AnchorBelowHost()
    {
        if (_window == null || !_window.IsVisible) return;
        var handle = new WindowInteropHelper(_window).Handle;
        if (handle == IntPtr.Zero) return;

        var hostHandle = _host != null ? new WindowInteropHelper(_host).Handle : IntPtr.Zero;
        SetWindowPos(
            handle,
            hostHandle != IntPtr.Zero ? hostHandle : HwndBottom,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Forces the ICC-CE window to the topmost band while presenting.</summary>
    public static void ForceTopMost(Window window, bool topMost)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        SetWindowPos(
            handle,
            topMost ? HwndTopMost : HwndNotTopMost,
            0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopAnchorTimer();
        DetachHost();
        if (_window != null)
        {
            _allowClose = true;
            _window.Closing -= OnWindowClosing;
            _window.Content = null;
            _window.Close();
            _window = null;
        }
    }
}
