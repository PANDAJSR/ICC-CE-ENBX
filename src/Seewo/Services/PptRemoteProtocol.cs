using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Text.Json;

namespace ICC.CE.ENBX.Seewo.Services;

internal sealed record PptRemoteState(int Status, int CurrentPage, int TotalPage, string Title, int Type = 0);

// 助手协议 5 / PPT 模块协议 1；与 EasiNote Mobile_Message 是不同的消息格式。
internal sealed class PptRemoteProtocol(short token, Func<PptRemoteState> state,
    Func<int, int, CancellationToken, Task<bool>> navigate)
{
    public bool Paired { get; private set; }
    public bool Closed { get; private set; }
    public string? RejectionReason { get; private set; }
    private readonly Dictionary<string, object> _receipts = new();
    private readonly Queue<string> _recent = new();

    public static object Message(int module, int id, object? body, string traceId = "") => new
    {
        fromAppCode = "SeewoIwbAssistant", messageId = module, traceId,
        content = new { id, body }
    };

    public static object StatusBody(PptRemoteState s) => new
    {
        code = 0, message = "", status = s.Status,
        info = s.Status == 0 ? null : new { currentPage = s.CurrentPage, totalPage = s.TotalPage, title = s.Title, type = s.Type }
    };

    public async Task<object?> HandleAsync(JsonElement m, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Closed) return null;
        var module = m.GetProperty("messageId").GetInt32();
        var content = m.GetProperty("content");
        var id = content.GetProperty("id").GetInt32();
        var trace = m.TryGetProperty("traceId", out var t) ? t.GetString() ?? "" : "";
        if (trace.Length > 256) throw new InvalidDataException("Trace too long");
        object Reply(int replyId, object? body) => Message(module, replyId, body, trace);
        if (module == 99999 && id == 9001)
        {
            var body = content.GetProperty("body");
            RejectionReason = m.GetProperty("fromAppCode").GetString() != "EasiNoteApp" ? "应用标识不匹配" :
                !body.TryGetProperty("token", out var supplied) ? "缺少配对令牌" :
                supplied.ValueKind != JsonValueKind.Number || !supplied.TryGetInt16(out var n) || n != token ? "配对令牌不匹配" :
                !body.TryGetProperty("clientId", out var clientId) || clientId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(clientId.GetString()) ? "缺少客户端标识" : null;
            var valid = RejectionReason == null;
            Paired = valid;
            Closed = !valid;
            return Reply(9002, new { protocol = 5, clientName = Environment.MachineName, result = valid ? 0 : 1 });
        }
        // 官方助手允许手机先发公共心跳探测 TCP，再进行配对握手。
        // 回心跳不会授予控制权限，未配对连接仍受首个握手超时限制。
        if (module == 99999 && id == 9003) return Reply(9004, null);
        if (!Paired) { Closed = true; RejectionReason = "控制请求先于公共握手"; return Message(99999, 9008, new { reason = 2 }, trace); }
        if (module == 99999)
        {
            switch (id)
            {
                case 9007: Closed = true; return null;
                case 9009: return Reply(9010, new { ip = PptRemotePairing.Addresses().Select(a => a.ToString()).ToArray() });
                case 9015: return Reply(9016, new { messageIds = new[] { "99999", "3" }, traceId = trace });
            }
        }
        if (module == 3)
        {
            if (id == 9001) return Reply(9002, new { protocol = 1, disable = false });
            if (id == 9003) return Reply(9004, StatusBody(state()));
            if (id == 9005) return null;
            if (id is 1001 or 1003 or 1005)
            {
                // 重发请求重放原回执，不再执行一次翻页。
                var key = $"{module}:{id}:{trace}";
                if (trace.Length > 0 && _receipts.TryGetValue(key, out var cached)) return cached;
                var page = 0;
                var valid = id != 1005 || (content.TryGetProperty("body", out var body) &&
                    body.ValueKind == JsonValueKind.Object && body.TryGetProperty("currentPage", out var p) && p.TryGetInt32(out page));
                var before = state();
                valid &= before.Status == 2 && (id != 1005 || page >= 1 && page <= before.TotalPage);
                var success = valid && await navigate(id, page, ct);
                ct.ThrowIfCancellationRequested();
                var after = state();
                var response = Reply(id + 1, new { code = success ? 0 : 2004,
                    message = success ? "" : "当前放映不可用或请求页码无效", currentPage = after.CurrentPage, totalPage = after.TotalPage });
                if (trace.Length > 0)
                {
                    _receipts[key] = response;
                    _recent.Enqueue(key);
                    if (_recent.Count > 256) _receipts.Remove(_recent.Dequeue());
                }
                return response;
            }
        }
        return Reply(-1, new { id });
    }
}

internal static class PptRemotePairing
{
    internal static bool IsLanAddress(IPAddress address)
    {
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        return b[0] != 0 && b[0] < 224 && !(b[0] == 169 && b[1] == 254) &&
            !(b[0] == 198 && b[1] is 18 or 19);
    }

    public static IPAddress[] Addresses() => System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
            (n.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 or
                System.Net.NetworkInformation.NetworkInterfaceType.Ethernet) &&
            !new[] { "vmware", "virtual", "vethernet", "mihomo", "tun", "tap", "vpn" }
                .Any(word => (n.Name + " " + n.Description).Contains(word, StringComparison.OrdinalIgnoreCase)))
        .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
        .ThenByDescending(n => n.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(n => n.Address)
        .Where(IsLanAddress)
        .Distinct().ToArray();

    public static string QrCode(IEnumerable<IPAddress> addresses, int port, short token)
    {
        var extension = new byte[] { 3, 1, (byte)token, (byte)(token >> 8) };
        var ips = addresses.SelectMany(a => new byte[] { 0 }.Concat(a.GetAddressBytes())).ToArray();
        var ports = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(ports, checked((ushort)port));
        BinaryPrimitives.WriteUInt16BigEndian(ports.AsSpan(2), checked((ushort)port));
        return $"https://r302.cc/d8WaY8?e={Convert.ToBase64String(extension)}&i={Convert.ToBase64String(ips)}&o={Convert.ToBase64String(ports)}";
    }

    public static async Task<JsonDocument> ReadAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[8];
        await stream.ReadExactlyAsync(header, ct);
        if (!header.AsSpan(0, 4).SequenceEqual(new byte[] { 170, 187, 204, 221 })) throw new InvalidDataException("Invalid header");
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));
        if (length <= 0 || length > 100_000) throw new InvalidDataException("Invalid packet length");
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonDocument.Parse(body);
    }

    public static async Task WriteAsync(Stream stream, object message, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message);
        if (body.Length > 100_000) throw new InvalidDataException("Packet too large");
        var header = new byte[] { 170, 187, 204, 221, 0, 0, 0, 0 };
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), body.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(body, ct);
    }
}
