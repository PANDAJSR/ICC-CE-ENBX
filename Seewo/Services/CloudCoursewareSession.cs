using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ICC.CE.ENBX.Seewo.Models;

namespace ICC.CE.ENBX.Seewo.Services;

public sealed class CloudCoursewareSession : IDisposable
{
    private readonly SeewoApiClient _api;
    private readonly Dictionary<string, SeewoFileEntry> _files;
    private readonly string _directory;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly CancellationTokenSource _lifetime = new();
    private readonly PriorityDownloadQueue _queue = new();
    private readonly SemaphoreSlim _refreshGate = new(1);
    private int _urlGeneration;
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Cid { get; }
    public int Version { get; }
    public string Name { get; }

    private CloudCoursewareSession(SeewoApiClient api, SeewoDetailResponse detail, string cacheDirectory)
    {
        _api = api;
        Cid = detail.Cid ?? throw new InvalidDataException("课件详情缺少 ID");
        Version = detail.Version;
        Name = detail.Name ?? "云课件";
        _files = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in detail.Files!)
        {
            file.Path = NormalizePath(file.Path);
            if (!_files.TryAdd(file.Path, file)) throw new InvalidDataException("课件存在重复文件路径");
        }
        // 清单指纹同时隔离同版本但内容不同的响应，不把签名 URL 写入磁盘。
        var identity = Cid + ":" + Version + ":" + string.Join("\n", _files.OrderBy(p => p.Key)
            .Select(p => $"{p.Key}:{p.Value.Size}:{p.Value.Hash}"));
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        _directory = Path.Combine(cacheDirectory, "partial", key);
        Directory.CreateDirectory(_directory);
    }

    public static async Task<CloudCoursewareSession> CreateAsync(SeewoApiClient api, string cid, string cacheDirectory)
    {
        var detail = await api.GetCoursewareDetailAsync(cid);
        if (detail?.Files == null || detail.Files.Count == 0) throw new InvalidDataException("获取课件文件清单失败");
        if (!string.Equals(detail.Cid, cid, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("课件详情 ID 不匹配");
        return new CloudCoursewareSession(api, detail, cacheDirectory);
    }

    public byte[] Manifest() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        protocolVersion = 1, sessionId = Id, cid = Cid, version = Version, name = Name,
        files = _files.Values.Select(f => new { path = f.Path, size = f.Size, hash = f.Hash })
    });

    public static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Length == 0 || normalized.StartsWith('/') || normalized.Contains(':') ||
            normalized.Split('/').Any(p => p.Length == 0 || p is "." or ".." ||
                p.EndsWith('.') || p.EndsWith(' ') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("无效的课件文件路径");
        return normalized;
    }

    public Task<string> GetFileAsync(string path, int priority = 1)
    {
        path = NormalizePath(path);
        if (!_files.TryGetValue(path, out var file)) throw new FileNotFoundException();
        return _queue.GetAsync(path, priority, () => DownloadAsync(file));
    }

    private static bool HasMd5(SeewoFileEntry file) => file.Hash.Length == 32 && file.Hash.All(Uri.IsHexDigit);

    private async Task<bool> IsValidAsync(string path, SeewoFileEntry file)
    {
        if (!File.Exists(path)) return false;
        if (file.Size >= 0 && new FileInfo(path).Length != file.Size) return false;
        if (!HasMd5(file)) return false; // 无可验证 hash 的缓存只在本会话内使用。
        await using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(await MD5.HashDataAsync(stream, _lifetime.Token));
        return string.Equals(hash, file.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private readonly HashSet<string> _verified = new(StringComparer.OrdinalIgnoreCase);
    private async Task<string> DownloadAsync(SeewoFileEntry file)
    {
        var ct = _lifetime.Token;
        ct.ThrowIfCancellationRequested();
        var path = Path.Combine(_directory, file.Path.Replace('/', Path.DirectorySeparatorChar));
        lock (_verified) { if (_verified.Contains(path) && File.Exists(path)) return path; }
        if (await IsValidAsync(path, file))
        {
            lock (_verified) _verified.Add(path);
            return path;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var part = path + "." + Id + ".part";
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var generation = Volatile.Read(ref _urlGeneration);
                using var response = await _http.GetAsync(file.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                if (attempt == 0 && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    await RefreshUrlsAsync(generation);
                    continue;
                }
                response.EnsureSuccessStatusCode();
                await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    await response.Content.CopyToAsync(output, ct);
                if (file.Size >= 0 && new FileInfo(part).Length != file.Size)
                    throw new InvalidDataException("课件文件长度校验失败");
                if (HasMd5(file) && !await IsValidAsync(part, file))
                    throw new InvalidDataException("课件文件 MD5 校验失败");
                ct.ThrowIfCancellationRequested();
                File.Move(part, path, true);
                lock (_verified) _verified.Add(path);
                return path;
            }
            throw new IOException("课件下载地址已失效，请重新打开");
        }
        finally { if (File.Exists(part)) File.Delete(part); }
    }

    private async Task RefreshUrlsAsync(int generation)
    {
        await _refreshGate.WaitAsync(_lifetime.Token);
        try
        {
            if (_urlGeneration != generation) return;
            var detail = await _api.GetCoursewareDetailAsync(Cid);
            if (detail?.Cid != Cid || detail.Version != Version || detail.Files?.Count != _files.Count)
                throw new InvalidDataException("云课件版本已变化，请重新打开");
            var replacements = detail.Files.ToDictionary(f => NormalizePath(f.Path), StringComparer.OrdinalIgnoreCase);
            foreach (var (path, file) in _files)
            {
                if (!replacements.TryGetValue(path, out var replacement) || replacement.Size != file.Size ||
                    !string.Equals(replacement.Hash, file.Hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("云课件内容已变化，请重新打开");
            }
            foreach (var (path, file) in _files) file.Url = replacements[path].Url;
            Interlocked.Increment(ref _urlGeneration);
        }
        finally { _refreshGate.Release(); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _queue.Dispose();
        _http.Dispose();
    }
}
