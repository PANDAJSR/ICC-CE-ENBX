using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ICC.CE.ENBX;

public sealed partial class EnbxWebViewLayer
{
    private Window? _popupOwner;
    private bool _popupHookInstalled;
    private readonly HashSet<Window> _webPopups = new();

    internal void SetPopupOwner(Window owner)
    {
        _popupOwner = owner;
        if (_popupHookInstalled) return;
        _popupHookInstalled = true;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CloseWebPopups();
        };
        _webView.CoreWebView2InitializationCompleted += (_, args) =>
        {
            if (args.IsSuccess) _webView.CoreWebView2.NewWindowRequested += OnNewWebWindowRequested;
        };
        if (_webView.CoreWebView2 != null)
            _webView.CoreWebView2.NewWindowRequested += OnNewWebWindowRequested;
    }

    internal void CloseWebPopups()
    {
        foreach (var popup in _webPopups.ToArray()) popup.Close();
    }

    private async void OnNewWebWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args)
    {
        // 在返回事件前接管请求，避免 WebView2 创建可能被批注宿主遮挡的默认窗口。
        args.Handled = true;
        using var deferral = args.GetDeferral();
        if (_disposed || !IsVisible || _popupOwner?.IsVisible != true) return;

        var web = new WebView2();
        var popup = new Window
        {
            Title = "课件网页",
            Owner = _popupOwner,
            Topmost = true,
            Width = Math.Min(1100, SystemParameters.WorkArea.Width),
            Height = Math.Min(760, SystemParameters.WorkArea.Height),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = web
        };
        _webPopups.Add(popup);
        popup.Closed += (_, _) =>
        {
            _webPopups.Remove(popup);
            web.Dispose();
        };
        try
        {
            // NewWindow 接管前先写入账号 Cookie，确保弹窗的首个请求就携带登录态。
            await SynchronizeBrowserAuthAsync();
            if (!_webPopups.Contains(popup)) return;
            if (_disposed || !IsVisible || _popupOwner?.IsVisible != true)
            {
                popup.Close();
                return;
            }
            popup.Show();
            await web.EnsureCoreWebView2Async(_webView.CoreWebView2.Environment);
            // 用户可能在异步初始化期间关闭弹窗或结束放映。
            if (!_webPopups.Contains(popup) || _disposed) return;
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = DiagnosticsEnabled;
            core.Settings.IsStatusBarEnabled = false;
            core.DocumentTitleChanged += (_, _) => popup.Title =
                string.IsNullOrWhiteSpace(core.DocumentTitle) ? "课件网页" : core.DocumentTitle;
            core.WindowCloseRequested += (_, _) => popup.Close();
            core.NewWindowRequested += OnNewWebWindowRequested;
            args.NewWindow = core;
            popup.Activate();
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke("Web popup initialization failed: " + ex);
            if (_webPopups.Contains(popup)) popup.Close();
        }
    }
}
