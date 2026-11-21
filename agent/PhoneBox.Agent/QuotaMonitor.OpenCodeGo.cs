using System.Globalization;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// OpenCode Go 的凭据读取: 优先用设置里的 <see cref="OpenCodeGoConfig"/>（config.json），
/// 兼容早期版本的会话文件 %APPDATA%\PhoneBox\ocgo-session.json（首次启动会迁移并改名）。
/// </summary>
internal static class OpenCodeGoCreds
{
    internal static readonly string LegacySessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneBox", "ocgo-session.json");

    /// <summary>Returns the configured key and workspace id; both empty when未配置.</summary>
    internal static (string Key, string Org) Read(ConfigStore? store)
    {
        try
        {
            if (store?.Get().OpenCodeGo is { } cfg && !string.IsNullOrWhiteSpace(cfg.Key))
                return (cfg.Key.Trim(), cfg.Org?.Trim() ?? "");
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // 配置读不出来时退回旧文件
        }
        return ReadLegacy();
    }

    /// <summary>Reads the pre-config session file, if it is still around.</summary>
    internal static (string Key, string Org) ReadLegacy()
    {
        try
        {
            if (!File.Exists(LegacySessionPath)) return ("", "");
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacySessionPath));
            var root = doc.RootElement;
            var key = root.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()?.Trim() ?? "" : "";
            var org = root.TryGetProperty("org", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()?.Trim() ?? "" : "";
            return (key, org);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return ("", "");
        }
    }

    /// <summary>
    /// Moves a session file written by an earlier version into the settings, then renames it so it is not read again.
    /// Does nothing when the settings already hold a key or the file is absent.
    /// </summary>
    internal static void MigrateLegacySession(ConfigStore store)
    {
        if (store.Get().OpenCodeGo is { Key.Length: > 0 }) return;
        var (key, org) = ReadLegacy();
        if (key.Length == 0) return;

        var config = store.Get();
        config.OpenCodeGo.Key = key;
        if (!string.IsNullOrWhiteSpace(org)) config.OpenCodeGo.Org = org;
        var err = store.Save(JsonSerializer.Serialize(config, ConfigStore.WriteOpts), null, out _);
        if (err is not null) return;   // 保存失败就保留旧文件, 下次再试

        try { File.Move(LegacySessionPath, LegacySessionPath + ".migrated", overwrite: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 改名失败无碍: 设置里已有 key, Read() 优先用设置
        }
    }
}

/// <summary>
/// OpenCode Go 额度: 用设置里的 API key 调 GET https://opencode.ai/console/api/go/status,
/// 取 access.meters 的三个窗口映射成 5 小时 / 本周 / 本月.
/// 面板侧无需改动 —— QuotaMonitor.Snapshot() 会把非 claude/codex/antigravity 的 provider 作为 extra 下发.
/// </summary>
public sealed partial class QuotaMonitor
{
    private volatile QuotaProvider? _openCodeGo;
    private long _nextOpenCodeGo;

    private const string OpenCodeStatusUrl = "https://opencode.ai/console/api/go/status";

    /// <summary>Fetches the Go subscription meters and maps them to the three usage windows.</summary>
    private async Task<bool> RefreshOpenCodeGo(CancellationToken ct)
    {
        var (key, org) = OpenCodeGoCreds.Read(config);
        if (key.Length == 0)
        {
            _openCodeGo = null;   // 未配置凭据: 不显示该 provider
            return false;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, OpenCodeStatusUrl);
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key);
            if (org.Length > 0) req.Headers.TryAddWithoutValidation("x-org-id", org);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "PhoneBox-agent/" + typeof(QuotaMonitor).Assembly.GetName().Version);

            using var res = await Http.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode)
            {
                var code = (int)res.StatusCode;
                var message = code == 403 ? "API key 缺少 Billing 权限" : $"HTTP {code}";
                _openCodeGo = _openCodeGo is { Limits.Count: > 0 } old
                    ? old with { Error = message }
                    : new QuotaProvider("opencodego", "OpenCode Go", "OpenCode Go", [], null, null, message);
                log.LogDebug("opencode go: HTTP {Code}", code);
                return false;
            }

            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("access", out var access) ||
                !access.TryGetProperty("meters", out var meters))
            {
                log.LogDebug("opencode go: response without access.meters");
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var limits = new List<QuotaLimit>();
            AddMeter(limits, meters, "fiveHour", "5 小时", 18_000);
            AddMeter(limits, meters, "week", "本周", 604_800);
            AddMeter(limits, meters, "month", "本月", 2_592_000);

            var product = root.TryGetProperty("renewalProduct", out var rp) && rp.ValueKind == JsonValueKind.String
                ? rp.GetString() : null;
            var planName = product switch
            {
                "go-plus" => "OpenCode Go Plus",
                "go-teams" => "OpenCode Go Teams",
                _ => "OpenCode Go"
            };
            var endsAt = access.TryGetProperty("endsAt", out var ea) && ea.ValueKind == JsonValueKind.String
                ? ea.GetString() : null;
            var plan = string.IsNullOrEmpty(endsAt) || endsAt.Length < 10
                ? planName
                : $"{planName} · 续费 {endsAt[..10]}";

            _openCodeGo = limits.Count == 0
                ? null
                : new QuotaProvider("opencodego", "OpenCode Go", plan, limits, null, now, null);
            return limits.Count > 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            log.LogDebug("opencode go: {Message}", ex.Message);
            _openCodeGo = _openCodeGo is { Limits.Count: > 0 } prev
                ? prev with { Error = "OpenCode Go 请求失败" }
                : null;
            return false;
        }
    }

    /// <summary>Adds one meter as a limit; microCents are integer strings, so go through decimal.</summary>
    private static void AddMeter(List<QuotaLimit> into, JsonElement meters, string name, string label, long windowSec)
    {
        if (!meters.TryGetProperty(name, out var meter) || meter.ValueKind != JsonValueKind.Object) return;
        var limit = MicroCents(meter, "limitMicroCents");
        if (limit <= 0) return;
        var used = MicroCents(meter, "usedMicroCents");
        var percent = (double)(used / limit * 100m);
        var resetsAt = meter.TryGetProperty("resetsAt", out var ra) && ra.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(ra.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var dt)
            ? dt.ToUnixTimeMilliseconds() : (long?)null;
        var severity = percent >= 95 ? "critical" : percent >= 80 ? "warning" : "normal";
        into.Add(new QuotaLimit(label, Math.Round(percent, 2), resetsAt, windowSec, severity));
    }

    /// <summary>Reads a microCents field, which the API sends as a string, as a decimal amount.</summary>
    private static decimal MicroCents(JsonElement obj, string key)
    {
        if (!obj.TryGetProperty(key, out var v)) return 0;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }
}
