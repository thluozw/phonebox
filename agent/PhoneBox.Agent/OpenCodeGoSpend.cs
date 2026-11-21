using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// OpenCode Go 花费: 用同一份 API key 每 5 分钟读 GET /console/api/activity/go,
/// 把「今日花费 / 本期花费」直接写进代理自己的 PushStore(POST /api/push 的同一个存储),
/// 因此面板上的 push 组件照常显示, 无需外部脚本或计划任务.
/// 凭据来源与 QuotaMonitor.OpenCodeGo 一致: 设置里的 openCodeGo.key / .org。
/// </summary>
public sealed class OpenCodeGoSpend(ConfigStore config, PushStore push, ILogger<OpenCodeGoSpend> log) : BackgroundService
{
    private const string ActivityUrl = "https://opencode.ai/console/api/activity/go";
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(20) };

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // 启动后先等几秒, 让面板先把已有值画出来
        try { await Task.Delay(TimeSpan.FromSeconds(8), ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogDebug("opencode go spend: {Message}", ex.Message);
            }

            try { await Task.Delay(Interval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        var (key, org) = OpenCodeGoCreds.Read(config);
        if (key.Length == 0) return;

        using var req = new HttpRequestMessage(HttpMethod.Get, ActivityUrl);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
        if (org.Length > 0) req.Headers.TryAddWithoutValidation("x-org-id", org);
        req.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            log.LogDebug("opencode go activity: HTTP {Code}", (int)res.StatusCode);
            return;
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var today = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        JsonElement? todayRow = null;
        JsonElement? lastRow = null;
        decimal totalCost = 0;
        long totalRequests = 0;
        var days = 0;

        if (root.TryGetProperty("daily", out var daily) && daily.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in daily.EnumerateArray())
            {
                lastRow = row;
                var cost = Usd(row, "totalCostMicroCents");
                totalCost += cost;
                totalRequests += (long)Num(row, "totalRequests");
                days++;
                if (row.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String
                    && string.Equals(d.GetString(), today, StringComparison.Ordinal))
                {
                    todayRow = row;
                }
            }
        }

        var chosen = todayRow ?? lastRow;
        if (chosen is { } row2)
        {
            var cost = Usd(row2, "totalCostMicroCents");
            var requests = (long)Num(row2, "totalRequests");
            var topModel = TopModel(root);
            var sub = $"今日 {requests} 次请求" + (topModel is null ? "" : $" · 主打 {topModel}");
            push.Set("ocgo_today", JsonSerializer.SerializeToElement(new
            {
                value = "$" + cost.ToString("0.00", CultureInfo.InvariantCulture),
                sub,
                level = "ok"
            }), null);
        }

        var startsAt = root.TryGetProperty("startsAt", out var sa) && sa.ValueKind == JsonValueKind.String
            ? sa.GetString() : null;
        push.Set("ocgo_mtd", JsonSerializer.SerializeToElement(new
        {
            value = "$" + totalCost.ToString("0.00", CultureInfo.InvariantCulture),
            sub = $"本期 {days} 天 · {totalRequests} 次请求" +
                  (string.IsNullOrEmpty(startsAt) || startsAt.Length < 10 ? "" : $"（自 {startsAt[..10]}）"),
            level = "ok"
        }), null);
    }

    private static string? TopModel(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return null;
        foreach (var m in models.EnumerateArray())
        {
            if (m.TryGetProperty("model", out var name) && name.ValueKind == JsonValueKind.String)
                return name.GetString();
            break;
        }
        return null;
    }

    private static decimal Usd(JsonElement row, string field) => Num(row, field) / 100_000_000m;

    private static decimal Num(JsonElement row, string field)
    {
        if (!row.TryGetProperty(field, out var v)) return 0;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
