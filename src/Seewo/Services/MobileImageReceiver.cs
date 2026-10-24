using System.IO;
using System.Text.Json;

namespace ICC.CE.ENBX.Seewo.Services;

internal sealed class MobileImageReceiver : IDisposable
{
    private readonly Func<int, object, int, Task> _send;
    private readonly Func<string, CancellationToken, Task<bool>> _insert;
    private readonly MobileImageDownload _download = new();
    private readonly string _root;
    private readonly Action<string> _log;
    private readonly Func<string, string, string?, string?, CancellationToken, Task<string>> _fetch;
    private readonly Dictionary<string, JsonElement> _photos = new();
    private readonly HashSet<string> _inserted = new();
    private readonly Dictionary<(string Address, string Name), int> _legacyResults = new();
    private int _remaining;
    private int _batchPlatform;

    public MobileImageReceiver(string root, Func<int, object, int, Task> send,
        Func<string, CancellationToken, Task<bool>> insert, Action<string> log,
        Func<string, string, string?, string?, CancellationToken, Task<string>>? fetch = null)
    {
        _root = root;
        _send = send;
        _insert = insert;
        _log = log;
        _fetch = fetch ?? _download.DownloadAsync;
    }

    // Called serially by the device session. Cancellation is controlled outside the queue.
    public async Task HandleAsync(int id, JsonElement body, int platform, CancellationToken ct)
    {
        switch (id)
        {
            case 301:
                _remaining = 0;
                _legacyResults.Clear();
                _batchPlatform = platform;
                var count = body.TryGetProperty("num", out var num) && num.TryGetInt32(out var n) ? n : 0;
                var size = body.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out var s) ? s : 0;
                var code = count is < 1 or > 9 || size < 0 || size > 9 * MobileImageDownload.MaxBytes ? 2 : 0;
                if (code == 0)
                {
                    Directory.CreateDirectory(_root);
                    if (new DriveInfo(Path.GetPathRoot(_root)!).AvailableFreeSpace < Math.Max(size, MobileImageDownload.MaxBytes) * 2) code = 1;
                    else _remaining = count;
                }
                await _send(302, new { code }, platform);
                break;
            case 303:
                var address = body.GetProperty("address").GetString() ?? "";
                var name = body.GetProperty("imagename").GetString() ?? "";
                if (platform == _batchPlatform && _legacyResults.TryGetValue((address, name), out var priorResult))
                {
                    await _send(304, new { address, code = priorResult }, platform);
                    break;
                }
                if (_remaining <= 0 || platform != _batchPlatform)
                {
                    await _send(304, new { address, code = 1 }, platform);
                    break;
                }
                _remaining--;
                var result = await ReceiveAsync(address, name, null, ct);
                ct.ThrowIfCancellationRequested();
                _legacyResults[(address, name)] = result;
                await _send(304, new { address, code = result }, platform);
                break;
            case 501:
            case 503:
                // 501 和 503 可携带同一列表，启动展示不能清空已完成的插图记录。
                if (id == 503) await _send(504, new { }, platform);
                if (!body.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array || images.GetArrayLength() > 9)
                    break;
                foreach (var photo in images.EnumerateArray())
                {
                    var photoId = photo.GetProperty("id").ToString();
                    if (_photos.Count >= 9 && !_photos.ContainsKey(photoId)) continue;
                    _photos[photoId] = photo.Clone();
                    if (_inserted.Contains(photoId)) continue;
                    var photoResult = await ReceivePhotoAsync(photo, ct);
                    if (photoResult == 0) _inserted.Add(photoId);
                    else _log($"照片插入失败，code={photoResult}。");
                }
                break;
            case 531:
                var requestedId = body.GetProperty("id").ToString();
                var success = _inserted.Contains(requestedId);
                if (!success && _photos.TryGetValue(requestedId, out var item))
                {
                    success = await ReceivePhotoAsync(item, ct) == 0;
                    if (success) _inserted.Add(requestedId);
                }
                ct.ThrowIfCancellationRequested();
                await _send(532, new { code = success ? 0 : 1, reason = success ? "" : "图片插入失败" }, platform);
                break;
            case 305:
            case 502:
            case 505:
                _remaining = 0;
                _legacyResults.Clear();
                _photos.Clear();
                _inserted.Clear();
                if (id == 505) await _send(506, new { }, platform);
                break;
        }
    }

    private Task<int> ReceivePhotoAsync(JsonElement photo, CancellationToken ct) => ReceiveAsync(
        photo.GetProperty("downloadUrl").GetString() ?? "", null,
        photo.TryGetProperty("extension", out var ext) ? ext.GetString() : null, ct);

    private async Task<int> ReceiveAsync(string url, string? name, string? extension, CancellationToken ct)
    {
        var directory = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        try
        {
            string path;
            try { path = await _fetch(url, directory, name, extension, ct); }
            catch (NotSupportedException) { return 3; }
            catch (InvalidDataException) { return name == null ? 3 : 2; }
            catch (OperationCanceledException) { throw; }
            catch { return 4; }
            ct.ThrowIfCancellationRequested();
            try { return await _insert(path, ct) ? 0 : 1; }
            catch (OperationCanceledException) { throw; }
            catch { return 1; }
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
            catch { _log("手机图片临时文件清理失败。"); }
        }
    }
    public void Dispose() => _download.Dispose();
}
