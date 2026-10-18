using ICC.CE.ENBX.Seewo.Models;

namespace ICC.CE.ENBX.Seewo.Services;

// 此测试只替换详情接口，缓存/HTTP 下载/队列均运行生产代码。
public sealed class SeewoApiClient
{
    public required Func<Task<SeewoDetailResponse?>> Detail { get; init; }
    public Task<SeewoDetailResponse?> GetCoursewareDetailAsync(string cid) => Detail();
}
