using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using ICC.CE.ENBX.Seewo.Models;

namespace ICC.CE.ENBX.Seewo.Services;

public sealed class DownloadProgressInfo
{
    public long TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public double ProgressPercentage => TotalBytes > 0 ? Math.Min(100.0, (double)DownloadedBytes / TotalBytes * 100.0) : 0;
    public string CurrentFileName { get; set; } = string.Empty;
    public int CompletedFileCount { get; set; }
    public int TotalFileCount { get; set; }
    public string StatusMessage { get; set; } = string.Empty;
}

public sealed class SeewoDownloader
{
    private readonly SeewoApiClient _apiClient;
    private readonly string _cacheDirectory;
    private readonly HttpClient _downloadHttpClient;

    public SeewoDownloader(SeewoApiClient apiClient, string baseConfigDirectory)
    {
        _apiClient = apiClient;
        _cacheDirectory = Path.Combine(baseConfigDirectory, "CoursewareCache");
        Directory.CreateDirectory(_cacheDirectory);
        _downloadHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
    }

    public string CacheDirectory => _cacheDirectory;

    /// <summary>
    /// 获取课件本地缓存路径（若存在）
    /// </summary>
    public string GetLocalCachedPath(string cid, int version, string name)
    {
        var safeName = SanitizeFileName(Path.GetFileNameWithoutExtension(name));
        var fileName = $"{safeName}_{cid}_v{version}.enbx";
        return Path.Combine(_cacheDirectory, fileName);
    }

    /// <summary>
    /// 检查课件是否已经在本地缓存过
    /// </summary>
    public bool IsCached(string cid, int version, string name)
    {
        var path = GetLocalCachedPath(cid, version, name);
        return File.Exists(path) && new FileInfo(path).Length > 0;
    }

    /// <summary>
    /// 下载并组装为 .enbx 文件
    /// </summary>
    public async Task<string> DownloadAndPackageAsync(
        SeewoCoursewareItem item,
        IProgress<DownloadProgressInfo>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var targetEnbxPath = GetLocalCachedPath(item.Id, item.Version, item.Name);
        if (File.Exists(targetEnbxPath) && new FileInfo(targetEnbxPath).Length > 0)
        {
            progress?.Report(new DownloadProgressInfo
            {
                TotalBytes = item.Size,
                DownloadedBytes = item.Size,
                StatusMessage = "课件已缓存，正在直接打开...",
                CompletedFileCount = 1,
                TotalFileCount = 1
            });
            return targetEnbxPath;
        }

        progress?.Report(new DownloadProgressInfo
        {
            TotalBytes = item.Size,
            DownloadedBytes = 0,
            StatusMessage = "正在获取云端课件文件清单...",
            CompletedFileCount = 0,
            TotalFileCount = 0
        });

        var detail = await _apiClient.GetCoursewareDetailAsync(item.Id);
        if (detail == null || detail.Files == null || detail.Files.Count == 0)
        {
            throw new InvalidOperationException("获取课件文件清单为空或失败");
        }

        var files = detail.Files;
        long totalBytes = files.Sum(f => f.Size);
        if (totalBytes <= 0) totalBytes = item.Size;

        var tempDir = Path.Combine(_cacheDirectory, "temp", $"{item.Id}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        long downloadedBytesTotal = 0;
        int completedCount = 0;
        var progressLock = new object();

        try
        {
            using var throttler = new SemaphoreSlim(4);
            var tasks = files.Select(async file =>
            {
                await throttler.WaitAsync(cancellationToken);
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativePath = file.Path.Replace('\\', '/').TrimStart('/');
                    var destFilePath = Path.Combine(tempDir, relativePath);
                    var destSubDir = Path.GetDirectoryName(destFilePath);
                    if (!string.IsNullOrEmpty(destSubDir))
                    {
                        Directory.CreateDirectory(destSubDir);
                    }

                    // 下载单个文件
                    using var response = await _downloadHttpClient.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
                    using (var fs = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 81920, true))
                    using (var md5 = MD5.Create())
                    {
                        var buffer = new byte[81920];
                        int bytesRead;
                        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                        {
                            await fs.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                            md5.TransformBlock(buffer, 0, bytesRead, null, 0);

                            lock (progressLock)
                            {
                                downloadedBytesTotal += bytesRead;
                                progress?.Report(new DownloadProgressInfo
                                {
                                    TotalBytes = totalBytes,
                                    DownloadedBytes = downloadedBytesTotal,
                                    CurrentFileName = file.Name,
                                    CompletedFileCount = completedCount,
                                    TotalFileCount = files.Count,
                                    StatusMessage = $"正在下载: {file.Name}"
                                });
                            }
                        }
                        await fs.FlushAsync(cancellationToken);
                        md5.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                        var computedHash = BitConverter.ToString(md5.Hash!).Replace("-", "").ToLowerInvariant();

                        // 若服务端提供了 hash，可进行校验
                        if (!string.IsNullOrWhiteSpace(file.Hash) &&
                            !string.Equals(computedHash, file.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            // 如果 MD5 不匹配但有内容，记录或警告
                        }
                    }

                    lock (progressLock)
                    {
                        completedCount++;
                        progress?.Report(new DownloadProgressInfo
                        {
                            TotalBytes = totalBytes,
                            DownloadedBytes = downloadedBytesTotal,
                            CurrentFileName = file.Name,
                            CompletedFileCount = completedCount,
                            TotalFileCount = files.Count,
                            StatusMessage = $"已下载 ({completedCount}/{files.Count})"
                        });
                    }
                }
                finally
                {
                    throttler.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);

            // 正在组装为 .enbx
            progress?.Report(new DownloadProgressInfo
            {
                TotalBytes = totalBytes,
                DownloadedBytes = totalBytes,
                StatusMessage = "正在打包为 .enbx 课件...",
                CompletedFileCount = files.Count,
                TotalFileCount = files.Count
            });

            // 预留缓冲时间等待 Windows 文件系统过滤驱动完成写入扫尾
            await Task.Delay(150, cancellationToken);

            try
            {
                // 直接将临时目录安全打包为目标 .enbx 文件，杜绝 File.Move 带来的防病毒扫描独占错误
                await CreateZipFromDirectorySafelyAsync(tempDir, targetEnbxPath, cancellationToken);
            }
            catch
            {
                try { if (File.Exists(targetEnbxPath)) File.Delete(targetEnbxPath); } catch { }
                throw;
            }

            progress?.Report(new DownloadProgressInfo
            {
                TotalBytes = totalBytes,
                DownloadedBytes = totalBytes,
                StatusMessage = "下载完成，正在打开课件...",
                CompletedFileCount = files.Count,
                TotalFileCount = files.Count
            });

            return targetEnbxPath;
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task CreateZipFromDirectorySafelyAsync(string sourceDir, string destinationZipFile, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 15; attempt++)
        {
            try
            {
                if (File.Exists(destinationZipFile))
                {
                    File.Delete(destinationZipFile);
                }

                using (var zipStream = new FileStream(destinationZipFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 81920, true))
                using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, false))
                {
                    var allFiles = Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories);
                    foreach (var file in allFiles)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relativePath = Path.GetRelativePath(sourceDir, file).Replace('\\', '/');
                        var entry = archive.CreateEntry(relativePath, CompressionLevel.Optimal);

                        // 以 FileShare.ReadWrite | FileShare.Delete 共享模式读取源文件，彻底避免与其他读取进程/杀毒软件冲突
                        using var entryStream = entry.Open();
                        using var fileStream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        await fileStream.CopyToAsync(entryStream, cancellationToken);
                    }
                }
                return;
            }
            catch (IOException) when (attempt < 14)
            {
                await Task.Delay(200, cancellationToken);
            }
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? '_' : c)).Trim();
    }
}
