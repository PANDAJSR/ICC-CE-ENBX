using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;

namespace ICC.CE.ENBX;

public sealed partial class EnbxWebViewLayer
{
    // 与官方 BrowserEnvironment 的网页账号作用域保持一致，不向任意课件链接附加凭证。
    private static readonly string[] SeewoBrowserDomains =
        ["seewo.com", "easinote.flashshan.com", "live-t.imlizhi.com", "seewoedu.cn"];
    private SeewoAuthManager? _browserAuthManager;
    private bool _browserAuthDirty = true;
    private readonly SemaphoreSlim _browserAuthGate = new(1, 1);

    internal void SetBrowserAuthManager(SeewoAuthManager manager)
    {
        if (_browserAuthManager != null)
            _browserAuthManager.AuthStateChanged -= OnBrowserAuthChanged;
        _browserAuthManager = manager;
        _browserAuthManager.AuthStateChanged += OnBrowserAuthChanged;
        _browserAuthDirty = true;
    }

    private void OnBrowserAuthChanged(SeewoAuthProfile? profile)
    {
        _ = Dispatcher.InvokeAsync(async () =>
        {
            if (_disposed) return;
            _browserAuthDirty = true;
            // 页面可能把旧账号状态留在内存或 storage 中，切换账号后需重新打开。
            CloseWebPopups();
            try { await SynchronizeBrowserAuthAsync(); }
            catch (Exception ex)
            {
                // 不输出异常正文，避免浏览器错误中携带凭证。
                Diagnostic?.Invoke("Browser account synchronization failed: " + ex.GetType().Name);
            }
        });
    }

    private async Task SynchronizeBrowserAuthAsync()
    {
        await _browserAuthGate.WaitAsync();
        try
        {
            if (_disposed || !_browserAuthDirty || _webView.CoreWebView2 == null) return;
            var manager = _webView.CoreWebView2.CookieManager;
            var cookies = await manager.GetCookiesAsync("");
            if (_disposed) return;
            // 清除这些服务的旧会话 Cookie，避免重启、退出或切换账号后沿用上一个账号。
            foreach (var cookie in cookies)
            {
                var domain = cookie.Domain.TrimStart('.');
                if (SeewoBrowserDomains.Any(root => domain.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith("." + root, StringComparison.OrdinalIgnoreCase)))
                    manager.DeleteCookie(cookie);
            }
            var profile = _browserAuthManager?.CurrentProfile;
            if (profile is { IsValid: true, IsExpired: false })
            {
                foreach (var domain in SeewoBrowserDomains)
                {
                    SetCookie("x-auth-token", profile.Token, domain);
                    SetCookie("x-token", profile.Token, domain);
                    SetCookie("x-auth-app", "EasiNote5", domain);
                    SetCookie("x-auth-brand", "seewo", domain);
                    if (!string.IsNullOrWhiteSpace(profile.Phone)) SetCookie("phone", profile.Phone, domain);
                }
            }
            _browserAuthDirty = false;
            Diagnostic?.Invoke("Browser account synchronized; authenticated=" +
                (profile is { IsValid: true, IsExpired: false }));

            void SetCookie(string name, string value, string domain)
            {
                var cookie = manager.CreateCookie(name, value, "." + domain, "/");
                cookie.IsHttpOnly = true;
                manager.AddOrUpdateCookie(cookie);
            }
        }
        finally { _browserAuthGate.Release(); }
    }

    private void DetachBrowserAuthManager()
    {
        if (_browserAuthManager != null)
            _browserAuthManager.AuthStateChanged -= OnBrowserAuthChanged;
        _browserAuthManager = null;
    }
}
