using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media;
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
    private bool _busy;
    private bool _closed;

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
        Closed += (_, _) =>
        {
            _closed = true;
            _loginControl?.Dispose();
            _browserControl?.Detach();
        };
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
            var account = _authManager.CurrentProfile!;
            var valid = await _apiClient.ValidateAccountAsync(account);
            if (_closed) return;
            if (valid == true)
            {
                ShowBrowserView();
                // 后台异步尝试拉取更新真实头像和资料
                _ = RefreshProfileAsync();
                return;
            }
            if (valid == false)
            {
                account.IsExpired = true;
                _authManager.UpdateProfile(account);
            }
        }

        ShowAccountManager();
    }

    private void ShowLoginView()
    {
        ClearContent();
        _loginControl = new SeewoLoginControl(_authManager, _apiClient, _plugin.PluginConfigFolder);
        _loginControl.LoginSuccess += OnLoginSuccess;
        _loginControl.CancelRequested += () =>
        {
            if (_authManager.IsLoggedIn && _authManager.CurrentProfile?.IsExpired == false) ShowBrowserView();
            else ShowAccountManager();
        };
        MainContentContainer.Children.Add(_loginControl);
    }

    private void ShowBrowserView()
    {
        ClearContent();
        UpdateUserUi();

        _browserControl = new SeewoBrowserControl(_apiClient, _downloader);
        _browserControl.PlayRequested += OnPlayRequested;
        MainContentContainer.Children.Add(_browserControl);
    }

    private void ClearContent()
    {
        _loginControl?.Dispose();
        _loginControl = null;
        if (_browserControl != null)
        {
            _browserControl.PlayRequested -= OnPlayRequested;
            _browserControl.Detach();
            _browserControl = null;
        }
        MainContentContainer.Children.Clear();
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
        else UserInfoPanel.Visibility = Visibility.Collapsed;
    }

    private void OnLoginSuccess(SeewoAuthProfile profile)
    {
        ShowBrowserView();
        _ = RefreshProfileAsync();
    }

    private async Task RefreshProfileAsync()
    {
        try
        {
            if (await _apiClient.FetchAndUpdateUserProfileAsync() && !_closed) UpdateUserUi();
        }
        catch (Exception ex) { _apiClient.Log($"保存账号资料失败：{ex.Message}"); }
    }

    private bool CanChangeAccount()
    {
        if (_busy) return false;
        if (_browserControl?.IsDownloading == true)
        {
            MessageBox.Show("下载完成或取消后可切换账号。", "希沃账号", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    private async Task SwitchAccountAsync(SeewoAuthProfile account)
    {
        if (!CanChangeAccount()) return;
        _busy = true;
        ClearContent();
        MainContentContainer.Children.Add(new TextBlock
        {
            Text = $"正在切换到 {account.DisplayName}…", HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, FontSize = 16
        });
        try
        {
            var valid = await _apiClient.ValidateAccountAsync(account);
            if (_closed) return;
            if (valid == true)
            {
                account.IsExpired = false;
                _authManager.Activate(account);
                ShowBrowserView();
                _ = RefreshProfileAsync();
            }
            else if (valid == false)
            {
                account.IsExpired = true;
                _authManager.UpdateProfile(account);
                ShowLoginView();
            }
            else
            {
                MessageBox.Show(_apiClient.LastValidationError ?? "账号校验未完成，请稍后重试。", "无法切换账号", MessageBoxButton.OK, MessageBoxImage.Warning);
                ShowAccountManager();
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _apiClient.Log($"Switch account failed: {ex}");
                ShowAccountManager();
                MessageBox.Show(this, ex.Message, "切换账号失败", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally { _busy = false; }
    }

    private static string AccountDetail(SeewoAuthProfile account)
    {
        var phone = account.Phone;
        if (phone.Length >= 7) phone = phone[..3] + "****" + phone[^4..];
        return (string.IsNullOrWhiteSpace(phone) ? "已保存的希沃账号" : phone) + (account.IsExpired ? " · 登录已过期" : "");
    }

    private void OnAccountMenuClick(object sender, RoutedEventArgs e)
    {
        if (!CanChangeAccount()) return;
        var menu = new ContextMenu { MaxHeight = 480 };
        foreach (var account in _authManager.Accounts.OrderByDescending(x => x.LastUsedTime))
        {
            var item = new MenuItem
            {
                Header = CreateAccountIdentity(account, 32),
                IsCheckable = true, IsChecked = ReferenceEquals(account, _authManager.CurrentProfile)
            };
            item.Click += async (_, _) => await SwitchAccountAsync(account);
            if (ReferenceEquals(account, _authManager.CurrentProfile) && _browserControl != null) item.IsEnabled = false;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var add = new MenuItem { Header = "＋ 添加账号" };
        add.Click += (_, _) => ShowLoginView();
        menu.Items.Add(add);
        var manage = new MenuItem { Header = "管理账号" };
        manage.Click += (_, _) => ShowAccountManager();
        menu.Items.Add(manage);
        var logout = new MenuItem { Header = "退出当前账号（保留登录态）" };
        logout.Click += OnLogoutClick;
        menu.Items.Add(logout);
        menu.PlacementTarget = UserInfoPanel;
        menu.IsOpen = true;
    }

    private void ShowAccountManager()
    {
        if (_closed) return;
        ClearContent();
        UpdateUserUi();
        var panel = new StackPanel { MaxWidth = 680, Margin = new Thickness(28), HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(new TextBlock { Text = "希沃账号", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "选择账号打开云课件，或添加其他账号。登录信息仅保存在此 Windows 用户下。", Margin = new Thickness(0, 8, 0, 20), TextWrapping = TextWrapping.Wrap });
        foreach (var account in _authManager.Accounts.OrderByDescending(x => x.LastUsedTime).ToList())
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            card.Children.Add(CreateAccountIdentity(account, 44));
            var actions = new WrapPanel { Margin = new Thickness(56, 8, 0, 0) };
            void AddAction(string title, Action action)
            {
                var button = new Button { Content = title, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 4) };
                button.Click += (_, _) => action();
                actions.Children.Add(button);
            }
            AddAction("使用此账号", () => { _ = SwitchAccountAsync(account); });
            AddAction("修改备注", () => EditRemark(account));
            AddAction("重新登录", ShowLoginView);
            AddAction("从此设备移除", () =>
            {
                if (MessageBox.Show($"从此设备移除“{account.DisplayName}”的登录信息？不会删除云端课件。", "移除账号", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _authManager.Remove(account);
                ShowAccountManager();
            });
            card.Children.Add(actions);
            panel.Children.Add(card);
        }
        var addButton = new Button { Content = "＋ 添加希沃账号", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(16, 8, 16, 8) };
        addButton.Click += (_, _) => ShowLoginView();
        panel.Children.Add(addButton);
        MainContentContainer.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }

    private FrameworkElement CreateAccountIdentity(SeewoAuthProfile account, double size)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var avatar = new Grid { Width = size, Height = size, Margin = new Thickness(0, 0, 12, 0) };
        avatar.Clip = new EllipseGeometry(new Point(size / 2, size / 2), size / 2, size / 2);
        var placeholder = new Border { Background = new SolidColorBrush(Color.FromRgb(226, 239, 251)) };
        placeholder.Child = new TextBlock
        {
            Text = account.DisplayName.Length > 0 ? account.DisplayName[..1].ToUpperInvariant() : "希",
            Foreground = new SolidColorBrush(Color.FromRgb(0, 105, 180)), FontWeight = FontWeights.SemiBold,
            FontSize = size * 0.42, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        avatar.Children.Add(placeholder);
        if (Uri.TryCreate(account.AvatarUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            try
            {
                var image = new Image { Stretch = Stretch.UniformToFill, Source = new BitmapImage(uri) };
                image.ImageFailed += (_, _) => image.Visibility = Visibility.Collapsed;
                avatar.Children.Add(image);
            }
            catch { /* 保留首字占位头像。 */ }
        }
        row.Children.Add(avatar);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = account.DisplayName + (ReferenceEquals(account, _authManager.CurrentProfile) ? " · 当前" : ""),
            FontSize = size >= 40 ? 16 : 14, FontWeight = FontWeights.SemiBold
        });
        var detail = new TextBlock { Text = AccountDetail(account), Margin = new Thickness(0, 3, 0, 0), FontSize = 12 };
        detail.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        text.Children.Add(detail);
        row.Children.Add(text);
        return row;
    }

    private void EditRemark(SeewoAuthProfile account)
    {
        var dialog = new Window { Title = "修改账号备注", Owner = this, Width = 360, Height = 180, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var input = new TextBox { Text = account.Remark, MaxLength = 40, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(input);
        var save = new Button { Content = "保存", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(20, 6, 20, 6) };
        save.Click += (_, _) => { account.Remark = input.Text.Trim(); _authManager.UpdateProfile(account); dialog.DialogResult = true; };
        panel.Children.Add(save);
        dialog.Content = panel;
        if (dialog.ShowDialog() == true) ShowAccountManager();
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
        if (!CanChangeAccount()) return;
        _authManager.Logout();
        ShowAccountManager();
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
