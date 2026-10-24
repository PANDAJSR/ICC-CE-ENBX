using System.Buffers.Binary;
using System.Net;
using System.Text.Json;
using ICC.CE.ENBX.Seewo.Services;

internal static class PptRemoteChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        JsonElement Request(int module, int id, object? body, string trace = "trace") => JsonSerializer.SerializeToElement(new
        {
            fromAppCode = "EasiNoteApp", messageId = module, traceId = trace, content = new { id, body }
        });
        JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);
        var current = new PptRemoteState(2, 1, 5, "test.pptx");
        var calls = new List<(int Id, int Page)>();
        var remote = new PptRemoteProtocol(-1234, () => current, (id, page, ct) =>
        {
            calls.Add((id, page));
            current = current with { CurrentPage = id == 1005 ? page : current.CurrentPage + (id == 1003 ? 1 : -1) };
            return Task.FromResult(true);
        });
        var handshake = Request(99999, 9001, new { clientName = "phone", clientId = "device", token = -1234 });
        var probe = Json(await remote.HandleAsync(Request(99999, 9003, null, "probe"), default));
        check(!remote.Paired && !remote.Closed && probe.GetProperty("content").GetProperty("id").GetInt32() == 9004 &&
            probe.GetProperty("traceId").GetString() == "probe", "PPT pre-handshake heartbeat responds without granting control");
        var reply = Json(await remote.HandleAsync(handshake, default));
        check(remote.Paired && reply.GetProperty("content").GetProperty("body").GetProperty("protocol").GetInt32() == 5,
            "PPT handshake accepts matching QR token");
        check(reply.GetProperty("traceId").GetString() == "trace", "PPT response preserves trace identity");
        reply = Json(await remote.HandleAsync(Request(3, 9001, null), default));
        check(reply.GetProperty("content").GetProperty("body").GetProperty("protocol").GetInt32() == 1, "PPT module protocol version");
        reply = Json(await remote.HandleAsync(Request(3, 9003, null), default));
        check(reply.GetProperty("content").GetProperty("body").GetProperty("info").GetProperty("currentPage").GetInt32() == 1,
            "PPT reports one-based current page");
        await remote.HandleAsync(Request(3, 1003, null, "next"), default);
        await remote.HandleAsync(Request(3, 1003, null, "next"), default);
        check(calls.Count == 1 && current.CurrentPage == 2, "PPT retry replays receipt without second navigation");
        await remote.HandleAsync(Request(3, 1001, null, "prev"), default);
        await remote.HandleAsync(Request(3, 1005, new { currentPage = 4 }, "goto"), default);
        check(current.CurrentPage == 4 && calls[^1] == (1005, 4), "PPT previous and direct navigation route through adapter");
        reply = Json(await remote.HandleAsync(Request(3, 1005, new { currentPage = 99 }, "invalid"), default));
        check(calls.Count == 3 && reply.GetProperty("content").GetProperty("body").GetProperty("code").GetInt32() != 0,
            "PPT rejects out-of-range page");
        current = current with { Status = 1 };
        await remote.HandleAsync(Request(3, 1003, null, "edit"), default);
        check(calls.Count == 3, "PPT cannot navigate edit mode");
        var unpaired = new PptRemoteProtocol(42, () => current, (_, _, _) => throw new Exception("must not navigate"));
        await unpaired.HandleAsync(Request(99999, 9003, null), default);
        await unpaired.HandleAsync(Request(3, 1003, null), default);
        check(unpaired.Closed && !unpaired.Paired, "PPT heartbeat probe never authorizes controls without handshake");
        var wrongToken = new PptRemoteProtocol(42, () => current, (_, _, _) => throw new Exception("must not navigate"));
        await wrongToken.HandleAsync(handshake, default);
        check(wrongToken.Closed && !wrongToken.Paired, "PPT rejects incorrect QR token");
        var missingToken = new PptRemoteProtocol(42, () => current, (_, _, _) => throw new Exception("must not navigate"));
        await missingToken.HandleAsync(Request(99999, 9001, new { clientId = "phone" }), default);
        check(!missingToken.Paired, "PPT never accepts absent QR token");
        check(missingToken.RejectionReason == "缺少配对令牌" && wrongToken.RejectionReason == "配对令牌不匹配",
            "PPT diagnostics distinguish absent and stale tokens without recording values");
        check(new[] { "192.168.31.113", "10.0.0.2", "172.16.0.2" }.All(s => PptRemotePairing.IsLanAddress(IPAddress.Parse(s))),
            "PPT pairing keeps normal LAN addresses");
        check(new[] { "127.0.0.1", "169.254.2.3", "198.18.0.1", "198.19.1.2", "0.0.0.0", "224.0.0.1", "255.255.255.255", "::1" }
            .All(s => !PptRemotePairing.IsLanAddress(IPAddress.Parse(s))), "PPT pairing excludes loopback link-local TUN and non-unicast addresses");

        var qr = PptRemotePairing.QrCode([IPAddress.Parse("192.168.1.2")], 12345, -1234);
        var parts = qr.Split('?')[1].Split('&').ToDictionary(s => s.Split('=')[0], s => s[(s.IndexOf('=') + 1)..]);
        check(Convert.FromBase64String(parts["i"]).SequenceEqual(new byte[] { 0, 192, 168, 1, 2 }), "PPT QR encodes LAN address with network flags");
        var extension = Convert.FromBase64String(parts["e"]);
        check(extension[0] == 3 && BinaryPrimitives.ReadInt16LittleEndian(extension.AsSpan(2)) == -1234, "PPT QR token uses signed little endian");
        var ports = Convert.FromBase64String(parts["o"]);
        check(BinaryPrimitives.ReadUInt16BigEndian(ports) == 12345 && BinaryPrimitives.ReadUInt16BigEndian(ports.AsSpan(2)) == 12345,
            "PPT QR advertises active TCP port");
        using var packet = new MemoryStream();
        await PptRemotePairing.WriteAsync(packet, reply, default);
        check(packet.ToArray().AsSpan(0, 4).SequenceEqual(new byte[] { 170, 187, 204, 221 }), "PPT framing magic");
        packet.Position = 0;
        using var parsed = await PptRemotePairing.ReadAsync(packet, default);
        check(parsed.RootElement.GetProperty("traceId").GetString() == "invalid", "PPT frame roundtrip");
        var malformed = packet.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(malformed.AsSpan(4), -1);
        try { using var ignored = await PptRemotePairing.ReadAsync(new MemoryStream(malformed), default); check(false, "must reject negative length"); }
        catch (InvalidDataException) { check(true, "PPT rejects negative frame length"); }
        BinaryPrimitives.WriteInt32BigEndian(malformed.AsSpan(4), 100001);
        try { using var ignored = await PptRemotePairing.ReadAsync(new MemoryStream(malformed), default); check(false, "must reject oversized frame"); }
        catch (InvalidDataException) { check(true, "PPT rejects oversized frame before allocating"); }

        var diagnostics = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var server = new PptRemoteServer(() => current, (_, _, _) => Task.FromResult(true), diagnostics.Enqueue);
        var connectionParts = server.QrCode.Split('?')[1].Split('&').ToDictionary(s => s.Split('=')[0], s => s[(s.IndexOf('=') + 1)..]);
        var serverPort = BinaryPrimitives.ReadUInt16BigEndian(Convert.FromBase64String(connectionParts["o"]));
        var serverToken = BinaryPrimitives.ReadInt16LittleEndian(Convert.FromBase64String(connectionParts["e"]).AsSpan(2));
        using var tcp = new System.Net.Sockets.TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await tcp.ConnectAsync(IPAddress.Loopback, serverPort, timeout.Token);
        await PptRemotePairing.WriteAsync(tcp.GetStream(), Request(99999, 9003, null, "tcp-probe"), timeout.Token);
        using var heartbeat = await PptRemotePairing.ReadAsync(tcp.GetStream(), timeout.Token);
        check(heartbeat.RootElement.GetProperty("content").GetProperty("id").GetInt32() == 9004 &&
            heartbeat.RootElement.GetProperty("traceId").GetString() == "tcp-probe", "PPT real TCP accepts heartbeat before handshake");
        await PptRemotePairing.WriteAsync(tcp.GetStream(), Request(99999, 9001,
            new { clientName = "integration", clientId = "phone", token = serverToken }), timeout.Token);
        using var accepted = await PptRemotePairing.ReadAsync(tcp.GetStream(), timeout.Token);
        check(accepted.RootElement.GetProperty("content").GetProperty("id").GetInt32() == 9002,
            "PPT real TCP framing and handshake");
        await PptRemotePairing.WriteAsync(tcp.GetStream(), Request(3, 9003, null), timeout.Token);
        var receivedQuery = false;
        for (var i = 0; i < 3; i++)
        {
            using var received = await PptRemotePairing.ReadAsync(tcp.GetStream(), timeout.Token);
            if (received.RootElement.GetProperty("content").GetProperty("id").GetInt32() == 9004)
            {
                receivedQuery = true;
                break;
            }
        }
        check(receivedQuery, "PPT server responds after initial state notification");
        check(diagnostics.Any(s => s.Contains("收到手机 TCP")) && diagnostics.Any(s => s.Contains("module=99999，id=9001")) &&
            !diagnostics.Any(s => s.Contains("token=")), "PPT records TCP and handshake stages without token values");
        server.Dispose();
        var disconnected = false;
        try { using var ignored = await PptRemotePairing.ReadAsync(tcp.GetStream(), timeout.Token); }
        catch (Exception ex) when (ex is EndOfStreamException or IOException) { disconnected = true; }
        check(disconnected, "PPT pairing window shutdown closes active socket");
    }
}
