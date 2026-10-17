using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;
using Microsoft.Web.WebView2.Core;

namespace ICC.CE.ENBX.Seewo.UI;

public partial class SeewoLoginControl : UserControl
{
    private readonly SeewoAuthManager _authManager;
    private readonly string _cacheDirectory;
    private DispatcherTimer? _cookiePollTimer;
    private bool _isCompleted;

    public event Action<SeewoAuthProfile>? LoginSuccess;

    public SeewoLoginControl(SeewoAuthManager authManager, string cacheDirectory)
    {
        InitializeComponent();
        _authManager = authManager;
        _cacheDirectory = cacheDirectory;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _cookiePollTimer?.Stop();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var webViewDataFolder = Path.Combine(_cacheDirectory, "SeewoWebData");
            Directory.CreateDirectory(webViewDataFolder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: webViewDataFolder);
            await LoginWebView.EnsureCoreWebView2Async(environment);

            LoginWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            LoginWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

            LoginWebView.CoreWebView2.SourceChanged += async (_, _) =>
            {
                await CheckUrlOrCookiesAsync(LoginWebView.Source?.ToString());
            };

            LoginWebView.NavigationStarting += async (_, e) =>
            {
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = "正在加载...";
                await CheckUrlOrCookiesAsync(e.Uri);
            };

            LoginWebView.NavigationCompleted += async (_, _) =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                await CheckUrlOrCookiesAsync(LoginWebView.Source?.ToString());
            };

            // 启动定时轮询检测（监测 URL 或 Cookie）
            _cookiePollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.0)
            };
            _cookiePollTimer.Tick += async (_, _) => await CheckUrlOrCookiesAsync(LoginWebView.Source?.ToString());
            _cookiePollTimer.Start();

            // 导航到希沃统一登录页
            LoginWebView.CoreWebView2.Navigate("https://id.seewo.com/login");
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"初始化登录组件失败: {ex.Message}";
        }
    }

    private async Task CheckUrlOrCookiesAsync(string? currentUrl)
    {
        if (_isCompleted || LoginWebView.CoreWebView2 == null) return;

        bool isUserPage = !string.IsNullOrWhiteSpace(currentUrl) &&
                          currentUrl.IndexOf("id.seewo.com/user", StringComparison.OrdinalIgnoreCase) >= 0;

        if (isUserPage)
        {
            await HandleUserPageLoginSuccessAsync();
            return;
        }

        // 常规 Cookie 监测
        await CheckAuthCookiesAsync();
    }

    private async Task HandleUserPageLoginSuccessAsync()
    {
        if (_isCompleted || LoginWebView.CoreWebView2 == null) return;
        _isCompleted = true;
        _cookiePollTimer?.Stop();

        LoadingOverlay.Visibility = Visibility.Visible;
        LoadingText.Text = "检测到已成功进入个人中心，正在获取登录凭证...";

        string? scriptToken = null;
        try
        {
            // 尝试从页面执行 JS 提取 localStorage 中的 token
            var jsResult = await LoginWebView.CoreWebView2.ExecuteScriptAsync(
                "(function() { try { return localStorage.getItem('token') || localStorage.getItem('x-auth-token') || sessionStorage.getItem('token') || ''; } catch { return ''; } })()");
            if (!string.IsNullOrWhiteSpace(jsResult) && jsResult != "null" && jsResult != "\"\"")
            {
                scriptToken = jsResult.Trim('\"', '\\');
            }
        }
        catch
        {
        }

        // 尝试从 CookieManager 提取
        SeewoAuthProfile? profile = null;
        for (int retry = 0; retry < 5; retry++)
        {
            profile = await _authManager.TryExtractFromWebViewAsync(LoginWebView.CoreWebView2.CookieManager, scriptToken);
            if (profile != null && profile.IsValid) break;
            await Task.Delay(300);
        }

        profile ??= new SeewoAuthProfile
        {
            Token = scriptToken ?? "user_authenticated",
            LoginTime = DateTime.UtcNow
        };

        _authManager.Save(profile);
        LoadingText.Text = $"登录成功，欢迎 {profile.DisplayName}！正在加载课件...";

        await Task.Delay(500);
        LoginSuccess?.Invoke(profile);
    }

    private async Task CheckAuthCookiesAsync()
    {
        if (_isCompleted || LoginWebView.CoreWebView2 == null) return;

        try
        {
            var profile = await _authManager.TryExtractFromWebViewAsync(LoginWebView.CoreWebView2.CookieManager);
            if (profile != null && profile.IsValid)
            {
                _isCompleted = true;
                _cookiePollTimer?.Stop();
                LoadingOverlay.Visibility = Visibility.Visible;
                LoadingText.Text = $"登录成功，欢迎 {profile.DisplayName}！正在加载课件...";

                await Task.Delay(500);
                LoginSuccess?.Invoke(profile);
            }
        }
        catch
        {
        }
    }

    private void OnRefreshLoginClick(object sender, RoutedEventArgs e)
    {
        _isCompleted = false;
        if (LoginWebView.CoreWebView2 != null)
        {
            LoginWebView.CoreWebView2.Reload();
        }
    }

    private async void OnClearAndReloadClick(object sender, RoutedEventArgs e)
    {
        _isCompleted = false;
        if (LoginWebView.CoreWebView2 != null)
        {
            try
            {
                var cookieManager = LoginWebView.CoreWebView2.CookieManager;
                var cookies = await cookieManager.GetCookiesAsync("https://id.seewo.com");
                foreach (var c in cookies) cookieManager.DeleteCookie(c);
                var eduCookies = await cookieManager.GetCookiesAsync("https://edu.seewo.com");
                foreach (var c in eduCookies) cookieManager.DeleteCookie(c);
            }
            catch
            {
            }
            LoginWebView.CoreWebView2.Navigate("https://id.seewo.com/login");
        }
    }
}
