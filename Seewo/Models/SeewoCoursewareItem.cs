using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace ICC.CE.ENBX.Seewo.Models;

/// <summary>
/// 课件或目录分组统一模型
/// </summary>
public sealed class SeewoCoursewareItem
{
    [JsonIgnore]
    public bool IsCached { get; set; }

    [JsonIgnore]
    public Visibility FolderVisibility => IsGroup ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility CoursewareVisibility => !IsGroup ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility CachedBadgeVisibility => (!IsGroup && IsCached) ? Visibility.Visible : Visibility.Collapsed;

    [JsonIgnore]
    public Visibility NotCachedBadgeVisibility => (!IsGroup && !IsCached) ? Visibility.Visible : Visibility.Collapsed;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }

    [JsonPropertyName("documentVersion")]
    public string DocumentVersion { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("parentId")]
    public string ParentId { get; set; } = string.Empty;

    [JsonPropertyName("createTime")]
    public long CreateTime { get; set; }

    [JsonPropertyName("create_Time")]
    public long CreateTimeAlt { set => CreateTime = value; }

    [JsonPropertyName("updateTime")]
    public long UpdateTime { get; set; }

    [JsonPropertyName("update_Time")]
    public long UpdateTimeAlt { set => UpdateTime = value; }

    [JsonPropertyName("isGroup")]
    public bool IsGroup { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    [JsonPropertyName("thumb")]
    public string? ThumbUrl { get; set; }

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonIgnore]
    public List<SeewoCoursewareItem> Children { get; } = new();

    [JsonIgnore]
    public string DisplaySize
    {
        get
        {
            if (IsGroup) return "";
            if (Size <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB" };
            double val = Size;
            int unitIdx = 0;
            while (val >= 1024 && unitIdx < units.Length - 1)
            {
                val /= 1024;
                unitIdx++;
            }
            return $"{val:0.##} {units[unitIdx]}";
        }
    }

    [JsonIgnore]
    public string DisplayUpdateTime
    {
        get
        {
            if (UpdateTime <= 0) return "";
            var epoch = UpdateTime > 1000000000000 ? UpdateTime / 1000 : UpdateTime;
            var dt = DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime;
            return dt.ToString("yyyy-MM-dd HH:mm");
        }
    }

    public static SeewoCoursewareItem FromJsonElement(JsonElement el)
    {
        var item = new SeewoCoursewareItem();

        if (TryGetProperty(el, out var idProp, "id", "cid", "remoteId"))
            item.Id = idProp.GetString() ?? "";

        if (TryGetProperty(el, out var nameProp, "name", "title"))
            item.Name = nameProp.GetString() ?? "";

        if (TryGetProperty(el, out var verProp, "version"))
        {
            if (verProp.ValueKind == JsonValueKind.Number) item.Version = verProp.GetInt32();
            else if (int.TryParse(verProp.GetString(), out var v)) item.Version = v;
        }

        if (TryGetProperty(el, out var docVerProp, "documentVersion", "document_version"))
            item.DocumentVersion = docVerProp.GetString() ?? "";

        if (TryGetProperty(el, out var sizeProp, "size"))
        {
            if (sizeProp.ValueKind == JsonValueKind.Number) item.Size = sizeProp.GetInt64();
            else if (long.TryParse(sizeProp.GetString(), out var s)) item.Size = s;
        }

        if (TryGetProperty(el, out var pProp, "parentId", "parent_id", "groupId"))
            item.ParentId = pProp.GetString() ?? "";

        if (TryGetProperty(el, out var groupProp, "isGroup", "is_group", "group"))
        {
            if (groupProp.ValueKind is JsonValueKind.True or JsonValueKind.False) item.IsGroup = groupProp.GetBoolean();
            else if (int.TryParse(groupProp.ToString(), out var g)) item.IsGroup = g != 0;
        }

        if (TryGetProperty(el, out var delProp, "isDeleted", "is_deleted", "deleted"))
        {
            if (delProp.ValueKind is JsonValueKind.True or JsonValueKind.False) item.IsDeleted = delProp.GetBoolean();
            else if (int.TryParse(delProp.ToString(), out var d)) item.IsDeleted = d != 0;
        }

        if (TryGetProperty(el, out var utProp, "update_Time", "updateTime", "update_time"))
        {
            if (utProp.ValueKind == JsonValueKind.Number) item.UpdateTime = utProp.GetInt64();
            else if (long.TryParse(utProp.GetString(), out var t)) item.UpdateTime = t;
        }

        if (TryGetProperty(el, out var ctProp, "create_Time", "createTime", "create_time"))
        {
            if (ctProp.ValueKind == JsonValueKind.Number) item.CreateTime = ctProp.GetInt64();
            else if (long.TryParse(ctProp.GetString(), out var t)) item.CreateTime = t;
        }

        if (TryGetProperty(el, out var thumbProp, "thumb", "thumbnail", "photoUrl", "cover"))
            item.ThumbUrl = thumbProp.GetString();

        if (TryGetProperty(el, out var authProp, "author"))
            item.Author = authProp.GetString();

        return item;
    }

    private static bool TryGetProperty(JsonElement el, out JsonElement result, params string[] candidateNames)
    {
        foreach (var name in candidateNames)
        {
            if (el.TryGetProperty(name, out result)) return true;
        }
        foreach (var prop in el.EnumerateObject())
        {
            foreach (var name in candidateNames)
            {
                if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    result = prop.Value;
                    return true;
                }
            }
        }
        result = default;
        return false;
    }
}
