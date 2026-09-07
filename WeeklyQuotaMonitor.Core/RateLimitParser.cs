using System.Text.Json;

namespace WeeklyQuotaMonitor.Core;

/// <summary>
/// 将 Codex App Server 的单桶或多桶额度 JSON 解析为统一的周窗口快照。
/// </summary>
public static class RateLimitParser
{
    private const string DefaultCodexLimitId = "codex";

    /// <summary>
    /// 从 account/rateLimits/read 响应或 account/rateLimits/updated 通知中选择目标额度窗口。
    /// </summary>
    /// <param name="container">result、params 或直接的 rateLimits 对象。</param>
    /// <param name="preferredLimitId">可选的额度桶标识；为空时自动选择最长窗口，并在等长时优先 Codex 主桶。</param>
    /// <param name="minimumWindowMinutes">自动选择时允许的最短窗口，用于排除 5 小时桶。</param>
    /// <param name="sampledAt">本次本地采样时间。</param>
    /// <returns>满足约束的最长窗口；等长时优先 Codex 主桶，没有匹配窗口时返回 null。</returns>
    public static RateLimitSnapshot? Parse(
        JsonElement container,
        string preferredLimitId,
        int minimumWindowMinutes,
        DateTimeOffset sampledAt)
    {
        var candidates = new List<WindowCandidate>();

        if (container.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in byId.EnumerateObject())
            {
                AddBucketCandidates(property.Value, property.Name, candidates);
            }
        }

        if (container.TryGetProperty("rateLimits", out var rateLimits) && rateLimits.ValueKind == JsonValueKind.Object)
        {
            AddBucketCandidates(rateLimits, null, candidates);
        }

        if (container.TryGetProperty("limitId", out _))
        {
            AddBucketCandidates(container, null, candidates);
        }

        var filtered = candidates
            .Where(candidate => candidate.WindowDurationMinutes >= minimumWindowMinutes);

        if (!string.IsNullOrWhiteSpace(preferredLimitId))
        {
            filtered = filtered.Where(candidate =>
                string.Equals(candidate.LimitId, preferredLimitId, StringComparison.Ordinal));
        }

        var selected = filtered
            .OrderByDescending(candidate => candidate.WindowDurationMinutes)
            .ThenByDescending(candidate =>
                string.Equals(candidate.LimitId, DefaultCodexLimitId, StringComparison.Ordinal))
            .ThenBy(candidate => candidate.LimitId, StringComparer.Ordinal)
            .FirstOrDefault();

        return selected is null
            ? null
            : new(
                sampledAt,
                selected.LimitId,
                selected.LimitName,
                selected.UsedPercent,
                selected.WindowDurationMinutes,
                DateTimeOffset.FromUnixTimeSeconds(selected.ResetsAtUnixSeconds));
    }

    /// <summary>
    /// 读取一个额度桶的 primary 和 secondary 窗口并加入候选集合。
    /// </summary>
    private static void AddBucketCandidates(
        JsonElement bucket,
        string? keyLimitId,
        ICollection<WindowCandidate> candidates)
    {
        var limitId = bucket.TryGetProperty("limitId", out var limitIdElement)
            ? limitIdElement.GetString() ?? keyLimitId ?? string.Empty
            : keyLimitId ?? string.Empty;
        var limitName = bucket.TryGetProperty("limitName", out var limitNameElement) && limitNameElement.ValueKind == JsonValueKind.String
            ? limitNameElement.GetString()
            : null;

        AddWindow(bucket, "primary", limitId, limitName, candidates);
        AddWindow(bucket, "secondary", limitId, limitName, candidates);
    }

    /// <summary>
    /// 将存在且字段完整的单个额度窗口转换为候选对象。
    /// </summary>
    private static void AddWindow(
        JsonElement bucket,
        string propertyName,
        string limitId,
        string? limitName,
        ICollection<WindowCandidate> candidates)
    {
        if (!bucket.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (!window.TryGetProperty("usedPercent", out var usedPercent) ||
            !window.TryGetProperty("windowDurationMins", out var duration) ||
            !window.TryGetProperty("resetsAt", out var resetsAt))
        {
            return;
        }

        candidates.Add(new(
            limitId,
            limitName,
            usedPercent.GetDecimal(),
            duration.GetInt32(),
            resetsAt.GetInt64()));
    }

    /// <summary>
    /// 保存解析过程中使用的额度窗口候选值。
    /// </summary>
    private sealed record WindowCandidate(
        string LimitId,
        string? LimitName,
        decimal UsedPercent,
        int WindowDurationMinutes,
        long ResetsAtUnixSeconds);
}
