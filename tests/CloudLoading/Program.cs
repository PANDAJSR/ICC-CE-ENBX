using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ICC.CE.ENBX;
using ICC.CE.ENBX.Seewo.Models;
using ICC.CE.ENBX.Seewo.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

using (var queue = new PriorityDownloadQueue())
{
    var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var count = 0;
    Task<string> Run()
    {
        if (Interlocked.Increment(ref count) == 2) started.SetResult();
        return release.Task;
    }
    var a = queue.GetAsync("a", 2, Run);
    var b = queue.GetAsync("b", 2, Run);
    await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var pending = queue.GetAsync("pending", 2, () => Task.FromResult("promoted"));
    Check(ReferenceEquals(pending, queue.GetAsync("pending", 0, () => throw new Exception())), "去重失败");
    Check(await pending.WaitAsync(TimeSpan.FromSeconds(5)) == "promoted", "前台任务被后台阻塞");
    release.SetResult("ok");
    await Task.WhenAll(a, b);
}
Console.WriteLine("PASS: 去重、排队提权、预留前台容量");

Check(CoursewareRange.TryParse("bytes=2-4", 10, out var start, out var countBytes) && start == 2 && countBytes == 3, "普通区间");
Check(CoursewareRange.TryParse("bytes=-3", 10, out start, out countBytes) && start == 7 && countBytes == 3, "后缀区间");
Check(CoursewareRange.TryParse("bytes=8-", 10, out start, out countBytes) && countBytes == 2, "开放区间");
foreach (var range in new[] { "bytes=10-", "bytes=4-2", "bytes=0-1,3-4", "bytes=-0", "bytes=+1-2" })
    Check(!CoursewareRange.TryParse(range, 10, out _, out _), "应拒绝无效区间: " + range);
foreach (var path in new[] { "../secret", "a/../b", "/absolute", "a:b", "a//b", "a./file" })
{
    try { CloudCoursewareSession.NormalizePath(path); throw new Exception("路径校验失败: " + path); }
    catch (InvalidDataException) { }
}
Console.WriteLine("PASS: Range 与路径边界");

var root = Path.Combine(Environment.GetEnvironmentVariable("TEMP")!, "opencode", "cloud-loading-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var settings = new PluginSettings();
    Check(settings.CloudLazyLoadingEnabled, "默认设置");
    settings.CloudLazyLoadingEnabled = false;
    settings.Save(root);
    Check(!PluginSettings.Load(root).CloudLazyLoadingEnabled, "设置持久化失败");
    var data = new byte[] { 1, 2, 3, 4, 5 };
    var tcp = new TcpListener(IPAddress.Loopback, 0);
    tcp.Start();
    var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
    tcp.Stop();
    using var listener = new HttpListener();
    listener.Prefixes.Add($"http://localhost:{port}/");
    listener.Start();
    var requests = 0;
    var server = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            Interlocked.Increment(ref requests);
            if (context.Request.RawUrl == "/expired") context.Response.StatusCode = 403;
            else
            {
                context.Response.ContentLength64 = data.Length;
                await context.Response.OutputStream.WriteAsync(data);
            }
            context.Response.Close();
        }
    });
    var hash = Convert.ToHexString(MD5.HashData(data));
    SeewoDetailResponse Detail(string url, string? overrideHash = null, int version = 3) => new()
    {
        Cid = "test", Name = "test", Version = version,
        Files = [new() { Path = "Resources/a.bin", Hash = overrideHash ?? hash, Size = data.Length, Url = url }]
    };
    var url = $"http://localhost:{port}/valid";
    var details = 0;
    var api = new SeewoApiClient { Detail = () => Task.FromResult<SeewoDetailResponse?>(
        Detail(Interlocked.Increment(ref details) == 1 ? $"http://localhost:{port}/expired" : url)) };
    using (var session = await CloudCoursewareSession.CreateAsync(api, "test", root))
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => session.GetFileAsync("Resources/a.bin")));
        Check(results.Distinct().Count() == 1 && requests == 2 && details == 2, "URL 刷新/并发去重失败");
        Check((await File.ReadAllBytesAsync(results[0])).SequenceEqual(data), "缓存内容错误");
        using var range = new CoursewareRangeStream(results[0], 1, 2);
        var buffer = new byte[10];
        Check(range.Read(buffer, 0, 10) == 2 && buffer[0] == 2 && buffer[1] == 3 && range.Read(buffer, 0, 10) == 0, "Range 流越界");
        await session.GetFileAsync("Resources/a.bin");
        Check(requests == 2, "缓存未复用");
    }
    using (var cached = await CloudCoursewareSession.CreateAsync(api, "test", root))
    {
        await cached.GetFileAsync("Resources/a.bin");
        Check(requests == 2, "已校验磁盘缓存未复用");
    }
    var badApi = new SeewoApiClient { Detail = () => Task.FromResult<SeewoDetailResponse?>(Detail(url, new string('0', 32))) };
    using (var bad = await CloudCoursewareSession.CreateAsync(badApi, "test", root))
    {
        try { await bad.GetFileAsync("Resources/a.bin"); throw new Exception("错误 hash 被接受"); }
        catch (InvalidDataException) { }
        Check(!Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any(), "遗留未校验文件");
    }
    var changedCalls = 0;
    var changedApi = new SeewoApiClient { Detail = () => Task.FromResult<SeewoDetailResponse?>(
        Detail($"http://localhost:{port}/expired", version: ++changedCalls == 1 ? 4 : 5)) };
    using (var changed = await CloudCoursewareSession.CreateAsync(changedApi, "test", root))
    {
        try { await changed.GetFileAsync("Resources/a.bin"); throw new Exception("版本变化未拒绝"); }
        catch (InvalidDataException) { }
    }
    listener.Stop();
    await server;
    Console.WriteLine("PASS: 设置持久化、真实 HTTP 下载、缓存、MD5、原子提交、URL 刷新、版本隔离、Range 流");
}
finally { Directory.Delete(root, true); }
