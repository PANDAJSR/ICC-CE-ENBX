using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;

namespace ICC.CE.ENBX.Seewo.UI;

public partial class SeewoCloudWindow : Window
{
    private readonly Plugin _plugin;
    private readonly SeewoAuthManager _authManager;
    private readonly SeewoApiClient _apiClient;
    private readonly SeewoDownloader _downloader;

    private SeewoLoginControl? _loginControl;
    private SeewoBrowserControl? _browserControl;

    public SeewoCloudWindow(
        Plugin plugin,
        SeewoAuthManager authManager,
        SeewoApiClient apiClient,
        SeewoDownloader downloader)
    {
        InitializeComponent();
        _plugin = plugin;
        _authManager = authManager;
        _apiClient = apiClient;
        _downloader = downloader;

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await SwitchViewBasedOnAuthAsync();
    }

    private async Task SwitchViewBasedOnAuthAsync()
    {
        if (_authManager.IsLoggedIn)
        {
            // 快速校验 Token 有效性
            bool valid = await _apiClient.ValidateTokenAsync();
            if (valid)
            {
                ShowBrowserView();
                // 后台异步尝试拉取更新真实头像和资料
                _ = Task.Run(async () =>
                {
                    if (await _apiClient.FetchAndUpdateUserProfileAsync())
                    {
                        Dispatcher.Invoke(UpdateUserUi);
                    }
                });
                return;
            }
        }

        ShowLoginView();
    }

    private void ShowLoginView()
    {
        UserInfoPanel.Visibility = Visibility.Collapsed;
        _loginControl = new SeewoLoginControl(_authManager, _plugin.PluginConfigFolder);
        _loginControl.LoginSuccess += OnLoginSuccess;
        MainContentContainer.Children.Clear();
        MainContentContainer.Children.Add(_loginControl);
    }

    private void ShowBrowserView()
    {
        UpdateUserUi();

        _browserControl = new SeewoBrowserControl(_apiClient, _downloader);
        _browserControl.PlayRequested += OnPlayRequested;
        MainContentContainer.Children.Clear();
        MainContentContainer.Children.Add(_browserControl);
    }

    private void UpdateUserUi()
    {
        var profile = _authManager.CurrentProfile;
        if (profile != null)
        {
            UserInfoPanel.Visibility = Visibility.Visible;
            TextUserName.Text = profile.DisplayName;

            if (!string.IsNullOrWhiteSpace(profile.AvatarUrl))
            {
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(profile.AvatarUrl, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.EndInit();
                    ImgUserAvatar.Source = bitmap;
                }
                catch
                {
                    ImgUserAvatar.Source = null;
                }
            }
            else
            {
                ImgUserAvatar.Source = null;
            }
        }
    }

    private void OnLoginSuccess(SeewoAuthProfile profile)
    {
        ShowBrowserView();
        _ = Task.Run(async () =>
        {
            if (await _apiClient.FetchAndUpdateUserProfileAsync())
            {
                Dispatcher.Invoke(UpdateUserUi);
            }
        });
    }

    private async void OnPlayRequested(string enbxPath)
    {
        WindowState = WindowState.Minimized;
        try
        {
            await _plugin.OpenFileAsync(enbxPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"播放课件失败: {ex.Message}", "希沃云课件", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnLogoutClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要退出当前的希沃账号吗？", "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            _authManager.Logout();
            ShowLoginView();
        }
    }

    private void OnViewLogsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var logPath = Path.Combine(_plugin.PluginConfigFolder, "seewo_cloud.log");
            if (File.Exists(logPath))
            {
                System.Diagnostics.Process.Start("notepad.exe", logPath);
            }
            else
            {
                MessageBox.Show($"当前日志文件暂无或保存在插件日志中。\n配置目录：{_plugin.PluginConfigFolder}", "诊断日志", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开日志失败: {ex.Message}", "诊断日志", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
