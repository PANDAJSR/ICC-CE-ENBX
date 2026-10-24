using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace SeewoImBridge;

internal sealed class Bridge(IpcOutput ipc)
{
    private const string AppId = "29a414ccedf245208cd78ef76954e73d";
    private static readonly ushort[] MobilePlatforms = [2, 3, 9, 13, 14, 15, 16];
    private readonly Channel<Action> events = Channel.CreateBounded<Action>(new BoundedChannelOptions(256)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.Wait
    });
    private string uid = "";
    private string en5Id = "";
    private string machineName = "";
    private bool initialized;
    private bool connectAttempted;
    private bool connected;
    private volatile bool stopping;
    private volatile bool queueOverflow;

    public static int Run(IpcOutput ipc, bool probe)
    {
        if (probe)
        {
            // Executes the native version getter, not merely Assembly.Load.
            string version = IMWrapper.GetIMSdkVersion();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var modules = process.Modules.Cast<System.Diagnostics.ProcessModule>()
                .Where(module => module.ModuleName.Equals("IMWrapper.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.Equals("Ijwhost.dll", StringComparison.OrdinalIgnoreCase)
                    || module.ModuleName.StartsWith("rtmq_", StringComparison.OrdinalIgnoreCase))
                .Select(module => module.ModuleName).Order().ToArray();
            ipc.Write(new { type = "probe", ok = true, architecture = "x86", sdkVersion = version, modules });
            return 0;
        }
        var bridge = new Bridge(ipc);
        return bridge.Run();
    }

    private int Run()
    {
        RegisterCallbacks();
        _ = ReadInputAsync();
        try
        {
            while (!stopping && events.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (!stopping && events.Reader.TryRead(out var action))
                {
                    try { action(); }
                    catch (Exception ex) { ipc.Error($"Bridge operation failed ({ex.GetType().Name})."); }
                }
                if (queueOverflow)
                {
                    ipc.Error("SDK event queue overflow; restart the bridge.");
                    stopping = true;
                }
            }
            return queueOverflow ? 1 : 0;
        }
        finally
        {
            stopping = true;
            // Exit stops the native transport before callbacks are unregistered.
            try { if (initialized) IMWrapper.ExitIM(); }
            catch { ipc.Error("SDK shutdown failed."); }
            try { IMWrapper.FreeResource(); }
            catch { ipc.Error("SDK callback cleanup failed."); }
            events.Writer.TryComplete();
        }
    }

    private void RegisterCallbacks()
    {
        IMWrapper.SetIMLogLevel(3);
        IMWrapper.SetFeatureAndMode((IMFeatureWrapper)1, (IMSdkMode)2);
        // The wrapper keeps strong delegate references in its static fields.
        IMWrapper.SetIMLogCallBack((_, _, _, _, _) => { });
        IMWrapper.SetIMConnectCallBack((_, _) => GuardCallback(() => Enqueue(OnConnected)));
        IMWrapper.SetIMDisconnectCallBack((code, _) => GuardCallback(() => Enqueue(() =>
        {
            connected = false;
            // Native reason strings can embed credentials; expose only a fixed description.
            ipc.Write(new { type = "disconnected", code, message = "IM transport disconnected." });
        })));
        IMWrapper.SetRecvIMSingleCommandCallBack((data, _) => GuardCallback(() =>
        {
            // Snapshot managed data before returning to the native caller.
            string fromUid = data.fromUid, appId = data.fromAppid, messageId = data.msgid;
            ushort platform = data.fromPlatform;
            string message = data.originalData is { Length: > 0 }
                ? Encoding.UTF8.GetString(data.originalData) : data.msg;
            if (message.Length <= 1024 * 1024)
                Enqueue(() => OnMessage(fromUid, appId, platform, messageId, message));
        }));
        // Register no-ops as the native wrapper calls these fields without null checks.
        IMWrapper.SetIMNotifyCallBack((_, _) => { });
        IMWrapper.SetIMMessageCallBack((_, _) => { });
        IMWrapper.SetIMSingleCommandAsyncCallBack((_, _, _, _) => { });
        IMWrapper.SetRecvIMRoomCommandCallBack((_, _) => { });
    }

    private void GuardCallback(Action callback)
    {
        try { if (!stopping) callback(); }
        catch { /* No managed exception may escape into the native callback. */ }
    }

    private void Enqueue(Action action)
    {
        if (!stopping && !events.Writer.TryWrite(action)) queueOverflow = true;
    }

    private async Task ReadInputAsync()
    {
        try
        {
            // Bound input before allocating a whole untrusted line (UTF-16 character limit).
            using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false, true));
            var line = new StringBuilder();
            char[] buffer = new char[4096];
            bool oversized = false;
            int count;
            while (!stopping && (count = await input.ReadAsync(buffer)) != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char ch = buffer[i];
                    if (ch == '\n')
                    {
                        if (oversized) await events.Writer.WriteAsync(() => ipc.Error("Input line exceeds 1 MiB characters."));
                        else if (line.Length != 0)
                        {
                            string json = line.ToString();
                            await events.Writer.WriteAsync(() => HandleInput(json));
                        }
                        line.Clear();
                        oversized = false;
                    }
                    else if (!oversized)
                    {
                        if (line.Length >= 1024 * 1024) { oversized = true; line.Clear(); }
                        else line.Append(ch);
                    }
                }
            }
            // JSONL requires a final newline; EOF always closes the session.
            await events.Writer.WriteAsync(() => stopping = true);
        }
        catch
        {
            events.Writer.TryWrite(() => { ipc.Error("Input stream closed or invalid UTF-8."); stopping = true; });
            events.Writer.TryComplete();
        }
    }

    private void HandleInput(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            switch (RequiredString(root, "type"))
            {
                case "connect": Connect(root); break;
                case "send":
                    if (!connected) throw new InvalidOperationException();
                    int id = root.GetProperty("id").GetInt32();
                    JsonElement body = root.GetProperty("body");
                    ushort? platform = root.TryGetProperty("platform", out var p) ? p.GetUInt16() : null;
                    if (platform.HasValue && !MobilePlatforms.Contains(platform.Value)) throw new ArgumentException();
                    Send(id, body, platform);
                    break;
                case "stop": stopping = true; break;
                default: ipc.Error("Unknown input type."); break;
            }
        }
        catch (JsonException) { ipc.Error("Invalid JSON input."); }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            ipc.Error("Invalid command fields or session state. Use one connect per process and send only after connected.");
        }
    }

    private void Connect(JsonElement root)
    {
        if (connectAttempted) throw new InvalidOperationException();
        string newUid = RequiredString(root, "uid");
        string token = RequiredString(root, "token");
        string deviceId = RequiredString(root, "deviceId");
        string newEn5Id = RequiredString(root, "en5Id");
        string newName = RequiredString(root, "machineName");
        uid = newUid;
        en5Id = newEn5Id;
        machineName = newName;
        connectAttempted = true;
        string auth = JsonSerializer.Serialize(new
        {
            user = new { token, uc_uid = uid, appcode = "EasiNote5" },
            device = new { device_id = deviceId + "_" + uid, device_token = token, system_version = (string?)null, device_model = (string?)null },
            extras = new
            {
                appVersion = "5.2.4.10148", protocolVersion = 23,
                loginTimestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                en5Id, name = machineName,
                osInfo = new { version = Environment.OSVersion.ToString(), isWin7 = false }
            }
        });
        IMWrapper.SetDefaultDns("gateway", "edu.seewo.com", "");
        initialized = true; // Exit even if InitIM throws after partially starting native state.
        if (!IMWrapper.InitIM("mc.seewo.com", AppId, "", uid, "", 1, auth))
            ipc.Error("IM initialization failed; restart the bridge before retrying.");
    }

    private void OnConnected()
    {
        connected = true;
        ipc.Write(new { type = "connected" });
        SendSoftwareInfo();
        Send(904, new { });
    }

    private void SendSoftwareInfo(ushort? platform = null) => Send(903, new
    {
        version = new { major = 5, minor = 2, build = 4, revision = 10148 },
        protocol = 23, clientName = machineName
    }, platform);

    private void Send(int id, object body, ushort? platform = null)
    {
        var response = IMWrapper.SendSingleCommand(new SingleCmdReqWrapper
        {
            toAppid = AppId,
            toUid = [uid],
            toPlatform = platform.HasValue ? [platform.Value] : [.. MobilePlatforms],
            msg = JsonSerializer.SerializeToUtf8Bytes(new { action = "Mobile_Message", content = new { id, en5Id, body } }),
            ttl = 0
        }, 0);
        if (response is null || response.code != 0)
            ipc.Error($"IM send failed for message {id} (code {response?.code}).");
    }

    private void OnMessage(string fromUid, string appId, ushort platform, string messageId, string message)
    {
        if (!connected || fromUid != uid || appId != AppId || !MobilePlatforms.Contains(platform)) return;
        using var document = JsonDocument.Parse(message);
        var root = document.RootElement;
        if (root.GetProperty("action").GetString() != "Mobile_Message") return;
        var content = root.GetProperty("content");
        string? target = content.TryGetProperty("en5Id", out var targetValue) && targetValue.ValueKind != JsonValueKind.Null
            ? targetValue.GetString() : null;
        if (!string.IsNullOrEmpty(target) && target != en5Id) return;
        int id = content.GetProperty("id").GetInt32();
        var body = content.GetProperty("body");
        if (id == 902)
        {
            SendSoftwareInfo(platform);
            Send(904, new { }, platform);
        }
        ipc.Write(new { type = "message", id, body, en5Id = target, uid = fromUid, appId, platform, messageId });
    }

    private static string RequiredString(JsonElement element, string property)
    {
        string? value = element.GetProperty(property).GetString();
        return !string.IsNullOrWhiteSpace(value) && value.Length <= 16384 ? value : throw new ArgumentException();
    }
}
