using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ZstdSharp;

namespace PhoneBox.Agent;

/// <summary>
/// DeepSeek Harness (DSH) sessions, read from disk without touching the harness:
///   %USERPROFILE%\.dsh\sessions\&lt;workspace&gt;\&lt;session-id&gt;\session.v&lt;n&gt;.jsonl.zstd
/// Each file is a sequence of zstd frames that decode to JSON Lines. The first record is the session header
/// (id, cwd, createdAt, origin), and later records carry the title, the model and the event stream, so the last
/// record says what the session is doing right now.
/// A session's file length changing is exactly "new activity"; the file write time is "last activity". Both are
/// cached per file, so only the sessions that actually moved are decoded again.
/// </summary>
public sealed partial class SessionMonitor
{
    private readonly record struct DshParsed(string Id, string Cwd, long CreatedAt, bool Subagent, string? Parent,
        string? Title, string? Model, string? LastType, string? LastTool, long LastEventAt, long TurnStartedAt);

    private readonly ConcurrentDictionary<string, (long Length, long WriteTicks, DshParsed Parsed)> _dshCache = new();

    /// <summary>Reads every recent DSH session, newest activity first.</summary>
    private IEnumerable<AgentSession> ReadDsh()
    {
        var root = Path.Combine(Home, ".dsh", "sessions");
        if (!Directory.Exists(root)) yield break;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowMs = Math.Max(1, DshWindowHours) * 3_600_000L;
        var found = new List<(long Write, AgentSession Session)>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> workspaces;
        try { workspaces = Directory.EnumerateDirectories(root).ToArray(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (var ws in workspaces)
        {
            string[] dirs;
            try { dirs = Directory.GetDirectories(ws); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var dir in dirs)
            {
                var file = NewestSessionFile(dir);
                if (file is null) continue;
                FileInfo info;
                try { info = new FileInfo(file); }
                catch (IOException) { continue; }
                if (!info.Exists) continue;

                var write = info.LastWriteTimeUtc.Ticks;
                if (now - new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds() > windowMs) continue;
                live.Add(file);

                DshParsed parsed;
                if (_dshCache.TryGetValue(file, out var cached) && cached.Length == info.Length && cached.WriteTicks == write)
                {
                    parsed = cached.Parsed;
                }
                else
                {
                    var fresh = ParseDshPartner(file);
                    if (fresh is null) continue;
                    parsed = fresh.Value;
                    _dshCache[file] = (info.Length, write, parsed);
                }

                if (parsed.Subagent && !DshIncludeSubagents) continue;

                var since = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeMilliseconds();
                var age = now - since;
                var (status, waitingFor) = DshStatus(parsed, age);
                var project = string.IsNullOrEmpty(parsed.Cwd) ? "" : Path.GetFileName(parsed.Cwd.TrimEnd('\\', '/'));
                // no title yet (a fresh session): show a short id, without DSH's "session-" prefix
                var shortId = parsed.Id.StartsWith("session-", StringComparison.OrdinalIgnoreCase)
                    ? parsed.Id["session-".Length..] : parsed.Id;
                var name = string.IsNullOrWhiteSpace(parsed.Title)
                    ? (shortId.Length > 8 ? shortId[..8] : shortId)
                    : parsed.Title!;

                found.Add((since, new AgentSession(
                    Id: "dsh:" + parsed.Id,
                    Tool: "dsh",
                    Name: name,
                    Project: project,
                    Status: status,
                    WaitingFor: waitingFor,
                    Since: since,
                    StartedAt: parsed.CreatedAt,
                    Pid: null,
                    Model: parsed.Model,
                    TurnStartedAt: parsed.TurnStartedAt > 0 ? parsed.TurnStartedAt : null,
                    LastTool: parsed.LastTool,
                    Title: parsed.Title)));
            }
        }

        // forget sessions that disappeared or aged out, so the cache cannot grow without bound
        foreach (var key in _dshCache.Keys)
            if (!live.Contains(key) && _dshCache.TryGetValue(key, out var v)
                && now - new DateTimeOffset(v.WriteTicks, TimeSpan.Zero).ToUnixTimeMilliseconds() > windowMs)
                _dshCache.TryRemove(key, out _);

        foreach (var entry in found.OrderByDescending(f => f.Write).Take(Math.Max(1, DshMaxSessions)))
            yield return entry.Session;
    }

    /// <summary>The newest session file in one session folder (DSH bumps the format version over time).</summary>
    private static string? NewestSessionFile(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "session.v*.jsonl.zstd")
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decodes one session file (concatenated zstd frames -> JSON Lines) and keeps the facts the panel needs.
    /// Records that do not parse are skipped: one bad line must not hide a whole session.
    /// </summary>
    private static DshParsed? ParseDshPartner(string file)
    {
        // DSH appends to this file while the session runs: share read AND write, and release the handle before
        // decoding, or the harness's own write fails with EBUSY.
        byte[] bytes;
        try
        {
            using var raw = new FileStream(file, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.SequentialScan);
            bytes = new byte[raw.Length];
            var read = 0;
            while (read < bytes.Length)
            {
                var n = raw.Read(bytes, read, bytes.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < bytes.Length) Array.Resize(ref bytes, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;   // a concurrent flush won this round; the next tick reads it
        }

        string text;
        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var zstd = new DecompressionStream(source);
            using var reader = new StreamReader(zstd, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is InvalidDataException or ZstdException or IOException)
        {
            return null;   // half-written frame: try again on the next tick
        }

        string id = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        var cwd = "";
        long createdAt = 0;
        var subagent = false;
        string? parent = null, title = null, model = null, lastType = null, lastTool = null;
        long lastEventAt = 0, turnStarted = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length < 2) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (type is null) continue;

                if (type == "session")
                {
                    if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                        id = idEl.GetString() ?? id;
                    if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
                        cwd = cwdEl.GetString() ?? "";
                    if (root.TryGetProperty("createdAt", out var ca) && ca.ValueKind == JsonValueKind.Number)
                        createdAt = ca.GetInt64();
                    if (root.TryGetProperty("origin", out var orig) && orig.ValueKind == JsonValueKind.String
                        && string.Equals(orig.GetString(), "subagent", StringComparison.Ordinal))
                        subagent = true;
                    if (root.TryGetProperty("parentSession", out var ps) && ps.ValueKind == JsonValueKind.String)
                        parent = ps.GetString();
                    continue;
                }

                if (root.TryGetProperty("time", out var tm) && tm.ValueKind == JsonValueKind.Number)
                    lastEventAt = Math.Max(lastEventAt, tm.GetInt64());

                switch (type)
                {
                    case "session/title":
                        if (root.TryGetProperty("data", out var td) && td.ValueKind == JsonValueKind.Object
                            && td.TryGetProperty("title", out var tt) && tt.ValueKind == JsonValueKind.String)
                            title = tt.GetString();
                        break;
                    case "request/context":
                        if (root.TryGetProperty("data", out var rc) && rc.ValueKind == JsonValueKind.Object
                            && rc.TryGetProperty("model", out var mm) && mm.ValueKind == JsonValueKind.String)
                            model = mm.GetString();
                        break;
                    case "turn/start":
                        if (root.TryGetProperty("time", out var ts) && ts.ValueKind == JsonValueKind.Number)
                            turnStarted = ts.GetInt64();
                        break;
                    case "tool/call":
                        if (root.TryGetProperty("data", out var tc) && tc.ValueKind == JsonValueKind.Object
                            && tc.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                            lastTool = nm.GetString();
                        break;
                }

                if (type is "step/start" or "step/end" or "tool/call" or "tool/result"
                    or "assistant/message" or "user/message" or "turn/start" or "turn/end")
                    lastType = type;
            }
            catch (JsonException)
            {
                // a half-written trailing record is normal while a turn is running
            }
        }

        return new DshParsed(id, cwd, createdAt, subagent, parent, title, model, lastType, lastTool, lastEventAt, turnStarted);
    }

    /// <summary>
    /// busy: the last record is a step/tool in flight and the file moved recently.
    /// waiting: a tool call is still the last record but nothing has been written for a while, which is what an
    /// unanswered approval or a stalled tool looks like from the outside.
    /// idle: the turn finished (step/end, turn/end) or the file is simply resting.
    /// </summary>
    private (string Status, string? WaitingFor) DshStatus(DshParsed parsed, long ageMs)
    {
        var waitingAfter = Math.Max(30, DshWaitingSec) * 1000L;
        var busyAfter = 120_000L;
        return parsed.LastType switch
        {
            "tool/call" => ageMs < waitingAfter ? ("busy", null) : ("waiting", parsed.LastTool ?? "tool"),
            "step/start" or "assistant/message" or "user/message" or "tool/result" or "turn/start"
                => ageMs < busyAfter ? ("busy", null) : ("idle", null),
            _ => ("idle", null)
        };
    }

    private int DshWindowHours => config?.Get().Dsh.WindowHours ?? 72;
    private int DshMaxSessions => config?.Get().Dsh.MaxSessions ?? 20;
    private int DshWaitingSec => config?.Get().Dsh.WaitingSec ?? 180;
    private bool DshIncludeSubagents => config?.Get().Dsh.IncludeSubagents == true;
}
