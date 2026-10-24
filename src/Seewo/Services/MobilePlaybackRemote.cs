using System.Text.Json;

namespace ICC.CE.ENBX.Seewo.Services;

// 状态快照由 UI 线程替换；遥控队列读取快照，不能持有已关闭文档的可变状态。
internal sealed record MobilePlaybackState(string SessionId, string Id, string Name, int Version,
    int Page, string[] SlideIds)
{
    public string SlideId => SlideIds.ElementAtOrDefault(Page - 1) ?? "";
}

internal sealed class MobilePlaybackRemote(
    Func<MobilePlaybackState?> getState,
    Func<MobilePlaybackState, bool, CancellationToken, Task<bool>> navigate,
    Func<int, object, int?, Task> send,
    Action<string> log)
{
    private MobilePlaybackState? _announced;
    private readonly HashSet<(int Platform, string Id)> _seen = new();
    private readonly Queue<(int Platform, string Id)> _recent = new();

    public static bool Handles(int id) => id is 101 or 201 or 2011 or 204 or 205 or 902;

    private static object Shell(MobilePlaybackState s, int state = 1) => new
    {
        id = s.Id, state, name = s.Name, version = s.Version, parentid = "", sync = false
    };

    private static object Display(MobilePlaybackState s) => new
    {
        id = s.Id, state = 0, version = s.Version, slideindex = s.Page - 1,
        slideid = s.SlideId, slidetotal = s.SlideIds.Length, animationid = "", download = 100
    };

    public async Task PublishAsync(int? platform = null)
    {
        var state = getState();
        if (_announced != null && _announced.SessionId != state?.SessionId)
            await send(103, Shell(_announced, -1), platform);
        if (state != null)
        {
            if (platform.HasValue || _announced?.SessionId != state.SessionId)
            {
                await send(103, Shell(state), platform);
                await send(202, Display(state), platform);
            }
            await SendPageAsync(state, platform);
        }
        _announced = state;
    }

    private Task SendPageAsync(MobilePlaybackState s, int? platform) => send(207, new
    {
        id = s.Id, slideindex = s.Page - 1, slideid = s.SlideId, animationid = ""
    }, platform);

    public async Task HandleAsync(int id, JsonElement body, int platform, string messageId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (id == 902) { await PublishAsync(platform); return; }
        var state = getState();
        if (id == 101)
        {
            await send(102, new { coursewares = state == null ? Array.Empty<object>() : new[] { Shell(state) } }, platform);
            if (state != null) await PublishAsync(platform);
            return;
        }
        var target = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("id", out var value) &&
            value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (state == null || target != state.Id)
        {
            if (id is 201 or 2011)
                await send(id == 201 ? 202 : 2022, new { id = target ?? "", state = 4 }, platform);
            log($"忽略遥控消息 {id}：目标课件未在放映。");
            return;
        }
        if (id is 201 or 2011)
        {
            await send(id == 201 ? 202 : 2022, Display(state), platform);
            return;
        }
        // 仅对传输消息 ID 去重；不同 ID 的连续点击必须按顺序执行。
        if (messageId.Length > 0)
        {
            var key = (platform, messageId);
            if (!_seen.Add(key)) return;
            _recent.Enqueue(key);
            if (_recent.Count > 512) _seen.Remove(_recent.Dequeue());
        }
        if (!await navigate(state, id == 205, ct)) return;
        var after = getState();
        if (after?.SessionId == state.SessionId) await SendPageAsync(after, platform);
    }
}
