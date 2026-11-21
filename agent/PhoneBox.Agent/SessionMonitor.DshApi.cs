using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

/// <summary>
/// DeepSeek Harness sessions through the harness's own local HTTP API, which is strictly better than reading the
/// session files: one call reports every session with its title, model, running flag, token totals and — the part a
/// file reader cannot know — whether the session is waiting for the user to answer a question.
///
/// The server picks a random loopback port at launch and requires a signed cookie on every route:
///   cookie name  = "dsh-auth-" + base64url(sha256(authority))
///   cookie value = "v1." + base64url(json) + "." + base64url(HMAC-SHA256(secret, base64url(json)))
///   json         = {"version":1,"authority":"127.0.0.1:PORT","issuedAt":ms,"expiresAt":ms}
/// The HMAC key is the browser-session secret in %USERPROFILE%\.dsh\.credentials.yaml, base64url-decoded to 32 bytes.
/// Everything stays on this computer: no launch token, no browser, no network egress.
/// </summary>
public sealed partial class SessionMonitor
{
    private static readonly HttpClient DshHttp = new(new SocketsHttpHandler
    {
        UseProxy = false,
        UseCookies = false,
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    { Timeout = TimeSpan.FromSeconds(5) };

    private int _dshPort;
    private long _dshPortCheckedAt;

    /// <summary>Reads every DSH session through the local API, or null when the harness cannot be reached.</summary>
    private async Task<IReadOnlyList<AgentSession>?> ReadDshApiAsync(CancellationToken ct)
    {
        var secret = DshSecret();
        if (secret is null) return null;

        var port = await DshPortAsync(secret, ct);
        if (port == 0) return null;

        var authority = "127.0.0.1:" + port;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://{authority}/api/session/list")
        {
            Content = new StringContent(
                """{"type":"client-request","rpcId":"1","method":"session/list","payload":{"args":{"_request":{}}}}""",
                Encoding.UTF8, "application/json")
        };
        request.Headers.TryAddWithoutValidation("Cookie", DshCookie(secret, authority));

        JsonDocument doc;
        try
        {
            using var response = await DshHttp.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode is 401 or 403 or 404) InvalidateDshPort();
                return null;
            }
            doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            InvalidateDshPort();
            return null;
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result)
                || result.ValueKind != JsonValueKind.Object
                || !result.TryGetProperty("value", out var value)
                || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array)
                return null;

            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var windowMs = Math.Max(1, DshWindowHours) * 3_600_000L;
            var list = new List<(long Updated, AgentSession Session)>();

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var id = Str(item, "sessionId");
                if (string.IsNullOrEmpty(id)) continue;

                var updated = Long(item, "updatedAt") ?? 0;
                if (updated > 0 && now - updated > windowMs) continue;
                if (Str(item, "origin") is { } origin && origin == "subagent" && !DshIncludeSubagents) continue;
                if (DshBool(item, "blank") == true) continue;

                var values = item.TryGetProperty("projections", out var proj) && proj.ValueKind == JsonValueKind.Object
                    && proj.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Object
                        ? vals : default;

                var title = values.ValueKind == JsonValueKind.Object ? Str(values, "title") : null;
                var model = values.ValueKind == JsonValueKind.Object
                    && values.TryGetProperty("modelSelection", out var ms) && ms.ValueKind == JsonValueKind.Object
                    && ms.TryGetProperty("lastUsed", out var lastUsed) && lastUsed.ValueKind == JsonValueKind.Object
                        ? Str(lastUsed, "model") : null;

                var pending = 0;
                if (values.ValueKind == JsonValueKind.Object
                    && values.TryGetProperty("userQuestions", out var uq) && uq.ValueKind == JsonValueKind.Object
                    && uq.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.Array)
                    pending = active.GetArrayLength();

                long? totalTokens = null;
                if (values.ValueKind == JsonValueKind.Object
                    && values.TryGetProperty("tokenUsage", out var tu) && tu.ValueKind == JsonValueKind.Object)
                {
                    var sum = (Long(tu, "uncachedInputTokens") ?? 0) + (Long(tu, "outputTokens") ?? 0)
                        + (Long(tu, "cacheReadTokens") ?? 0) + (Long(tu, "cacheWriteTokens") ?? 0);
                    if (sum > 0) totalTokens = sum;
                }

                long? lastPrompt = null;
                if (values.ValueKind == JsonValueKind.Object
                    && values.TryGetProperty("sessionListMetadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    lastPrompt = Long(meta, "lastPromptAt");

                var running = DshBool(item, "running") == true;
                var available = DshBool(item, "agentAvailable") != false;
                string status;
                string? waitingFor = null;
                if (pending > 0)
                {
                    status = "waiting";
                    waitingFor = pending == 1 ? "等待你回答" : $"等待你回答（{pending} 个提问）";
                }
                else if (running) status = "busy";
                // agentAvailable=false just means the harness no longer has an agent attached to it; "shell" is Claude's
                // own state and would read as a wrong label on the phone.
                else if (!available) status = "idle";
                else status = "idle";

                var cwd = Str(item, "cwd") ?? "";
                var name = string.IsNullOrWhiteSpace(title) ? ShortDshId(id) : title!;
                var since = updated > 0 ? updated : now;

                list.Add((since, new AgentSession(
                    Id: "dsh:" + id,
                    Tool: "dsh",
                    Name: name,
                    Project: string.IsNullOrEmpty(cwd) ? "" : Path.GetFileName(cwd.TrimEnd('\\', '/')),
                    Status: status,
                    WaitingFor: waitingFor,
                    Since: since,
                    StartedAt: lastPrompt ?? since,
                    Pid: null,
                    Model: model,
                    TotalTokens: totalTokens,
                    Title: title)));
            }

            return list.OrderByDescending(x => x.Updated).Take(Math.Max(1, DshMaxSessions))
                .Select(x => x.Session).ToList();
        }
    }

    /// <summary>
    /// Finds the harness's loopback port: a cached one while it still answers, else the listening ports of the
    /// "DeepSeek Harness" processes, tried until one accepts the signed cookie.
    /// </summary>
    private async Task<int> DshPortAsync(byte[] secret, CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (_dshPort != 0 && now - _dshPortCheckedAt < 120_000) return _dshPort;

        var candidates = new List<int>();
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!process.ProcessName.Contains("DeepSeek", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var listener in ListeningPorts.ForProcess(process.Id, 8))
                        candidates.Add(listener.Port);
                }
                catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
                {
                    // the process ended while we looked at it
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return 0;
        }

        foreach (var port in candidates.Distinct())
        {
            ct.ThrowIfCancellationRequested();
            var authority = "127.0.0.1:" + port;
            using var probe = new HttpRequestMessage(HttpMethod.Post, $"http://{authority}/api/session/list")
            {
                Content = new StringContent(
                    """{"type":"client-request","rpcId":"1","method":"session/list","payload":{"args":{"_request":{}}}}""",
                    Encoding.UTF8, "application/json")
            };
            probe.Headers.TryAddWithoutValidation("Cookie", DshCookie(secret, authority));
            try
            {
                using var response = await DshHttp.SendAsync(probe, ct);
                if (response.IsSuccessStatusCode)
                {
                    _dshPort = port;
                    _dshPortCheckedAt = now;
                    return port;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
            {
                // not this one
            }
        }

        _dshPort = 0;
        _dshPortCheckedAt = now;
        return 0;
    }

    private void InvalidateDshPort()
    {
        _dshPort = 0;
        _dshPortCheckedAt = 0;
    }

    /// <summary>Builds the signed cookie the harness accepts, for one authority.</summary>
    internal static string DshCookie(byte[] secret, string authority)
    {
        var name = "dsh-auth-" + Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(authority)));
        var issued = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var json = $$"""{"version":1,"authority":"{{authority}}","issuedAt":{{issued}},"expiresAt":{{issued + 3_600_000}}}""";
        var body = Base64Url(Encoding.UTF8.GetBytes(json));
        var signature = Base64Url(HMACSHA256.HashData(secret, Encoding.ASCII.GetBytes(body)));
        return $"{name}=v1.{body}.{signature}";
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Reads the browser-session secret from the harness credentials file (32 raw bytes). The file is a map keyed by
    /// connection name — "client-connection/browser-session:" — whose payload holds the secret, not a "kind:" value.
    /// </summary>
    private byte[]? DshSecret()
    {
        try
        {
            var path = Path.Combine(Home, ".dsh", ".credentials.yaml");
            if (!File.Exists(path)) return null;
            var lines = File.ReadAllLines(path);
            var found = false;
            foreach (var line in lines)
            {
                var text = line.Trim().Trim('\'', '"');
                if (!found)
                {
                    if (text.StartsWith("client-connection/browser-session", StringComparison.Ordinal)) found = true;
                    continue;
                }
                if (!text.StartsWith("secret:", StringComparison.Ordinal)) continue;
                var raw = text["secret:".Length..].Trim().Trim('\'', '"');
                var padded = raw.Replace('-', '+').Replace('_', '/');
                padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
                var bytes = Convert.FromBase64String(padded);
                return bytes.Length == 32 ? bytes : null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            log.LogDebug("dsh credentials: {Message}", ex.Message);
        }
        return null;
    }

    /// <summary>Show a session that has no title yet by a short id, without DSH's "session-" prefix.</summary>
    private static string ShortDshId(string id)
    {
        var body = id.StartsWith("session-", StringComparison.OrdinalIgnoreCase) ? id["session-".Length..] : id;
        return body.Length > 8 ? body[..8] : body;
    }

    private static bool? DshBool(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;
}
