using System.Text.Json;
using ICC.CE.ENBX.Seewo.Services;

internal static class PlaybackChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        JsonElement Body(object value) => JsonSerializer.SerializeToElement(value);
        MobilePlaybackState? state = new("session-a", "cloud-cid", "演示课件", 12, 1, ["s1", "s2"]);
        var sent = new List<(int Id, JsonElement Body, int? Platform)>();
        var navigations = 0;
        var remote = new MobilePlaybackRemote(() => state, (expected, next, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            navigations++;
            state = state! with { Page = next ? 2 : 1 };
            return Task.FromResult(true);
        }, (id, body, platform) =>
        {
            sent.Add((id, Body(body), platform));
            return Task.CompletedTask;
        }, _ => { });

        await remote.HandleAsync(101, Body(new { }), 2, "list", default);
        check(sent[0].Id == 102 && sent[0].Body.GetProperty("coursewares")[0].GetProperty("id").GetString() == "cloud-cid",
            "remote advertises actual cloud identity");
        var display = sent.Single(m => m.Id == 202);
        check(display.Body.GetProperty("slideindex").GetInt32() == 0 &&
            display.Body.GetProperty("slideid").GetString() == "s1" &&
            display.Body.GetProperty("slidetotal").GetInt32() == 2, "remote advertises slide identity and zero-based index");
        await remote.HandleAsync(205, Body(new { id = "other" }), 2, "wrong", default);
        check(navigations == 0, "remote rejects other courseware identity");
        await remote.HandleAsync(205, Body(new { }), 2, "missing", default);
        check(navigations == 0, "remote requires explicit target identity");
        await remote.HandleAsync(2011, Body(new { id = "other" }), 2, "missing-enter", default);
        check(sent[^1].Id == 2022 && sent[^1].Body.GetProperty("state").GetInt32() == 4,
            "remote enter request for unopened courseware receives failure");
        await remote.HandleAsync(205, Body(new { id = "cloud-cid" }), 2, "next-1", default);
        check(navigations == 1 && sent[^1].Id == 207 && sent[^1].Body.GetProperty("slideindex").GetInt32() == 1 &&
            sent[^1].Body.GetProperty("slideid").GetString() == "s2", "remote next reports committed page");
        await remote.HandleAsync(205, Body(new { id = "cloud-cid" }), 2, "next-1", default);
        check(navigations == 1, "remote transport retransmission does not advance twice");
        await remote.HandleAsync(204, Body(new { id = "cloud-cid" }), 2, "previous-1", default);
        check(navigations == 2 && state?.Page == 1, "remote previous uses same navigation callback");
        await remote.HandleAsync(205, Body(new { id = "cloud-cid" }), 2, "next-2", default);
        check(navigations == 3, "distinct rapid clicks are not deduplicated");
        await remote.HandleAsync(2011, Body(new { id = "cloud-cid" }), 3, "enter", default);
        check(sent[^1].Id == 2022 && sent[^1].Platform == 3 && sent[^1].Body.GetProperty("state").GetInt32() == 0,
            "new enter-display request receives targeted reply");
        var before = sent.Count;
        await remote.PublishAsync();
        check(sent.Count == before + 1 && sent[^1].Id == 207, "page updates do not reannounce display mode");
        state = null;
        await remote.PublishAsync();
        check(sent[^1].Id == 103 && sent[^1].Body.GetProperty("state").GetInt32() == -1,
            "closing presentation withdraws advertised courseware");
        await remote.HandleAsync(205, Body(new { id = "cloud-cid" }), 2, "late", default);
        check(navigations == 3, "remote does not navigate closed presentation");
        await remote.HandleAsync(101, Body(new { }), 2, "empty", default);
        check(sent[^1].Body.GetProperty("coursewares").GetArrayLength() == 0, "closed presentation returns empty list");
        state = new("session-b", "cloud-cid", "新会话", 13, 1, ["new"]);
        await remote.PublishAsync();
        check(sent[^2].Id == 202 && sent[^2].Body.GetProperty("version").GetInt32() == 13,
            "replacement presentation publishes fresh version");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await remote.HandleAsync(205, Body(new { id = "cloud-cid" }), 2, "cancelled", cancelled.Token);
            check(false, "cancelled remote navigation must stop");
        }
        catch (OperationCanceledException) { check(navigations == 3, "cancelled connection cannot navigate"); }
    }
}
