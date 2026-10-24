using System.Net;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ICC.CE.ENBX.Seewo.Services;

// 仅在用户打开配对窗口时启动，关闭窗口即断开所有手机并释放监听端口。
internal sealed class PptRemoteServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<PptRemoteState> _state;
    private readonly Func<int, int, CancellationToken, Task<bool>> _navigate;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _slots = new(4);
    private readonly short _token = (short)RandomNumberGenerator.GetInt32(short.MinValue, short.MaxValue + 1);
    private readonly DateTime _expires = DateTime.UtcNow.AddMinutes(15);
    public string QrCode { get; }
    public string ListeningEndpoints { get; }
    public event Action<string>? StatusChanged;

    public PptRemoteServer(Func<PptRemoteState> state, Func<int, int, CancellationToken, Task<bool>> navigate, Action<string> log)
    {
        _state = state;
        _navigate = navigate;
        _log = log;
        var addresses = PptRemotePairing.Addresses();
        if (addresses.Length == 0) throw new InvalidOperationException("没有可供手机连接的 IPv4 网络。");
        _listener = new TcpListener(IPAddress.Any, 0);
        _listener.Start(4);
        QrCode = PptRemotePairing.QrCode(addresses, ((IPEndPoint)_listener.LocalEndpoint).Port, _token);
        ListeningEndpoints = string.Join("、", addresses.Select(a => $"{a}:{((IPEndPoint)_listener.LocalEndpoint).Port}"));
        _log($"PPT 配对监听已启动：地址={string.Join(",", addresses.Select(a => a.ToString()))}，端口={((IPEndPoint)_listener.LocalEndpoint).Port}。");
        _ = AcceptAsync();
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                _log("收到手机 TCP 连接，等待协议握手。");
                if (DateTime.UtcNow >= _expires)
                {
                    client.Dispose();
                    StatusChanged?.Invoke("二维码已过期，请关闭窗口后重新打开。");
                    continue;
                }
                if (!_slots.Wait(0)) { client.Dispose(); _log("手机连接数已达上限。"); continue; }
                StatusChanged?.Invoke("手机已连入 TCP，正在等待协议握手……");
                _ = ServeAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { _log($"PPT 手机监听停止（{ex.GetType().Name}）。"); }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token))
        using (var sendGate = new SemaphoreSlim(1))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(15));
            var protocol = new PptRemoteProtocol(_token, _state, _navigate);
            Task? publisher = null;
            async Task SendAsync(object message)
            {
                await sendGate.WaitAsync(lifetime.Token);
                try { await PptRemotePairing.WriteAsync(client.GetStream(), message, lifetime.Token); }
                finally { sendGate.Release(); }
            }
            async Task PublishAsync()
            {
                PptRemoteState? previous = null;
                try
                {
                    while (!lifetime.IsCancellationRequested)
                    {
                        var current = _state();
                        if (current != previous)
                        {
                            await SendAsync(PptRemoteProtocol.Message(3, 9006, PptRemoteProtocol.StatusBody(current), Guid.NewGuid().ToString()));
                            previous = current;
                        }
                        await Task.Delay(500, lifetime.Token);
                    }
                }
                catch (Exception) { lifetime.Cancel(); }
            }
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var message = await PptRemotePairing.ReadAsync(client.GetStream(), lifetime.Token);
                    var root = message.RootElement;
                    var module = root.TryGetProperty("messageId", out var moduleValue) && moduleValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        moduleValue.TryGetInt32(out var moduleNumber) ? moduleNumber.ToString() : "invalid";
                    var command = root.TryGetProperty("content", out var content) && content.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        content.TryGetProperty("id", out var idValue) && idValue.ValueKind == System.Text.Json.JsonValueKind.Number &&
                        idValue.TryGetInt32(out var idNumber) ? idNumber.ToString() : "invalid";
                    _log($"收到配对协议消息：module={module}，id={command}。");
                    var response = await protocol.HandleAsync(message.RootElement, lifetime.Token);
                    if (response != null) await SendAsync(response);
                    if (protocol.Closed)
                    {
                        var reason = protocol.RejectionReason;
                        _log(reason == null ? "手机主动断开连接。" : "协议拒绝连接：" + reason + "。");
                        if (reason != null) StatusChanged?.Invoke("手机握手被拒绝：" + reason + "。请重新扫描当前二维码。");
                        break;
                    }
                    if (protocol.Paired)
                    {
                        lifetime.CancelAfter(TimeSpan.FromSeconds(45));
                        if (publisher == null)
                        {
                            StatusChanged?.Invoke("手机已连接，可使用 PPT 控制。");
                            _log("手机已通过局域网配对。");
                            publisher = PublishAsync();
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (!_lifetime.IsCancellationRequested)
                {
                    _log(protocol.Paired ? "手机连接空闲超时。" : "手机已连入 TCP，但协议握手超时。");
                    StatusChanged?.Invoke(protocol.Paired ? "手机连接超时，请重新扫码。" : "手机已连入，但握手超时。请重新扫码。");
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
            {
                _log($"手机连接在{(protocol.Paired ? "配对后" : "握手前")}结束（{ex.GetType().Name}）。");
                if (!protocol.Paired) StatusChanged?.Invoke($"手机已连入，但握手未完成（{ex.GetType().Name}）。");
            }
            catch (Exception ex) { _log($"PPT 手机连接停止（{ex.GetType().Name}）。"); }
            finally
            {
                lifetime.Cancel();
                if (publisher != null) await publisher;
                _slots.Release();
                if (protocol.Paired) StatusChanged?.Invoke("手机连接已结束，可重新扫码。");
            }
        }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _listener.Stop();
    }
}
