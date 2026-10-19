using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using ICC.CE.ENBX.Seewo.Models;

namespace ICC.CE.ENBX.Seewo.Services;

public sealed class SeewoApiClient
{
    private const string BaseUrl = "https://edu.seewo.com";
    private readonly HttpClient _httpClient;
    private readonly SeewoAuthManager _authManager;
    private readonly Action<string>? _logger;

    public SeewoApiClient(SeewoAuthManager authManager, Action<string>? logger = null)
    {
        _authManager = authManager;
        _logger = logger;
        var handler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25)
        };
    }

    public void Log(string message)
    {
        _logger?.Invoke("[SeewoApi] " + message);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string pathAndQuery, SeewoAuthProfile? account = null)
    {
        var url = pathAndQuery.StartsWith("http", StringComparison.OrdinalIgnoreCase) 
            ? pathAndQuery 
            : BaseUrl + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);

        var request = new HttpRequestMessage(method, url);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        // 希沃标准客户端 Headers
        request.Headers.TryAddWithoutValidation("x-auth-app", "EasiNote5");
        request.Headers.TryAddWithoutValidation("x-auth-timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; WOW64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/108.0.0.0 Safari/537.36 EasiNote5/5.2.4.10148");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // 组装 Cookies
        var profile = account ?? _authManager.CurrentProfile;
        if (profile != null && !string.IsNullOrWhiteSpace(profile.Token))
        {
            request.Headers.TryAddWithoutValidation("x-auth-token", profile.Token);

            var cookieParts = new List<string>
            {
                $"x-auth-token={profile.Token}",
                "x-auth-app=EasiNote5",
                "client_version=5.2.4.10148",
                "client_flags=tabs"
            };

            foreach (var kvp in profile.Cookies)
            {
                if (kvp.Key.Equals("x-auth-token", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Equals("x-auth-app", StringComparison.OrdinalIgnoreCase) ||
                    kvp.Key.Equals("x-auth-brand", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                cookieParts.Add($"{kvp.Key}={kvp.Value}");
            }

            request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookieParts));
        }

        return request;
    }

    /// <summary>
    /// 尝试调用用户信息 API 获取真实头像与昵称
    /// </summary>
    public async Task<bool> FetchAndUpdateUserProfileAsync()
    {
        var profile = _authManager.CurrentProfile;
        if (profile == null) return false;
        var updated = await FetchUserIdentityAsync(profile);
        if (updated) _authManager.UpdateProfile(profile);
        return updated;
    }

    public async Task<bool> FetchUserIdentityAsync(SeewoAuthProfile profile)
    {
        var endpoints = new[]
        {
            "https://edu.seewo.com/api/v3/user/info",
            "https://id.seewo.com/api/v1/user",
            "https://account.seewo.com/api/user/info"
        };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var req = CreateRequest(HttpMethod.Get, endpoint, profile);
                using var resp = await _httpClient.SendAsync(req);
                var rawJson = await resp.Content.ReadAsStringAsync();
                Log($"FetchUserProfile from {endpoint} status: {resp.StatusCode}");

                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(rawJson);
                    var root = doc.RootElement;
                    var targetObj = root;
                    if (root.TryGetProperty("data", out var dProp) && dProp.ValueKind == JsonValueKind.Object)
                        targetObj = dProp;

                    static string ReadLabel(JsonElement obj, params string[] names)
                    {
                        if (obj.ValueKind != JsonValueKind.Object) return string.Empty;
                        foreach (var key in names)
                        {
                            if (!obj.TryGetProperty(key, out var value)) continue;
                            if (value.ValueKind == JsonValueKind.String) return value.GetString() ?? string.Empty;
                            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String) return label.GetString() ?? string.Empty;
                        }
                        return string.Empty;
                    }
                    var stage = ReadLabel(targetObj, "stageName", "schoolStageName", "phaseName", "stage");
                    var subject = ReadLabel(targetObj, "subjectName", "subject");
                    if (targetObj.TryGetProperty("userInfoExtendVo", out var teaching))
                    {
                        if (string.IsNullOrWhiteSpace(stage)) stage = ReadLabel(teaching, "stageName", "schoolStageName", "phaseName", "stage");
                        if (string.IsNullOrWhiteSpace(subject)) subject = ReadLabel(teaching, "subjectName", "subject");
                    }

                    string? avatar = null;
                    if (targetObj.TryGetProperty("photoUrl", out var p1) || 
                        targetObj.TryGetProperty("avatar", out p1) || 
                        targetObj.TryGetProperty("avatarUrl", out p1) ||
                        targetObj.TryGetProperty("headUrl", out p1))
                    {
                        avatar = p1.GetString();
                    }
                    if (string.IsNullOrWhiteSpace(avatar) && targetObj.TryGetProperty("userInfoExtendVo", out var extendVo) && extendVo.ValueKind == JsonValueKind.Object)
                    {
                        if (extendVo.TryGetProperty("picUrl", out var picUrlProp))
                            avatar = picUrlProp.GetString();
                    }

                    string? name = null;
                    if (targetObj.TryGetProperty("nickName", out var n1) ||
                        targetObj.TryGetProperty("nickname", out n1) ||
                        targetObj.TryGetProperty("realName", out n1) ||
                        targetObj.TryGetProperty("name", out n1) ||
                        targetObj.TryGetProperty("username", out n1))
                    {
                        name = n1.GetString();
                    }

                    string? uid = null;
                    if (targetObj.TryGetProperty("uid", out var u1))
                        uid = u1.ToString();

                    if (!string.IsNullOrWhiteSpace(avatar) || !string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(uid) || !string.IsNullOrWhiteSpace(stage) || !string.IsNullOrWhiteSpace(subject))
                    {
                        if (!string.IsNullOrWhiteSpace(stage)) profile.StageName = stage;
                        if (!string.IsNullOrWhiteSpace(subject)) profile.SubjectName = subject;
                        if (!string.IsNullOrWhiteSpace(avatar)) profile.AvatarUrl = avatar;
                        if (!string.IsNullOrWhiteSpace(name)) profile.NickName = name;
                        if (!string.IsNullOrWhiteSpace(uid)) profile.Uid = uid;
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"FetchUserProfile failed for {endpoint}: {ex.Message}");
            }
        }
        return false;
    }

    /// <summary>
    /// 验证当前 Token 是否依然有效
    /// </summary>
    public async Task<bool> ValidateTokenAsync()
    {
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/api/courseware-core/recent/list?recentSize=1");
            using var resp = await _httpClient.SendAsync(req);
            var rawJson = await resp.Content.ReadAsStringAsync();
            Log($"ValidateToken Status: {resp.StatusCode}, Length: {rawJson.Length}");

            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(rawJson);
            if (doc.RootElement.TryGetProperty("code", out var codeProp))
            {
                return codeProp.GetInt32() == 0;
            }
            return true;
        }
        catch (Exception ex)
        {
            Log($"ValidateToken Exception: {ex.Message}");
            return false;
        }
    }

    // null 表示网络或服务异常；只有明确的认证错误才返回 false。
    public string? LastValidationError { get; private set; }

    internal static bool? ClassifyAccountResponse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        // 此服务的错误响应使用 error_code，部分成功响应直接返回 data/content。
        if (root.TryGetProperty("error_code", out var error) && error.ToString() != "0")
            return error.ToString() is "401" or "403" or "4011" ? false : null;
        if (root.TryGetProperty("code", out var code))
        {
            var value = code.ToString();
            if (value is "401" or "403" or "4011") return false;
            if (value != "0") return null;
            return true;
        }
        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return null;
        foreach (var name in new[] { "data", "content", "items", "list" })
        {
            if (!root.TryGetProperty(name, out var data)) continue;
            if (data.ValueKind == JsonValueKind.Array) return true;
            if (data.ValueKind != JsonValueKind.Object) continue;
            foreach (var key in new[] { "content", "items", "list" })
                if (data.TryGetProperty(key, out var items) && items.ValueKind == JsonValueKind.Array) return true;
        }
        return null;
    }

    public async Task<bool?> ValidateAccountAsync(SeewoAuthProfile profile)
    {
        LastValidationError = null;
        if (!profile.IsValid || profile.Token == "user_authenticated")
        {
            LastValidationError = "保存的登录凭证无效，请重新登录。";
            return false;
        }
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "/api/courseware-core/recent/list?recentSize=1", profile);
            using var resp = await _httpClient.SendAsync(req);
            LastValidationError = $"希沃服务返回 HTTP {(int)resp.StatusCode}。";
            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden) return false;
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var result = ClassifyAccountResponse(root);
            if (result == true) LastValidationError = null;
            else if (root.ValueKind == JsonValueKind.Object)
            {
                var code = root.TryGetProperty("error_code", out var ec) ? ec.ToString() : root.TryGetProperty("code", out var c) ? c.ToString() : "未提供";
                LastValidationError = $"希沃服务返回未通过的账号校验（HTTP {(int)resp.StatusCode}，业务码 {code}）。可尝试重新登录。";
                Log($"Account validation: HTTP {(int)resp.StatusCode}, code={code}, properties={string.Join(",", root.EnumerateObject().Select(x => x.Name))}");
            }
            return result;
        }
        catch (TaskCanceledException) { LastValidationError = "连接希沃服务超时，请稍后重试。"; return null; }
        catch (HttpRequestException ex) { LastValidationError = "连接希沃服务失败，请检查网络或代理设置。"; Log($"Account validation transport error: {ex.GetType().Name}"); return null; }
        catch (JsonException) { LastValidationError = "希沃服务返回了无法识别的内容，请稍后重试。"; return null; }
    }

    /// <summary>
    /// 获取最近打开/修改的课件
    /// </summary>
    public async Task<List<SeewoCoursewareItem>> GetRecentListAsync(int size = 100)
    {
        var url = $"/api/courseware-core/recent/list?recentSize={size}";
        Log($"Requesting GetRecentListAsync: {url}");

        using var req = CreateRequest(HttpMethod.Get, url);
        using var resp = await _httpClient.SendAsync(req);
        var rawJson = await resp.Content.ReadAsStringAsync();
        Log($"GetRecentListAsync Status: {resp.StatusCode}, Raw: {(rawJson.Length > 500 ? rawJson.Substring(0, 500) + "..." : rawJson)}");

        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(rawJson);
        var items = ExtractCoursewareItems(doc.RootElement);
        Log($"GetRecentListAsync parsed {items.Count} items.");
        return items;
    }

    /// <summary>
    /// 获取全部课件与文件夹列表（分页自动拉取合并）
    /// </summary>
    public async Task<List<SeewoCoursewareItem>> GetAllGroupsAndCoursewaresAsync()
    {
        var account = _authManager.CurrentProfile;
        var allItems = new List<SeewoCoursewareItem>();
        int pageIndex = 0;
        const int pageSize = 500;
        bool hasNext = true;

        while (hasNext)
        {
            var url = $"/api/courseware-core/group/list?index={pageIndex}&pagesize={pageSize}";
            Log($"Requesting GetAllGroupsAndCoursewaresAsync: {url}");

            using var req = CreateRequest(HttpMethod.Get, url, account);
            using var resp = await _httpClient.SendAsync(req);
            var rawJson = await resp.Content.ReadAsStringAsync();
            Log($"GetAllGroupsAndCoursewaresAsync (page {pageIndex}) Status: {resp.StatusCode}, Raw: {(rawJson.Length > 500 ? rawJson.Substring(0, 500) + "..." : rawJson)}");

            resp.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(rawJson);
            var pageItems = ExtractCoursewareItems(doc.RootElement);
            allItems.AddRange(pageItems);

            // 检查分页 hasNext
            hasNext = false;
            if (doc.RootElement.TryGetProperty("data", out var dataProp) && dataProp.ValueKind == JsonValueKind.Object)
            {
                if (dataProp.TryGetProperty("hasNext", out var hasNextProp) && hasNextProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    hasNext = hasNextProp.GetBoolean();
                }
            }
            else if (doc.RootElement.TryGetProperty("hasNext", out var hProp) && hProp.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                hasNext = hProp.GetBoolean();
            }

            pageIndex++;
            if (pageIndex > 20) break; // 防死循环
        }

        Log($"GetAllGroupsAndCoursewaresAsync finished. Total parsed items: {allItems.Count}");
        return allItems;
    }

    /// <summary>
    /// 兼容 content, items, list, 或 data 本身即数组的多种服务端返回结构
    /// </summary>
    private List<SeewoCoursewareItem> ExtractCoursewareItems(JsonElement root)
    {
        var list = new List<SeewoCoursewareItem>();

        JsonElement arrayElement = default;
        bool foundArray = false;

        // 1. 尝试 data 属性
        if (root.TryGetProperty("data", out var dataProp))
        {
            if (dataProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = dataProp;
                foundArray = true;
            }
            else if (dataProp.ValueKind == JsonValueKind.Object)
            {
                // 优先 content（希沃官方定义），其次 items, list
                if (dataProp.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = cProp;
                    foundArray = true;
                }
                else if (dataProp.TryGetProperty("items", out var iProp) && iProp.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = iProp;
                    foundArray = true;
                }
                else if (dataProp.TryGetProperty("list", out var lProp) && lProp.ValueKind == JsonValueKind.Array)
                {
                    arrayElement = lProp;
                    foundArray = true;
                }
            }
        }

        // 2. 尝试根对象上的 content / items
        if (!foundArray)
        {
            if (root.TryGetProperty("content", out var cProp) && cProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = cProp;
                foundArray = true;
            }
            else if (root.TryGetProperty("items", out var iProp) && iProp.ValueKind == JsonValueKind.Array)
            {
                arrayElement = iProp;
                foundArray = true;
            }
        }

        if (foundArray)
        {
            foreach (var itemEl in arrayElement.EnumerateArray())
            {
                try
                {
                    var item = SeewoCoursewareItem.FromJsonElement(itemEl);
                    if (!item.IsDeleted && (!string.IsNullOrWhiteSpace(item.Id) || !string.IsNullOrWhiteSpace(item.Name)))
                    {
                        list.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    Log($"Error parsing single item: {ex.Message}");
                }
            }
        }
        else
        {
            Log("No courseware array found in response JSON!");
        }

        return list;
    }

    /// <summary>
    /// 获取课件完整文件明细与下载直链
    /// </summary>
    public async Task<SeewoDetailResponse?> GetCoursewareDetailAsync(string cid)
    {
        var url = $"/api/v1/courseware/detail?cid={Uri.EscapeDataString(cid)}";
        Log($"Requesting GetCoursewareDetailAsync: {url}");

        using var req = CreateRequest(HttpMethod.Get, url);
        using var resp = await _httpClient.SendAsync(req);
        var rawJson = await resp.Content.ReadAsStringAsync();
        Log($"GetCoursewareDetailAsync Status: {resp.StatusCode}, Length: {rawJson.Length}");

        resp.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<SeewoDetailResponse>(rawJson);
    }
}
