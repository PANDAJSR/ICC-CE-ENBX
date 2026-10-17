using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;

namespace ICC.CE.ENBX.Seewo.UI;

public partial class SeewoBrowserControl : UserControl
{
    private readonly SeewoApiClient _apiClient;
    private readonly SeewoDownloader _downloader;

    private List<SeewoCoursewareItem> _allCoursewareAndGroups = new();
    private List<SeewoCoursewareItem> _recentCoursewares = new();
    private readonly Stack<(string id, string name)> _folderStack = new();
    private CancellationTokenSource? _downloadCts;
    private bool _isLoading;
    private readonly HashSet<string> _downloadingIds = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? PlayRequested;

    public SeewoBrowserControl(SeewoApiClient apiClient, SeewoDownloader downloader)
    {
        InitializeComponent();
        _apiClient = apiClient;
        _downloader = downloader;
        _folderStack.Push(("", "全部课件"));
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    public async Task RefreshAsync()
    {
        await LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        if (_isLoading) return;
        _isLoading = true;
        LoadingPanel.Visibility = Visibility.Visible;
        try
        {
            var groupsTask = _apiClient.GetAllGroupsAndCoursewaresAsync();
            var recentTask = _apiClient.GetRecentListAsync(100);

            await Task.WhenAll(groupsTask, recentTask);
            _allCoursewareAndGroups = await groupsTask;
            _recentCoursewares = await recentTask;

            UpdateSummaryText();
            ApplyFilterAndRender();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"拉取希沃云课件失败: {ex.Message}", "希沃云课件", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            LoadingPanel.Visibility = Visibility.Collapsed;
            _isLoading = false;
        }
    }

    private void UpdateSummaryText()
    {
        var cwCount = _allCoursewareAndGroups.Count(x => !x.IsGroup);
        var groupCount = _allCoursewareAndGroups.Count(x => x.IsGroup);
        TextSummary.Text = $"共 {cwCount} 份课件，{groupCount} 个文件夹";
    }

    private void ApplyFilterAndRender()
    {
        UpdateBreadcrumbs();

        IEnumerable<SeewoCoursewareItem> itemsToDisplay;

        var searchText = SearchBox.Text.Trim();

        if (!string.IsNullOrEmpty(searchText))
        {
            // 全局搜索模式
            itemsToDisplay = _allCoursewareAndGroups
                .Where(x => x.Name.Contains(searchText, StringComparison.OrdinalIgnoreCase));
        }
        else if (TabRecentCoursewares.IsChecked == true)
        {
            itemsToDisplay = _recentCoursewares;
        }
        else if (TabDownloadedCoursewares.IsChecked == true)
        {
            // 本地已下载缓存课件
            itemsToDisplay = _allCoursewareAndGroups
                .Where(x => !x.IsGroup && _downloader.IsCached(x.Id, x.Version, x.Name));
        }
        else
        {
            // 目录树模式：展示当前 ParentId 下的子项
            var currentFolderId = _folderStack.Peek().id;
            bool isRoot = string.IsNullOrEmpty(currentFolderId);
            var existingGroupIds = new HashSet<string>(_allCoursewareAndGroups.Where(g => g.IsGroup).Select(g => g.Id), StringComparer.OrdinalIgnoreCase);

            itemsToDisplay = _allCoursewareAndGroups
                .Where(x =>
                {
                    if (isRoot)
                    {
                        // 根目录：ParentId 为空/0/-1，或者指向一个不存在的分组
                        return string.IsNullOrEmpty(x.ParentId) || x.ParentId == "0" || x.ParentId == "-1" || !existingGroupIds.Contains(x.ParentId);
                    }
                    return string.Equals(x.ParentId, currentFolderId, StringComparison.OrdinalIgnoreCase);
                })
                .OrderByDescending(x => x.IsGroup)
                .ThenByDescending(x => x.UpdateTime);
        }

        var list = itemsToDisplay.ToList();
        foreach (var item in list)
        {
            item.IsCached = !item.IsGroup && _downloader.IsCached(item.Id, item.Version, item.Name);
        }
        CoursewareListView.ItemsSource = list;
        EmptyStatePanel.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateBreadcrumbs()
    {
        BreadcrumbPanel.Children.Clear();
        var array = _folderStack.Reverse().ToArray();
        BtnBack.IsEnabled = array.Length > 1;

        for (int i = 0; i < array.Length; i++)
        {
            var folder = array[i];
            var btn = new Button
            {
                Content = folder.name,
                Tag = folder.id,
                Padding = new Thickness(6, 2, 6, 2),
                Margin = new Thickness(0, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontWeight = (i == array.Length - 1) ? FontWeights.SemiBold : FontWeights.Normal
            };

            int targetIndex = i;
            btn.Click += (_, _) =>
            {
                while (_folderStack.Count > targetIndex + 1)
                {
                    _folderStack.Pop();
                }
                ApplyFilterAndRender();
            };

            BreadcrumbPanel.Children.Add(btn);

            if (i < array.Length - 1)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "/",
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(2, 0, 6, 0)
                });
            }
        }
    }

    private void OnBackFolderClick(object sender, RoutedEventArgs e)
    {
        if (_folderStack.Count > 1)
        {
            _folderStack.Pop();
            ApplyFilterAndRender();
        }
    }

    private void OnEnterFolderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SeewoCoursewareItem item } && item.IsGroup)
        {
            EnterFolder(item);
        }
    }

    private void OnListItemMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem { Content: SeewoCoursewareItem item })
        {
            if (item.IsGroup)
            {
                EnterFolder(item);
            }
            else
            {
                StartPlayOrDownload(item, autoPlay: true);
            }
            e.Handled = true;
        }
    }

    private void OnListViewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && CoursewareListView.SelectedItem is SeewoCoursewareItem item)
        {
            if (item.IsGroup)
            {
                EnterFolder(item);
            }
            else
            {
                StartPlayOrDownload(item, autoPlay: true);
            }
            e.Handled = true;
        }
    }

    private void EnterFolder(SeewoCoursewareItem item)
    {
        _folderStack.Push((item.Id, item.Name));
        SearchBox.Text = string.Empty;
        ApplyFilterAndRender();
    }

    private void OnPlayItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SeewoCoursewareItem item })
        {
            StartPlayOrDownload(item, autoPlay: true);
        }
    }

    private void OnDownloadOnlyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SeewoCoursewareItem item })
        {
            StartPlayOrDownload(item, autoPlay: false);
        }
    }

    private async void StartPlayOrDownload(SeewoCoursewareItem item, bool autoPlay)
    {
        if (item.IsGroup) return;

        // 若已在下载队列中，避免重复触发
        if (_downloadingIds.Contains(item.Id))
        {
            return;
        }

        // 若已缓存，直接秒开
        if (_downloader.IsCached(item.Id, item.Version, item.Name))
        {
            var cachedPath = _downloader.GetLocalCachedPath(item.Id, item.Version, item.Name);
            if (autoPlay)
            {
                PlayRequested?.Invoke(cachedPath);
            }
            else
            {
                MessageBox.Show("该课件已下载在本地缓存中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        // 开始下载
        _downloadingIds.Add(item.Id);
        DownloadBar.Visibility = Visibility.Visible;
        _downloadCts = new CancellationTokenSource();

        var progress = new Progress<DownloadProgressInfo>(info =>
        {
            TextDownloadStatus.Text = $"{info.StatusMessage} ({info.CompletedFileCount}/{info.TotalFileCount})";
            TextDownloadPercent.Text = $"{info.ProgressPercentage:0}%";
            ProgressDownload.Value = info.ProgressPercentage;
        });

        try
        {
            var enbxPath = await _downloader.DownloadAndPackageAsync(item, progress, _downloadCts.Token);
            DownloadBar.Visibility = Visibility.Collapsed;

            // 标记已缓存并刷新列表状态
            item.IsCached = true;
            ApplyFilterAndRender();

            if (autoPlay)
            {
                PlayRequested?.Invoke(enbxPath);
            }
            else
            {
                MessageBox.Show($"课件《{item.Name}》下载完成！已打包为 .enbx 格式。", "下载成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException)
        {
            TextDownloadStatus.Text = "下载已取消";
            await Task.Delay(1000);
            DownloadBar.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _apiClient.Log($"Download error: {ex}");
            MessageBox.Show($"下载课件失败: {ex.Message}", "下载错误", MessageBoxButton.OK, MessageBoxImage.Error);
            DownloadBar.Visibility = Visibility.Collapsed;
        }
        finally
        {
            _downloadingIds.Remove(item.Id);
        }
    }

    private void OnCancelDownloadClick(object sender, RoutedEventArgs e)
    {
        _downloadCts?.Cancel();
    }

    private void OnTabSelectionChanged(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        ApplyFilterAndRender();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyFilterAndRender();
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await LoadDataAsync();
    }

    private void OnOpenCacheFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (Directory.Exists(_downloader.CacheDirectory))
            {
                Process.Start("explorer.exe", _downloader.CacheDirectory);
            }
        }
        catch
        {
        }
    }
}
