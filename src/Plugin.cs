using System.Globalization;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ink_Canvas.Plugins;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;
using ICC.CE.ENBX.Seewo.UI;

namespace ICC.CE.ENBX;

[PluginEntrance]
public sealed partial class Plugin : PluginBase
{
    public const string PluginId = "com.pandajsr.icc-ce-enbx";

    private IPluginHost? _host;
    private IFileDialogService? _fileDialog;
    private IPresentationSourceService? _presentation;
    private IWindowService? _window;
    private EnbxWebViewLayer? _webView;
    private EnbxPresentationWindow? _presentationWindow;
    private ToolsMenuBridge? _toolsMenu;
    private readonly Dictionary<string, TaskCompletionSource<int>> _navigationRequests = new();
    private readonly object _navigationLock = new();
    private bool _presentationActive;
    private int _activePageCount;
    private int _currentPresentationPage;
    private bool _wasFullscreen;
    private int _ending;
    private FrameworkElement? _hostBackgroundCover;
    private Visibility _savedBackgroundCoverVisibility;
    private bool _savedTopMost;
    private bool _hostOverlayApplied;
    private SeewoAuthManager? _seewoAuthManager;
    private SeewoApiClient? _seewoApiClient;
    private SeewoDownloader? _seewoDownloader;
    private SeewoCloudWindow? _seewoCloudWindow;

    public override string Id => PluginId;
    public override string Name => "希沃白板播放器";
    public override string Version => "0.1.0";
    public override string Description => "Play ENBX courseware and import MAXHUB MHB whiteboard files in ICC-CE.";
    public override string Author => "PANDA-JSR";

    private PluginSettings? _settings;
    private PluginSettingsControl? _settingsControl;

    public PluginSettings Settings => _settings ??= PluginSettings.Load(PluginConfigFolder);

    public override void Initialize(IPluginHost host)
    {
        base.Initialize(host);
        _host = host;
        _fileDialog = host.GetService<IFileDialogService>();
        _presentation = host.GetService<IPresentationSourceService>();
        _window = host.GetService<IWindowService>();

        if (_presentation == null || _fileDialog == null)
            throw new InvalidOperationException("The host does not provide the ENBX presentation services.");

        _settings = PluginSettings.Load(PluginConfigFolder);
        _webView = new EnbxWebViewLayer();
        _webView.CurrentSettings = _settings;
        _webView.MessageReceived += OnWebMessageReceived;
        _webView.LoadFailed += OnWebViewLoadFailed;
        _webView.Diagnostic += message => _host?.Log("[WebView] " + message);
        _presentationWindow = new EnbxPresentationWindow(_webView);
        _presentationWindow.CloseRequested += (s, e) => _ = EndPresentationAsync();
        _presentationWindow.PageClickRequested += OnPresentationPageClickRequested;

        _presentation.Ended += OnPresentationEnded;
        _toolsMenu = new ToolsMenuBridge(this);
        _toolsMenu.Start();
        _host.Log("ENBX plugin services initialized.");

        var uriService = host.GetService<IPluginUriService>();
        uriService?.RegisterHandler("open", request =>
        {
            if (!request.Query.TryGetValue("path", out var path) || string.IsNullOrWhiteSpace(path))
                return false;
            _ = OpenFileAsync(path);
            return true;
        });
        uriService?.RegisterHandler("seewo", _ =>
        {
            OpenSeewoCloudWindow();
            return true;
        });

        // 初始化希沃云课件服务与工具栏组件
        _seewoAuthManager = new SeewoAuthManager(PluginConfigFolder);
        _webView.SetBrowserAuthManager(_seewoAuthManager);
        var logFilePath = Path.Combine(PluginConfigFolder, "seewo_cloud.log");
        _seewoApiClient = new SeewoApiClient(_seewoAuthManager, msg =>
        {
            try
            {
                _host?.Log("[Seewo] " + msg);
                File.AppendAllText(logFilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        });
        _seewoDownloader = new SeewoDownloader(_seewoApiClient, PluginConfigFolder);
        RegisterSeewoToolbarItem(host);

        host.Log("ENBX presentation plugin initialized.");
    }

    public override object? GetMainView() => null;

    public override object? GetSettingsView() => _settingsControl ??= new PluginSettingsControl(this);

    public void SaveSettings()
    {
        _settings?.Save(PluginConfigFolder);
        if (_settings != null && _webView != null)
        {
            _webView.CurrentSettings = _settings;
            _webView.UpdateSettings(_settings);
        }
    }

    public override void Shutdown()
    {
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            await EndPresentationAsync();
            _toolsMenu?.Dispose();
            if (_presentation != null) _presentation.Ended -= OnPresentationEnded;
            _webView?.Dispose();
            _presentationWindow?.Dispose();
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                try { _seewoCloudWindow?.Close(); } catch { }
                _seewoCloudWindow = null;
            });
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to shut down the ENBX presentation plugin.", ex);
        }
    }

    public void OpenSeewoCloudWindow()
    {
        Application.Current?.Dispatcher?.Invoke(() =>
        {
            if (_seewoAuthManager == null || _seewoApiClient == null || _seewoDownloader == null)
                return;

            if (_seewoCloudWindow == null || !_seewoCloudWindow.IsLoaded)
            {
                _seewoCloudWindow = new SeewoCloudWindow(this, _seewoAuthManager, _seewoApiClient, _seewoDownloader,
                    _host?.GetService<IThemeService>());
                // 所属窗口始终在宿主上方，避免播放窗口定时锚定层级时盖住云课件。
                if (Application.Current?.MainWindow is { IsLoaded: true } owner)
                    _seewoCloudWindow.Owner = owner;
                _seewoCloudWindow.Closed += (_, _) => _seewoCloudWindow = null;
                _seewoCloudWindow.Show();
            }
            else
            {
                if (_seewoCloudWindow.WindowState == WindowState.Minimized)
                    _seewoCloudWindow.WindowState = WindowState.Normal;
                _seewoCloudWindow.Activate();
                _seewoCloudWindow.Focus();
            }
        });
    }

    private void RegisterSeewoToolbarItem(IPluginHost host)
    {
        try
        {
            // 预先写入 .toolbar_registered 标记，确保宿主判定为非首次安装，绝不自动追加到工具栏，满足用户“手动添加”需求
            try
            {
                var folder = !string.IsNullOrEmpty(PluginFolder) ? PluginFolder : PluginConfigFolder;
                var markerPath = Path.Combine(folder, ".toolbar_registered");
                if (!File.Exists(markerPath))
                {
                    File.WriteAllText(markerPath, "");
                }
            }
            catch { }

            // 注册浮动工具栏项（需用户手动添加）
            var floatingItem = new PluginToolbarItemInfo
            {
                Id = "com.pandajsr.icc-ce-enbx.seewo",
                DisplayName = "希沃",
                Description = "浏览并下载播放希沃云课件",
                Surface = PluginToolbarSurface.Floating,
                ViewFactory = () => CreateSeewoToolbarButton(isBoard: false)
            };
            SetAutoAddToActiveConfig(floatingItem, false);
            host.RegisterToolbarItem(floatingItem);

            // 注册白板工具栏项（需用户手动添加）
            var boardItem = new PluginToolbarItemInfo
            {
                Id = "com.pandajsr.icc-ce-enbx.seewo.board",
                DisplayName = "希沃",
                Description = "浏览并下载播放希沃云课件",
                Surface = PluginToolbarSurface.Whiteboard,
                ViewFactory = () => CreateSeewoToolbarButton(isBoard: true)
            };
            SetAutoAddToActiveConfig(boardItem, false);
            host.RegisterBoardToolbarItem(boardItem);

            host.Log("ENBX 希沃工具栏组件已成功注册（需用户手动添加）。");
        }
        catch (Exception ex)
        {
            host.LogError("注册希沃工具栏组件失败", ex);
        }
    }

    private static void SetAutoAddToActiveConfig(PluginToolbarItemInfo itemInfo, bool value)
    {
        try
        {
            var prop = itemInfo.GetType().GetProperty("AutoAddToActiveConfig");
            prop?.SetValue(itemInfo, value);
        }
        catch { }
    }

    private FrameworkElement CreateSeewoToolbarButton(bool isBoard)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Margin = isBoard ? new Thickness(0) : new Thickness(0, 2, 0, 7),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "希沃云课件"
        };

        var grid = new Grid
        {
            Width = isBoard ? 60 : 44,
            Height = isBoard ? 50 : 43,
            Background = Brushes.Transparent
        };

        var hoverOverlay = new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            IsHitTestVisible = false
        };

        var contentGrid = new Grid
        {
            Margin = isBoard ? new Thickness(0, 6, 0, 4) : new Thickness(0),
            // Match the host's unselected floating toolbar button content offset.
            RenderTransform = isBoard ? Transform.Identity : new TranslateTransform(0, 2)
        };

        var img = new Image
        {
            Width = isBoard ? 20 : 24,
            Height = isBoard ? 20 : 24,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = isBoard ? new Thickness(0) : new Thickness(0, 1, 0, 0),
            // Compensate for the PNG's transparent top padding without moving the label.
            RenderTransform = new TranslateTransform(0, isBoard ? -2 : -3),
            Stretch = Stretch.Uniform
        };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        img.Source = LoadSeewoIconBitmap();

        var label = new TextBlock
        {
            Text = "希沃",
            FontSize = isBoard ? 12 : 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = isBoard ? new Thickness(0) : new Thickness(0, 0, 0, 2),
            TextAlignment = TextAlignment.Center
        };

        try
        {
            label.SetResourceReference(TextBlock.ForegroundProperty, isBoard ? "FloatingBarForegroundBrush" : "FloatBarForeground");
        }
        catch
        {
        }

        contentGrid.Children.Add(img);
        contentGrid.Children.Add(label);

        grid.Children.Add(hoverOverlay);
        grid.Children.Add(contentGrid);
        border.Child = grid;

        bool isPressed = false;

        border.MouseLeave += (_, _) =>
        {
            isPressed = false;
            hoverOverlay.Background = Brushes.Transparent;
        };

        border.MouseLeftButtonDown += (_, e) =>
        {
            isPressed = true;
            hoverOverlay.Background = new SolidColorBrush(Color.FromArgb(48, 128, 128, 128));
            e.Handled = true;
        };

        border.MouseLeftButtonUp += (_, e) =>
        {
            if (isPressed)
            {
                isPressed = false;
                hoverOverlay.Background = Brushes.Transparent;
                OpenSeewoCloudWindow();
                e.Handled = true;
            }
        };

        // The host resets the root Margin when arranging toolbar components.
        // Keep the button spacing inside a wrapper, as ToolbarImageButton does.
        return new UserControl { Content = border };
    }

    private ImageSource? LoadSeewoIconBitmap()
    {
        try
        {
            // 优先从嵌入资源/Pack URI 加载
            var uri = new Uri("pack://application:,,,/ICC.CE.ENBX;component/Resources/seewo_icon.png", UriKind.Absolute);
            return new BitmapImage(uri);
        }
        catch
        {
            try
            {
                var searchPaths = new[]
                {
                    Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "", "Resources", "seewo_icon.png"),
                    Path.Combine(PluginFolder ?? "", "Resources", "seewo_icon.png"),
                    Path.Combine(PluginConfigFolder ?? "", "Resources", "seewo_icon.png")
                };

                foreach (var path in searchPaths)
                {
                    if (File.Exists(path))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.UriSource = new Uri(path, UriKind.Absolute);
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch
            {
            }
        }
        return null;
    }

    internal async Task OpenFromMenuAsync()
        => await OpenMixedFileDialogAsync();

    internal void LogBridge(string message)
        => _host?.Log(message);

    private async Task OpenFromDialogAsync()
        => await OpenMixedFileDialogAsync();

    private async Task OpenMixedFileDialogAsync()
    {
        try
        {
            var isZh = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            var title = isZh ? "打开课件或墨迹文件" : "Open ICC-CE or ENBX file";
            var filter = isZh
                ? "所有支持的文件 (*.enbx;*.mhb;*.uink;*.zip;*.xml;*.icstk)|*.enbx;*.mhb;*.uink;*.zip;*.xml;*.icstk|ENBX 课件 (*.enbx)|*.enbx|MAXHUB 白板文件 (*.mhb)|*.mhb|墨迹文件 (*.uink;*.zip;*.xml;*.icstk)|*.uink;*.zip;*.xml;*.icstk|所有文件 (*.*)|*.*"
                : "All Supported Files (*.enbx;*.mhb;*.uink;*.zip;*.xml;*.icstk)|*.enbx;*.mhb;*.uink;*.zip;*.xml;*.icstk|ENBX courseware (*.enbx)|*.enbx|MAXHUB Whiteboard (*.mhb)|*.mhb|Ink files (*.uink;*.zip;*.xml;*.icstk)|*.uink;*.zip;*.xml;*.icstk|All files (*.*)|*.*";

            var path = _fileDialog?.OpenFile(title, filter);
            if (string.IsNullOrWhiteSpace(path)) return;

            if (string.Equals(Path.GetExtension(path), ".enbx", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetExtension(path), ".mhb", StringComparison.OrdinalIgnoreCase))
            {
                await OpenFileAsync(path);
                return;
            }

            if (_presentationActive) await EndPresentationAsync();
            OpenHostStrokeFile(path);
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to open ICC-CE or ENBX file.", ex);
        }
    }

    private static void OpenHostStrokeFile(string path)
    {
        var mainWindow = GetHostMainWindow();
        if (mainWindow == null) throw new InvalidOperationException("ICC-CE main window is unavailable.");

        var methodName = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".uink" => "OpenUInkFile",
            ".zip" => "OpenICCZipFile",
            ".xml" => "OpenXMLStrokeFile",
            _ => "OpenSingleStrokeFile"
        };
        var method = mainWindow.GetType().GetMethod(
            methodName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(mainWindow.GetType().FullName, methodName);
        method.Invoke(mainWindow, new object[] { path });

        try
        {
            var inkCanvasProperty = mainWindow.GetType().GetProperty("inkCanvas", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?? (object?)mainWindow.GetType().GetField("inkCanvas", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var inkCanvas = inkCanvasProperty is PropertyInfo pi ? pi.GetValue(mainWindow) as UIElement : (inkCanvasProperty as FieldInfo)?.GetValue(mainWindow) as UIElement;
            if (inkCanvas != null && inkCanvas.Visibility != Visibility.Visible)
            {
                var cursorMethod = mainWindow.GetType().GetMethod("SymbolIconCursor_Click", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                cursorMethod?.Invoke(mainWindow, new object?[] { null, null });
            }
        }
        catch
        {
            // Best effort switch to cursor mode
        }
    }

    public async Task OpenFileAsync(string path)
    {
        if (!File.Exists(path))
        {
            _host?.Log($"File does not exist: {path}");
            return;
        }

        var ext = Path.GetExtension(path);
        if (string.Equals(ext, ".mhb", StringComparison.OrdinalIgnoreCase))
        {
            if (_presentationActive) await EndPresentationAsync();
            _host?.Log($"Opening MHB natively into whiteboard: {path}");
            var mainWindow = GetHostMainWindow();
            if (mainWindow != null)
            {
                MhbWhiteboardImporter.ImportToWhiteboard(path, mainWindow, message => _host?.Log(message));
            }
            return;
        }

        if (!string.Equals(ext, ".enbx", StringComparison.OrdinalIgnoreCase))
        {
            _host?.Log($"Ignoring unsupported file: {path}");
            return;
        }

        if (_presentationActive) await EndPresentationAsync();
        _host?.Log($"Opening ENBX: {path}");
        ShowPresentationWindow();
        _webView?.OpenDocument(path);
    }

    public async Task OpenCloudCoursewareAsync(SeewoCoursewareItem item)
    {
        if (_seewoApiClient == null || _seewoDownloader == null || _webView == null) return;
        var session = await CloudCoursewareSession.CreateAsync(_seewoApiClient, item.Id, _seewoDownloader.CacheDirectory);
        try
        {
            await EndPresentationAsync();
            ShowPresentationWindow();
            _webView.OpenCloudDocument(session);
        }
        catch { session.Dispose(); throw; }
    }

    /// <summary>显示承载网页的独立窗口；必须先于 WebView2 初始化，否则控件无法创建。</summary>
    private void ShowPresentationWindow()
    {
        var mainWindow = GetHostMainWindow();
        if (mainWindow == null)
        {
            _host?.Log("Host main window was not found; ENBX output window cannot be shown.");
            return;
        }

        _presentationWindow?.ShowFor(mainWindow);
        SetHostOverlay(true);
        _host?.Log("ENBX output window shown behind the ICC-CE window.");
    }

    private static Window? GetHostMainWindow()
        => Application.Current?.MainWindow
            ?? Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsVisible);

    private async void OnWebMessageReceived(EnbxWebMessage message)
    {
        if (message.Type.StartsWith("enbx:annotation-", StringComparison.Ordinal))
        {
            OnAnnotationMessage(message);
            return;
        }
        // 云会话关闭或替换后，忽略浏览器中迟到的页码与完成通知。
        if (message.SessionId.Length > 0 && message.SessionId != _webView?.CloudSessionId) return;
        try
        {
            if (message.Type != "enbx:frontend-log")
            {
                _host?.Log($"WebView message: {message.Type} page={message.Page} pages={message.PageCount} request={message.RequestId}");
            }
            switch (message.Type)
            {
                case "enbx:request-open":
                    await OpenFromDialogAsync();
                    break;
                case "enbx:document-loaded":
                    await BeginPresentationAsync(message.PageCount, message.CurrentPage);
                    break;
                case "enbx:navigation-complete":
                    CompleteNavigation(message.RequestId, message.Page);
                    break;
                case "enbx:page-changed":
                    if (_presentationActive && message.Page > 0)
                    {
                        await _presentation!.UpdatePageAsync(message.Page);
                        SwitchInkPage(message.Page);
                    }
                    break;
                case "enbx:close":
                    await EndPresentationAsync();
                    break;
                case "enbx:error":
                    _host?.Log("WebEasiNote error: " + message.Error);
                    break;
            }
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to process a WebEasiNote message.", ex);
        }
    }

    private async Task BeginPresentationAsync(int pageCount, int currentPage)
    {
        if (pageCount <= 0 || _presentation == null || _webView == null) return;
        if (_presentationActive)
        {
            // 已在放映中：只同步页码，绝不再次 BeginAsync。
            // 再次 BeginAsync 会让宿主结束旧演示源并重建，从而中断放映。
            _activePageCount = pageCount;
            var page = Math.Clamp(currentPage, 1, pageCount);
            _host?.Log($"Presentation already active; syncing only. page={page}, pages={pageCount}");
            await _presentation.UpdatePageAsync(page, pageCount);
            return;
        }

        _host?.Log($"Begin presentation requested: page={currentPage}, pages={pageCount}, active={_presentationActive}");
        _wasFullscreen = _window?.IsFullscreen == true;
        _webView.SetViewerVisible(true);
        var descriptor = new PresentationSourceDescriptor
        {
            Id = PluginId,
            Name = "ENBX Presentation",
            PageCount = pageCount,
            CurrentPage = Math.Clamp(currentPage, 1, pageCount),
            AllowPageNumberClick = true,
            NavigateAsync = NavigateAsync
        };
        ConfigureWebAnnotation(descriptor);

        if (!await _presentation.BeginAsync(descriptor))
        {
            _host?.Log("Begin presentation rejected by host.");
            _webView.SetViewerVisible(false);
            return;
        }

        _presentationActive = true;
        _activePageCount = pageCount;
        _currentPresentationPage = descriptor.CurrentPage;
        _ending = 0;
        _window?.SetFullscreen(true);
        _host?.Log("ENBX presentation active; host PPT controls should now be visible.");
    }

    private async Task<int> NavigateAsync(PresentationNavigation direction, CancellationToken cancellationToken)
    {
        if (!_presentationActive || _webView == null) return 0;

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_navigationLock) _navigationRequests[requestId] = completion;

        try
        {
            _host?.Log($"Host navigation requested: {direction}, request={requestId}");
            _webView.Navigate(direction == PresentationNavigation.Previous ? "previous" : "next", requestId);
            var newPage = await completion.Task.WaitAsync(TimeSpan.FromSeconds(45), cancellationToken);
            if (newPage > 0) SwitchInkPage(newPage);
            return newPage;
        }
        catch (OperationCanceledException)
        {
            _webView?.CancelNavigation(requestId);
            return 0;
        }
        catch (TimeoutException)
        {
            _webView?.CancelNavigation(requestId);
            return 0;
        }
        finally
        {
            lock (_navigationLock) _navigationRequests.Remove(requestId);
        }
    }

    private void CompleteNavigation(string requestId, int page)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        lock (_navigationLock)
        {
            if (_navigationRequests.TryGetValue(requestId, out var completion))
                completion.TrySetResult(page);
        }
    }

    private long _lastToggleTicks;
    private void OnPresentationPageClickRequested(object? sender, string side)
    {
        var now = Environment.TickCount64;
        if (now - _lastToggleTicks < 300) return;
        _lastToggleTicks = now;

        _host?.Log($"Page click requested from {side} nav bar; toggling thumbnails.");
        _webView?.ToggleThumbnails(side);
    }

    /// <summary>同步放映页码；墨迹分页由 Web 管理。</summary>
    private void SwitchInkPage(int page)
    {
        if (page <= 0 || page == _currentPresentationPage) return;
        _currentPresentationPage = page;
    }

    private async Task EndPresentationAsync()
    {
        _webView?.CloseCloudDocument();
        if (Interlocked.Exchange(ref _ending, 1) != 0) return;

        _host?.Log("Ending ENBX presentation.");

        try
        {
            if (_presentation != null)
                await _presentation.EndAsync(PluginId);
        }
        catch (Exception ex)
        {
            _host?.LogError("Failed to leave ENBX presentation mode.", ex);
        }
        finally
        {
            _presentationActive = false;
            _annotationDescriptor = null;
            _annotationState = null;
            _annotationSessionId = "";
            _activePageCount = 0;
            _currentPresentationPage = 0;
            _webView?.SetViewerVisible(false);
            _presentationWindow?.Hide();
            SetHostOverlay(false);
            _window?.SetFullscreen(_wasFullscreen);
            lock (_navigationLock)
            {
                foreach (var request in _navigationRequests.Values)
                    request.TrySetResult(0);
                _navigationRequests.Clear();
            }
            _ending = 0;
        }
    }

    private void OnPresentationEnded(string sourceId)
    {
        _host?.Log($"Host ended presentation source: {sourceId}");
        if (!string.Equals(sourceId, PluginId, StringComparison.OrdinalIgnoreCase)) return;
        _ = EndPresentationAsync();
    }

    private void OnWebViewLoadFailed(string message)
        => _host?.Log("WebEasiNote failed to load: " + message);

    /// <summary>
    /// 让 ICC-CE 主窗口保持“透明标注层”状态，使下方 ENBX 窗口的内容能透出来。
    /// 这正是 ICC-CE 自身标注 PowerPoint 时使用的状态，退出播放时按原值恢复。
    /// </summary>
    private void SetHostOverlay(bool enabled)
    {
        var mainWindow = GetHostMainWindow();
        if (mainWindow == null) return;

        var type = mainWindow.GetType();
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        if (enabled)
        {
            if (_hostOverlayApplied) return;

            // 只隐藏挡住画面的不透明背景遮罩；不去改宿主的透明假背景，
            // 否则会给主窗口加出一层可命中表面，导致工具栏无法点击。
            _hostBackgroundCover = type.GetField("GridBackgroundCover", Flags)?.GetValue(mainWindow) as FrameworkElement;
            if (_hostBackgroundCover != null)
            {
                _savedBackgroundCoverVisibility = _hostBackgroundCover.Visibility;
                _hostBackgroundCover.Visibility = Visibility.Collapsed;
            }

            // 播放期间让 ICC-CE 保持置顶：网页窗口是普通（非置顶）窗口，
            // 这样宿主的工具栏与 PPT 翻页条一定不会被网页窗口遮挡。
            _savedTopMost = _window?.IsTopMost == true;
            _window?.SetTopMost(true);
            EnbxPresentationWindow.ForceTopMost(mainWindow, true);
            try
            {
                var wtmType = mainWindow.GetType().Assembly.GetType("Ink_Canvas.Helpers.WindowTopmostManager");
                var startMaint = wtmType?.GetMethod("StartTopmostMaintenance", BindingFlags.Public | BindingFlags.Static);
                startMaint?.Invoke(null, new object[] { mainWindow });
            }
            catch { }

            _hostOverlayApplied = true;
            _host?.Log(
                "Host canvas switched to transparent annotation mode for ENBX playback. " +
                $"previousCoverVisibility={_savedBackgroundCoverVisibility}, previousTopMost={_savedTopMost}");
            return;
        }

        if (!_hostOverlayApplied) return;

        try
        {
            var wtmType = mainWindow.GetType().Assembly.GetType("Ink_Canvas.Helpers.WindowTopmostManager");
            var stopMaint = wtmType?.GetMethod("StopTopmostMaintenance", BindingFlags.Public | BindingFlags.Static);
            stopMaint?.Invoke(null, null);
        }
        catch { }

        if (_hostBackgroundCover != null)
            _hostBackgroundCover.Visibility = _savedBackgroundCoverVisibility;
        _window?.SetTopMost(_savedTopMost);
        EnbxPresentationWindow.ForceTopMost(mainWindow, _savedTopMost);

        _hostBackgroundCover = null;
        _hostOverlayApplied = false;
        _host?.Log("Host canvas appearance restored.");
    }
}

public sealed class EnbxWebMessage
{
    public int ProtocolVersion { get; set; }
    public bool CanUndo { get; set; }
    public bool CanRedo { get; set; }
    public PresentationAnnotationStatus? State { get; set; }
    public bool Success { get; set; }
    public string SessionId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string Direction { get; set; } = "";
    public string Error { get; set; } = "";
    public int PageCount { get; set; }
    public int CurrentPage { get; set; }
    public int Page { get; set; }
    public string Level { get; set; } = "";
    public string Text { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
    public string Stack { get; set; } = "";
}
