using System.IO.Compression;
using System.Net;
using System.Text.Json;
using ICC.CE.ENBX.Seewo.Services;

var passed = 0;
void Check(bool value, string name)
{
    if (!value) throw new Exception(name);
    passed++;
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (Exception e) when (e is InvalidDataException or NotSupportedException) { passed++; return; }
    throw new Exception(name);
}
JsonElement Json(object data) => JsonSerializer.SerializeToElement(data);
var root = Path.Combine(Path.GetTempPath(), "icc-mobile-test-" + Guid.NewGuid().ToString("N"));
await PlaybackChecks.RunAsync(Check);
await PptRemoteChecks.RunAsync(Check);
Directory.CreateDirectory(root);
try
{
    Check(SeewoDeviceSession.GetHelperPath(root) == Path.Combine(root, "SeewoImBridge", "SeewoImBridge.exe"),
        "helper resolves from host installation directory independently of assembly location");
    foreach (var address in new[] { "127.0.0.1", "10.0.0.1", "192.168.1.1", "169.254.169.254", "172.16.0.1", "::1", "::ffff:127.0.0.1", "fc00::1" })
        Check(!MobileImageDownload.IsPublicAddress(IPAddress.Parse(address)), "reject private address " + address);
    Check(MobileImageDownload.IsPublicAddress(IPAddress.Parse("8.8.8.8")), "public address");
    foreach (var url in new[] { "file:///C:/test", "http://example.com/a", "https://user:pass@example.com/a", "https://127.0.0.1/a", "https://example.com:8443/a" })
        await Reject(() => Task.FromResult(MobileImageDownload.ValidateUrl(url)), "reject URL " + url);

    var archive = Path.Combine(root, "sample.zip");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
    {
        var entry = zip.CreateEntry("../../photo.png");
        await using var stream = entry.Open();
        await stream.WriteAsync(new byte[] { 1, 2, 3 });
    }
    var extracted = await MobileImageDownload.ExtractAsync(archive, root, "photo.png", default);
    Check(extracted == Path.Combine(root, "image.png") && File.ReadAllBytes(extracted).Length == 3, "ZIP paths never control output");
    await Reject(() => MobileImageDownload.ExtractAsync(archive, root, "../../photo.png", default), "reject traversal name");
    await Reject(() => MobileImageDownload.ExtractAsync(archive, root, "missing.png", default), "reject missing entry");
    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Update)) zip.CreateEntry("other/photo.png");
    await Reject(() => MobileImageDownload.ExtractAsync(archive, root, "photo.png", default), "reject ambiguous ZIP name");

    var messages = new List<(int Id, JsonElement Body)>();
    var inserted = 0;
    var allowInsert = true;
    var completed = new TaskCompletionSource<bool>();
    using var receiver = new MobileImageReceiver(root,
        (id, body, _) => { messages.Add((id, Json(body))); return Task.CompletedTask; },
        async (_, ct) => { inserted++; await completed.Task.WaitAsync(ct); return allowInsert; }, _ => { },
        (_, _, _, _, _) => Task.FromResult(extracted));
    await receiver.HandleAsync(301, Json(new { num = 10, size = 0 }), 2, default);
    Check(messages[^1].Body.GetProperty("code").GetInt32() == 2, "reject oversized batch");
    await receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, default);
    Check(inserted == 0 && messages[^1].Body.GetProperty("code").GetInt32() == 1, "unsolicited download rejected");
    await receiver.HandleAsync(301, Json(new { num = 1, size = 10 }), 2, default);
    await receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 3, default);
    Check(inserted == 0, "batch cannot be consumed by another platform");
    var priorCount = messages.Count;
    var pending = receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, default);
    Check(!pending.IsCompleted && messages.Count == priorCount && inserted == 1, "waits for insertion");
    completed.SetResult(true);
    await pending;
    Check(messages.Count == priorCount + 1 && messages[^1].Id == 304 && messages[^1].Body.GetProperty("code").GetInt32() == 0, "success only after insertion");
    await receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, default);
    Check(inserted == 1 && messages[^1].Body.GetProperty("code").GetInt32() == 0, "legacy retransmission replays receipt without reinserting");
    await receiver.HandleAsync(301, Json(new { num = 1, size = 10 }), 2, default);
    allowInsert = false;
    await receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, default);
    Check(messages[^1].Body.GetProperty("code").GetInt32() == 1, "insertion failure acknowledged");
    await receiver.HandleAsync(305, Json(new { }), 2, default);
    var oldInserted = inserted;
    await receiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, default);
    Check(inserted == oldInserted, "cancel clears batch");

    var waiting = new TaskCompletionSource<bool>();
    using var cancelReceiver = new MobileImageReceiver(root,
        (id, body, _) => { messages.Add((id, Json(body))); return Task.CompletedTask; },
        (_, ct) => waiting.Task.WaitAsync(ct), _ => { }, (_, _, _, _, _) => Task.FromResult(extracted));
    await cancelReceiver.HandleAsync(301, Json(new { num = 1, size = 10 }), 2, default);
    using var cancelled = new CancellationTokenSource();
    var cancelledInsert = cancelReceiver.HandleAsync(303, Json(new { address = "x", imagename = "x.png" }), 2, cancelled.Token);
    priorCount = messages.Count;
    cancelled.Cancel();
    try { await cancelledInsert; throw new Exception("cancel must interrupt pending insertion"); }
    catch (OperationCanceledException) { passed++; }
    Check(messages.Count == priorCount, "cancelled insert never acknowledges success");

    allowInsert = true;
    oldInserted = inserted;
    await receiver.HandleAsync(503, Json(new { images = new[] { new { id = "photo", downloadUrl = "x", extension = "png" } } }), 2, default);
    Check(inserted == oldInserted + 1 && messages[^1].Id == 504, "photo upload inserts and acknowledges start");
    await receiver.HandleAsync(501, Json(new { images = new[] { new { id = "photo", downloadUrl = "x", extension = "png" } } }), 2, default);
    await receiver.HandleAsync(531, Json(new { id = "photo" }), 2, default);
    Check(inserted == oldInserted + 1 && messages[^1].Id == 532 && messages[^1].Body.GetProperty("code").GetInt32() == 0, "photo retransmission and explicit insert do not duplicate");
    await receiver.HandleAsync(503, Json(new { images = new[] { new { id = "photo", downloadUrl = "x", extension = "png" } } }), 2, default);
    Check(inserted == oldInserted + 1 && messages[^1].Id == 504, "503 after 501 or repeated start preserves deduplication");
    await receiver.HandleAsync(505, Json(new { }), 2, default);
    await receiver.HandleAsync(501, Json(new { images = new[] { new { id = "photo", downloadUrl = "x", extension = "png" } } }), 2, default);
    await receiver.HandleAsync(503, Json(new { images = new[] { new { id = "photo", downloadUrl = "x", extension = "png" } } }), 2, default);
    await receiver.HandleAsync(531, Json(new { id = "photo" }), 2, default);
    Check(inserted == oldInserted + 2, "501 then 503 then 531 inserts once after previous session closes");
    await receiver.HandleAsync(531, Json(new { id = "unknown" }), 2, default);
    Check(messages[^1].Body.GetProperty("code").GetInt32() != 0, "unknown photo fails");

    var trusted = new { uid = "u", appId = SeewoDeviceSession.AppId, platform = 2, en5Id = "session" };
    Check(SeewoDeviceSession.IsTrustedMessage(Json(trusted), "u", "session"), "trusted source accepted");
    Check(!SeewoDeviceSession.IsTrustedMessage(Json(trusted), "other", "session"), "wrong account rejected");
    Check(!SeewoDeviceSession.IsTrustedMessage(Json(trusted), "u", "other"), "wrong desktop rejected");
    Check(!SeewoDeviceSession.IsTrustedMessage(Json(new { uid = "u", appId = "other", platform = 2, en5Id = "session" }), "u", "session"), "wrong app rejected");
    Check(!SeewoDeviceSession.IsTrustedMessage(Json(new { uid = "u", appId = SeewoDeviceSession.AppId, platform = 1, en5Id = "session" }), "u", "session"), "desktop sender rejected");
    Console.WriteLine($"PASS {passed} mobile-image checks");
}
finally { Directory.Delete(root, recursive: true); }
