using System.Text.Json.Serialization;

namespace ICC.CE.ENBX.Seewo.Models;

/// <summary>
/// 希沃登录凭证与用户信息
/// </summary>
public sealed class SeewoAuthProfile
{
    private string _token = string.Empty;

    public string AccountId { get; set; } = Guid.NewGuid().ToString("N");
    public string Remark { get; set; } = string.Empty;
    public bool IsExpired { get; set; }
    public DateTime LastUsedTime { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("token")]
    public string Token
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_token)) return _token;
            if (Cookies != null)
            {
                if (Cookies.TryGetValue("x-token", out var xt) && !string.IsNullOrWhiteSpace(xt)) return xt;
                if (Cookies.TryGetValue("x-auth-token", out var xat) && !string.IsNullOrWhiteSpace(xat)) return xat;
                if (Cookies.TryGetValue("x-samesite-none-token", out var xsnt) && !string.IsNullOrWhiteSpace(xsnt)) return xsnt;
                if (Cookies.TryGetValue("token", out var t) && !string.IsNullOrWhiteSpace(t)) return t;
            }
            return string.Empty;
        }
        set => _token = value;
    }

    [JsonPropertyName("uid")]
    public string Uid { get; set; } = string.Empty;

    [JsonPropertyName("userName")]
    public string UserName { get; set; } = string.Empty;

    [JsonPropertyName("nickName")]
    public string NickName { get; set; } = string.Empty;

    [JsonPropertyName("phone")]
    public string Phone { get; set; } = string.Empty;

    [JsonPropertyName("avatarUrl")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonPropertyName("loginTime")]
    public DateTime LoginTime { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("cookies")]
    public Dictionary<string, string> Cookies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool IsValid => !string.IsNullOrWhiteSpace(Token);

    [JsonIgnore]
    public string DisplayName => !string.IsNullOrWhiteSpace(Remark) ? Remark : !string.IsNullOrWhiteSpace(NickName)
        ? NickName 
        : (!string.IsNullOrWhiteSpace(UserName) ? UserName : (string.IsNullOrWhiteSpace(Phone) ? "希沃用户" : Phone));
}
