using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;
using Ink_Canvas.Plugins;

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
    private int _viewVersion;

    public SeewoCloudWindow(
        Plugin plugin,
        SeewoAuthManager authManager,
        SeewoApiClient apiClient,
        SeewoDownloader downloader,
        IThemeService? themeService = null)
    {
        InitializeComponent();
        _plugin = plugin;
        _authManager = authManager;
        _apiClient = apiClient;
        _downloader = downloader;

        // 使用宿主配置的主题，避免继承白板操作遗留的应用级主题。
        themeService?.ApplyThemeToElement(this);
        Activated += (_, _) => themeService?.ApplyThemeToElement(this);
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
        _browserControl.UseCloudLazyLoading = () => _plugin.Settings.CloudLazyLoadingEnabled;
        _browserControl.CloudPlayRequested = async item =>
        {
            await _plugin.OpenCloudCoursewareAsync(item);
            if (!_closed) WindowState = WindowState.Minimized;
        };
        _browserControl.PlayRequested += OnPlayRequested;
        MainContentContainer.Children.Add(_browserControl);
    }

    private void ClearContent()
    {
        _viewVersion++;
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
        foreach (var account in OrderedAccounts())
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

    private void ShowAccountManager() => ShowAccountManager(true);

    private void ShowAccountManager(bool refreshProfiles)
    {
        if (_closed) return;
        ClearContent();
        UpdateUserUi();
        var panel = new StackPanel { Width = 440, Margin = new Thickness(24, 40, 24, 40), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = "希沃账号", FontSize = 28, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 24) });
        foreach (var account in OrderedAccounts().ToList())
        {
            var card = new Grid { Margin = new Thickness(0, 0, 0, 24) };
            card.ColumnDefinitions.Add(new ColumnDefinition());
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            card.Children.Add(CreateAccountIdentity(account, 52));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var enterContent = new StackPanel { Orientation = Orientation.Horizontal };
            enterContent.Children.Add(new TextBlock { Text = "进入", VerticalAlignment = VerticalAlignment.Center });
            enterContent.Children.Add(CreateIcon("M2,8 L14,8 M9,3 L14,8 L9,13", 16));
            var enter = new Button { Content = enterContent, Padding = new Thickness(12, 7, 10, 7) };
            if (ReferenceEquals(account, _authManager.CurrentProfile)) enter.SetResourceReference(StyleProperty, "AccentButtonStyle");
            enter.Click += async (_, _) => await SwitchAccountAsync(account);
            actions.Children.Add(enter);
            var chevron = CreateIcon("M3,5 L8,10 L13,5", 12);
            chevron.Margin = new Thickness(0);
            var more = new Button { Content = chevron, Padding = new Thickness(8, 9, 8, 9), Margin = new Thickness(2, 0, 0, 0), ToolTip = "账号选项" };
            more.Click += (_, _) => ShowAccountActions(account, more);
            actions.Children.Add(more);
            Grid.SetColumn(actions, 1);
            card.Children.Add(actions);
            panel.Children.Add(card);
        }
        var addButton = new Button { Content = "＋ 添加希沃账号", HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(20, 10, 20, 10), Margin = new Thickness(0, 12, 0, 0) };
        addButton.Click += (_, _) => ShowLoginView();
        panel.Children.Add(addButton);
        MainContentContainer.Children.Add(new ScrollViewer { Content = panel, VerticalContentAlignment = VerticalAlignment.Center, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        if (refreshProfiles) _ = RefreshAccountListProfilesAsync(_viewVersion);
    }

    private async Task RefreshAccountListProfilesAsync(int version)
    {
        var changed = false;
        foreach (var account in OrderedAccounts().ToList())
        {
            if (_closed || version != _viewVersion) return;
            try
            {
                if (await _apiClient.FetchUserIdentityAsync(account))
                {
                    if (_closed || version != _viewVersion) return;
                    _authManager.UpdateProfile(account);
                    changed = true;
                }
            }
            catch (Exception ex) { _apiClient.Log($"刷新账号资料失败：{ex.GetType().Name}"); }
        }
        if (changed && !_closed && version == _viewVersion) ShowAccountManager(false);
    }

    private IEnumerable<SeewoAuthProfile> OrderedAccounts() => _authManager.Accounts
        .OrderByDescending(x => ReferenceEquals(x, _authManager.CurrentProfile))
        .ThenByDescending(x => x.UseCount).ThenByDescending(x => x.LastUsedTime);

    private static FrameworkElement CreateIcon(string geometry, double size)
    {
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), StrokeThickness = 1.5, Width = size, Height = size, Stretch = Stretch.Uniform, Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        return icon;
    }

    private void ShowAccountActions(SeewoAuthProfile account, Button target)
    {
        var menu = new ContextMenu { PlacementTarget = target };
        void Add(string title, Action action)
        {
            var item = new MenuItem { Header = title };
            item.Click += (_, _) =>
            {
                try { action(); }
                catch (Exception ex) { _apiClient.Log($"Account action failed: {ex}"); MessageBox.Show(this, ex.Message, "账号操作失败"); }
            };
            menu.Items.Add(item);
        }
        Add("修改备注", () => EditRemark(account));
        Add("重新登录", ShowLoginView);
        if (ReferenceEquals(account, _authManager.CurrentProfile)) Add("退出登录", () => OnLogoutClick(target, new RoutedEventArgs()));
        Add("从此设备移除", () =>
        {
            if (MessageBox.Show(this, $"移除“{account.DisplayName}”的本机登录信息？不会删除云端课件。", "移除账号", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            _authManager.Remove(account);
            ShowAccountManager();
        });
        menu.IsOpen = true;
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
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = size >= 40 ? 230 : 260, Margin = new Thickness(0, 0, 12, 0) };
        text.Children.Add(new TextBlock
        {
            Text = account.DisplayName + (ReferenceEquals(account, _authManager.CurrentProfile) ? " · 当前" : ""),
            FontSize = size >= 40 ? 20 : 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
        });
        var detail = new TextBlock { Text = $"{(string.IsNullOrWhiteSpace(account.StageName) ? "其它" : account.StageName)} · {(string.IsNullOrWhiteSpace(account.SubjectName) ? "其它" : account.SubjectName)}" + (account.IsExpired ? " · 登录已过期" : ""), Margin = new Thickness(0, 3, 0, 0), FontSize = 12 };
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
