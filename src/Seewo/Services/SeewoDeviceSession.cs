using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Channels;
using ICC.CE.ENBX.Seewo.Models;

namespace ICC.CE.ENBX.Seewo.Services;

internal sealed class SeewoDeviceSession : IDisposable
{
    internal const string AppId = "29a414ccedf245208cd78ef76954e73d";
    private readonly SeewoAuthManager _auth;
    private readonly string _directory;
    private readonly string _pluginDirectory;
    private readonly Action<string> _log;
    private readonly Func<string, CancellationToken, Task<bool>> _insert;
    private readonly Func<MobilePlaybackState, bool, CancellationToken, Task<bool>> _navigate;
    private MobilePlaybackState? _playback;
    private event Action? PlaybackChanged;

    public void UpdatePlayback(MobilePlaybackState? state)
    {
        Volatile.Write(ref _playback, state);
        PlaybackChanged?.Invoke();
    }
    private readonly object _gate = new();
    private CancellationTokenSource? _session;
    private string? _uid, _token;
    private bool _disposed;
    private readonly string _deviceId;
    private readonly string _en5Id = Guid.NewGuid().ToString("N");

    public SeewoDeviceSession(SeewoAuthManager auth, string directory, string pluginDirectory,
        Func<string, CancellationToken, Task<bool>> insert, Action<string> log,
        Func<MobilePlaybackState, bool, CancellationToken, Task<bool>>? navigate = null)
    {
        _auth = auth;
        _directory = directory;
        _pluginDirectory = pluginDirectory;
        _insert = insert;
        _navigate = navigate ?? ((_, _, _) => Task.FromResult(false));
        _log = log;
        Directory.CreateDirectory(directory);
        var idFile = Path.Combine(directory, "seewo-device-id");
        var saved = File.Exists(idFile) ? File.ReadAllText(idFile).Trim() : "";
        _deviceId = Guid.TryParseExact(saved, "N", out _) ? saved : Guid.NewGuid().ToString("N");
        if (saved != _deviceId) File.WriteAllText(idFile, _deviceId);
        _auth.AuthStateChanged += OnAuthChanged;
        OnAuthChanged(auth.CurrentProfile);
    }

    private void OnAuthChanged(SeewoAuthProfile? profile)
    {
        lock (_gate)
        {
            if (_disposed) return;
            var uid = profile?.IsValid == true ? profile.Uid : null;
            var token = profile?.IsValid == true ? profile.Token : null;
            if (uid == _uid && token == _token) return;
            _session?.Cancel();
            _uid = uid;
            _token = token;
            if (string.IsNullOrWhiteSpace(uid) || string.IsNullOrWhiteSpace(token)) return;
            var cts = new CancellationTokenSource();
            _session = cts;
            _ = Task.Run(async () =>
            {
                try { await RunAsync(uid, token, cts.Token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _log($"手机传图会话停止（{ex.GetType().Name}, 0x{ex.HResult:X8}），请重新登录后重试。"); }
                finally { lock (_gate) { if (ReferenceEquals(_session, cts)) _session = null; cts.Dispose(); } }
            });
        }
    }

    private async Task RunAsync(string uid, string token, CancellationToken ct)
    {
        var helper = GetHelperPath(_pluginDirectory);
        var sdk = FindSdkDirectory();
        if (!File.Exists(helper) || sdk == null)
        {
            _log("手机传图不可用：需要 SeewoImBridge 辅助程序、本机希沃白板 5 组件及 x86 .NET 10 运行时。");
            return;
        }
        for (var attempt = 0; !ct.IsCancellationRequested; attempt++)
        {
            try { await RunProcessAsync(helper, sdk, uid, token, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { _log($"手机传图连接中断（{ex.GetType().Name}, 0x{ex.HResult:X8}），将重试。"); }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * (attempt + 1))), ct);
        }
    }

    private async Task RunProcessAsync(string helper, string sdk, string uid, string token, CancellationToken ct)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(helper)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        } };
        process.StartInfo.ArgumentList.Add("--sdk");
        process.StartInfo.ArgumentList.Add(sdk);
        process.Start();
        _log("手机传图辅助程序已启动，等待 IM 连接。");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var connectionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var timeoutRegistration = connectionTimeout.Token.Register(() => lifetime.Cancel());
        using var kill = lifetime.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } });
        // Native diagnostics may contain credentials: drain stderr without persisting it.
        var stderr = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync(lifetime.Token) != null) { } } catch { } });
        using var writeGate = new SemaphoreSlim(1, 1);
        async Task WriteAsync(object command)
        {
            await writeGate.WaitAsync(lifetime.Token);
            try
            {
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command).AsMemory(), lifetime.Token);
                await process.StandardInput.FlushAsync(lifetime.Token);
            }
            finally { writeGate.Release(); }
        }
        using var receiver = new MobileImageReceiver(Path.Combine(_directory, "mobile-images"),
            (id, body, platform) => WriteAsync(new { type = "send", id, body, platform }), _insert, _log);
        var remote = new MobilePlaybackRemote(() => Volatile.Read(ref _playback), _navigate,
            (id, body, platform) => platform.HasValue
                ? WriteAsync(new { type = "send", id, body, platform = platform.Value })
                : WriteAsync(new { type = "send", id, body }), _log);
        var controls = Channel.CreateBounded<(JsonElement Message, string? SessionId)>(64);
        void QueuePlayback() => controls.Writer.TryWrite((JsonSerializer.SerializeToElement(new { id = -100 }), null));
        PlaybackChanged += QueuePlayback;
        var controlWorker = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in controls.Reader.ReadAllAsync(lifetime.Token))
                {
                    try
                    {
                        var m = entry.Message;
                        var id = m.GetProperty("id").GetInt32();
                        if (id is 204 or 205 && entry.SessionId != Volatile.Read(ref _playback)?.SessionId) continue;
                        if (id == -100) await remote.PublishAsync();
                        else await remote.HandleAsync(id, m.GetProperty("body"), m.GetProperty("platform").GetInt32(),
                            m.TryGetProperty("messageId", out var mid) ? mid.GetString() ?? "" : "", lifetime.Token);
                    }
                    catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
                    catch { _log("手机课件遥控消息处理失败。"); }
                }
            }
            catch (OperationCanceledException) { }
        });
        var queue = Channel.CreateBounded<(JsonElement Message, CancellationToken Token)>(32);
        var batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var worker = Task.Run(async () =>
        {
            try
            {
                await foreach (var entry in queue.Reader.ReadAllAsync(lifetime.Token))
                {
                    try
                    {
                        entry.Token.ThrowIfCancellationRequested();
                        var m = entry.Message;
                        await receiver.HandleAsync(m.GetProperty("id").GetInt32(), m.GetProperty("body"), m.GetProperty("platform").GetInt32(), entry.Token);
                    }
                    catch (OperationCanceledException) { }
                    catch { _log("手机图片消息处理失败。"); }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            await WriteAsync(new { type = "connect", uid, token, deviceId = _deviceId, en5Id = _en5Id, machineName = Environment.MachineName });
            while (await process.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                if (line.Length > 1024 * 1024) throw new InvalidDataException("IM message too large");
                JsonDocument parsed;
                try { parsed = JsonDocument.Parse(line); } catch (JsonException) { continue; }
                using (parsed)
                {
                    var m = parsed.RootElement;
                    if (!m.TryGetProperty("type", out var type)) continue;
                    switch (type.GetString())
                    {
                        case "connected": connectionTimeout.CancelAfter(Timeout.InfiniteTimeSpan); _log("希沃手机传图 IM 已连接。"); QueuePlayback(); break;
                        case "disconnected":
                            var code = m.TryGetProperty("code", out var status) && status.TryGetInt32(out var number) ? number : 0;
                            _log($"希沃手机传图 IM 已断开，code={code}。"); return;
                        case "error": _log("希沃手机传图 IM 报告错误；请检查官方组件、运行时及登录凭证。"); break;
                        case "message":
                            if (!IsTrustedMessage(m, uid, _en5Id)) break;
                            var id = m.GetProperty("id").GetInt32();
                            if (MobilePlaybackRemote.Handles(id))
                            {
                                if (!controls.Writer.TryWrite((m.Clone(), Volatile.Read(ref _playback)?.SessionId)))
                                    throw new InvalidDataException("IM control queue full");
                                break;
                            }
                            if (id is 301 or 305 or 502 or 505)
                            {
                                batch.Cancel();
                                batch.Dispose();
                                batch = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            }
                            if (!queue.Writer.TryWrite((m.Clone(), batch.Token)))
                                throw new InvalidDataException("IM queue full");
                            break;
                    }
                }
            }
        }
        finally
        {
            lifetime.Cancel();
            PlaybackChanged -= QueuePlayback;
            controls.Writer.TryComplete();
            queue.Writer.TryComplete();
            await controlWorker;
            await worker;
            await stderr;
            batch.Dispose();
        }
    }

    internal static bool IsTrustedMessage(JsonElement message, string uid, string en5Id)
    {
        return message.TryGetProperty("uid", out var u) && u.GetString() == uid &&
            message.TryGetProperty("appId", out var a) && a.GetString() == AppId &&
            message.TryGetProperty("platform", out var p) && p.TryGetInt32(out var platform) && platform is 2 or 3 or 9 or 13 or 14 or 15 or 16 &&
            message.TryGetProperty("en5Id", out var e) && (e.GetString() is null or "" || e.GetString() == en5Id);
    }

    // Host loads plugin assemblies from streams, so Assembly.Location can be empty.
    internal static string GetHelperPath(string pluginDirectory)
    {
        if (string.IsNullOrWhiteSpace(pluginDirectory) || !Path.IsPathFullyQualified(pluginDirectory))
            throw new ArgumentException("A full plugin installation directory is required.", nameof(pluginDirectory));
        return Path.Combine(pluginDirectory, "SeewoImBridge", "SeewoImBridge.exe");
    }

    private static string? FindSdkDirectory()
    {
        var configured = Environment.GetEnvironmentVariable("ICC_CE_SEEWO_SDK");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(Path.Combine(configured, "runtimes", "win-x86", "lib", "net5.0", "IMWrapper.dll"))) return configured;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Seewo", "EasiNote5");
        if (!Directory.Exists(root)) return null;
        return Directory.EnumerateDirectories(root, "EasiNote5_*").OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => Path.Combine(x, "Main"))
            .FirstOrDefault(x => File.Exists(Path.Combine(x, "runtimes", "win-x86", "lib", "net5.0", "IMWrapper.dll")));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _auth.AuthStateChanged -= OnAuthChanged;
            _session?.Cancel();
            _uid = _token = null;
        }
    }
}
