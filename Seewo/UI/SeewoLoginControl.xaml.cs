using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;
using Microsoft.Web.WebView2.Core;

namespace ICC.CE.ENBX.Seewo.UI;

public partial class SeewoLoginControl : UserControl, IDisposable
{
    private readonly SeewoAuthManager _authManager;
    private readonly SeewoApiClient _apiClient;
    private readonly string _cacheDirectory;
    private readonly string _sessionName = "login_" + Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(750) };
    private bool _checking;
    private bool _completed;
    private bool _disposed;
    private bool _initialized;
    private DateTime _nextCheck;
    public event Action<SeewoAuthProfile>? LoginSuccess;
    public event Action? CancelRequested;

    private const string LoginStyleScript = """
        (() => {
          if (location.protocol !== 'https:' || location.hostname !== 'id.seewo.com' || !/^\/login\/?$/.test(location.pathname)) return;
          const apply = () => {
            if (!document.documentElement) return;
            if (!document.getElementById('icc-login-style')) {
              const style = document.createElement('style');
              style.id = 'icc-login-style';
              style.textContent = `#banner-box { display: none !important; }
                #login { float: none !important; position: relative !important;
                  left: auto !important; right: auto !important; transform: none !important;
                  margin-left: auto !important; margin-right: auto !important; max-width: 100% !important; }
                #bodyer { min-width: 0 !important; height: auto !important; }
                .login-container { height: auto !important; min-height: 500px !important; }
                .right-form { position: relative !important; top: auto !important; right: auto !important;
                  float: none !important; margin: 24px auto !important; max-width: calc(100% - 32px) !important;
                  height: auto !important; min-height: 415px !important; overflow: visible !important; }
                .right-form-inner { position: relative !important; height: auto !important; }
                body { overflow-y: auto !important; min-width: 0 !important; }`;
              document.documentElement.appendChild(style);
            }
            document.getElementById('banner-box')?.remove();
          };
          new MutationObserver(apply).observe(document, { childList: true, subtree: true });
          apply();
        })();
        """;

    public SeewoLoginControl(SeewoAuthManager authManager, SeewoApiClient apiClient, string cacheDirectory)
    {
        InitializeComponent();
        _authManager = authManager;
        _apiClient = apiClient;
        _cacheDirectory = cacheDirectory;
        Loaded += OnLoaded;
        _timer.Tick += OnPoll;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _disposed) return;
        _initialized = true;
        try
        {
            var folder = Path.Combine(_cacheDirectory, "SeewoLoginSessions");
            Directory.CreateDirectory(folder);
            var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            if (_disposed) return;
            // 每次添加/重新登录均使用独立的隐私会话，不继承其他账号的 Cookie。
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.ProfileName = _sessionName;
            options.IsInPrivateModeEnabled = true;
            await LoginWebView.EnsureCoreWebView2Async(environment, options);
            if (_disposed) return;
            var core = LoginWebView.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(LoginStyleScript);
            if (_disposed) return;
            core.SourceChanged += OnSourceChanged;
            core.NavigationStarting += OnNavigationStarting;
            core.NavigationCompleted += OnNavigationCompleted;
            _timer.Start();
            core.Navigate("https://id.seewo.com/login");
        }
        catch (Exception ex)
        {
            if (!_disposed) LoadingText.Text = $"初始化登录组件失败：{ex.Message}";
        }
    }

    private static bool IsUserPage(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host.Equals("id.seewo.com", StringComparison.OrdinalIgnoreCase) &&
        (uri.AbsolutePath.Equals("/user", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.StartsWith("/user/", StringComparison.OrdinalIgnoreCase));

    private async void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e)
    {
        if (!_disposed && IsUserPage(LoginWebView.CoreWebView2.Source)) _nextCheck = DateTime.MinValue;
        await CheckAsync();
    }
    private async void OnPoll(object? sender, EventArgs e) => await CheckAsync();
    private async void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (IsUserPage(e.Uri))
        {
            _nextCheck = DateTime.MinValue;
            LoadingText.Text = "登录成功，正在连接云课件…";
            LoadingOverlay.Visibility = Visibility.Visible;
            await CheckAsync();
        }
    }
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_disposed || _completed) return;
        LoadingOverlay.Visibility = IsUserPage(LoginWebView.CoreWebView2.Source) ? Visibility.Visible : Visibility.Collapsed;
        await CheckAsync();
    }

    private async Task CheckAsync()
    {
        if (_disposed || _completed || _checking || DateTime.UtcNow < _nextCheck || LoginWebView.CoreWebView2 == null) return;
        _checking = true;
        try
        {
            var core = LoginWebView.CoreWebView2;
            string? token = null;
            if (Uri.TryCreate(core.Source, UriKind.Absolute, out var uri) && uri.Host == "id.seewo.com")
            {
                var result = await core.ExecuteScriptAsync("(() => { try { return localStorage.getItem('token') || localStorage.getItem('x-auth-token') || sessionStorage.getItem('token') || ''; } catch { return ''; } })()");
                token = JsonSerializer.Deserialize<string>(result);
            }
            if (_disposed) return;
            var profile = await _authManager.TryExtractFromWebViewAsync(core.CookieManager, token);
            if (_disposed || profile?.IsValid != true) return;
            var valid = await _apiClient.ValidateAccountAsync(profile);
            if (_disposed) return;
            if (valid != true)
            {
                if (IsUserPage(core.Source))
                {
                    LoginStatus.Text = valid == false ? "登录凭证尚未生效，请重试登录。" : "暂时无法连接云课件，正在重试。";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
                return;
            }
            profile.IsExpired = false;
            _authManager.Save(profile);
            _completed = true;
            var success = LoginSuccess;
            Dispose();
            success?.Invoke(profile);
        }
        catch (Exception ex)
        {
            if (!_disposed)
            {
                LoginStatus.Text = $"获取登录状态失败：{ex.Message}。可刷新重试。";
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }
        finally { _checking = false; _nextCheck = DateTime.UtcNow.AddSeconds(3); }
    }

    private void OnRefreshLoginClick(object sender, RoutedEventArgs e)
    {
        if (!_disposed && LoginWebView.CoreWebView2 != null) LoginWebView.CoreWebView2.Reload();
    }
    private async void OnClearAndReloadClick(object sender, RoutedEventArgs e)
    {
        if (_disposed || _checking || LoginWebView.CoreWebView2 == null) return;
        try
        {
            await LoginWebView.CoreWebView2.Profile.ClearBrowsingDataAsync();
            if (!_disposed) LoginWebView.CoreWebView2.Navigate("https://id.seewo.com/login");
        }
        catch (Exception ex) { if (!_disposed) LoginStatus.Text = $"重置失败：{ex.Message}"; }
    }
    private void OnCancelClick(object sender, RoutedEventArgs e) => CancelRequested?.Invoke();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnPoll;
        Loaded -= OnLoaded;
        if (LoginWebView.CoreWebView2 is { } core)
        {
            core.SourceChanged -= OnSourceChanged;
            core.NavigationStarting -= OnNavigationStarting;
            core.NavigationCompleted -= OnNavigationCompleted;
        }
        LoginWebView.Dispose();
    }
}
