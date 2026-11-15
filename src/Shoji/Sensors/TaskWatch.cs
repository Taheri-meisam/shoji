using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Shoji.Core;

namespace Shoji.Sensors;

/// <summary>
/// Something long-running the dashboard keeps an eye on: a command started with "shoji run", or a coding agent's
/// session. <see cref="State"/>: running, waiting (it needs you: a permission, an answer), done, failed.
/// </summary>
public sealed class TaskItem
{
    public string Id { get; set; } = "";
    /// <summary>"command" or "agent".</summary>
    public string Kind { get; set; } = "command";
    public string Title { get; set; } = "";
    public string? Detail { get; set; }
    public string State { get; set; } = "running";
    public DateTime Started { get; set; } = DateTime.UtcNow;
    /// <summary>When the state last changed (UTC).</summary>
    public DateTime Changed { get; set; } = DateTime.UtcNow;
    public int? ExitCode { get; set; }
    /// <summary>The wrapper process of a "shoji run" command, so a killed terminal doesn't leave it "running" forever.</summary>
    public int? Pid { get; set; }
    /// <summary>An agent's session log. When it is written to after the agent started waiting, the question was answered.</summary>
    public string? Transcript { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsYou => State is "waiting" or "done" or "failed";
}

/// <summary>One small JSON file per task in the data folder's "tasks" folder: any tool can write one, the dashboard shows them.</summary>
public static class TaskFiles
{
    public static string Folder => Path.Combine(AppPaths.DataFolder, "tasks");

    static string FileOf(string id) => Path.Combine(Folder, SafeId(id) + ".json");

    public static string SafeId(string id)
    {
        var chars = id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(80).ToArray();
        return chars.Length == 0 ? "task" : new string(chars);
    }

    public static void Write(TaskItem item)
    {
        Directory.CreateDirectory(Folder);
        string path = FileOf(item.Id), temp = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(item));
        File.Move(temp, path, overwrite: true);
    }

    public static TaskItem? Read(string id)
    {
        try
        {
            string path = FileOf(id);
            return File.Exists(path) ? JsonSerializer.Deserialize<TaskItem>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Remove(string id)
    {
        try { File.Delete(FileOf(id)); } catch { /* gone already */ }
    }

    public static List<TaskItem> ReadAll()
    {
        var list = new List<TaskItem>();
        if (!Directory.Exists(Folder))
            return list;
        foreach (var file in Directory.EnumerateFiles(Folder, "*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<TaskItem>(File.ReadAllText(file)) is { Id.Length: > 0 } item)
                    list.Add(item);
            }
            catch
            {
                // half-written or not ours: the next look reads it
            }
        }
        return list;
    }
}

/// <summary>
/// The command-line side: "shoji run npm test" wraps a command, "shoji task done --title Backup" reports anything
/// by hand, and "shoji hook claude" is what Claude Code's hooks call (Settings → Hardware explains the set-up).
/// These only write a task file and never start the dashboard, so they are quick and safe to call from scripts.
/// </summary>
public static class TaskCli
{
    /// <summary>True when the arguments were one of these commands (then <paramref name="exitCode"/> is the result).</summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
            return false;
        try
        {
            switch (args[0])
            {
                case "run": exitCode = Run(args.Skip(1).ToArray()); return true;
                case "task": exitCode = Task(args.Skip(1).ToArray()); return true;
                case "hook": Hook(args.Skip(1).FirstOrDefault() ?? "claude"); return true;
                default: return false;
            }
        }
        catch (Exception ex)
        {
            // A hook must never break the agent that called it; "run" has already given its own exit code or failed to start.
            if (args[0] != "hook")
                Console.Error.WriteLine("shoji: " + ex.Message);
            exitCode = args[0] == "hook" ? 0 : 1;
            return true;
        }
    }

    static int Run(string[] command)
    {
        if (command.Length == 0)
        {
            Console.Error.WriteLine("Usage: shoji run <command> [arguments…]   shows the command on the dashboard until it ends");
            return 2;
        }
        var item = new TaskItem
        {
            Id = "run-" + Environment.ProcessId + "-" + DateTime.UtcNow.Ticks % 100000,
            Kind = "command",
            Title = string.Join(' ', command),
            Detail = ShortFolder(Environment.CurrentDirectory),
            Pid = Environment.ProcessId,
        };
        TaskFiles.Write(item);
        int code;
        try
        {
            var start = new ProcessStartInfo(command[0]) { UseShellExecute = false };
            foreach (var a in command.Skip(1))
                start.ArgumentList.Add(a);
            using var p = Process.Start(start) ?? throw new InvalidOperationException("could not start " + command[0]);
            // Ctrl+C goes to the command too (same terminal); wait for it and report how it ended.
            Console.CancelKeyPress += (_, e) => e.Cancel = true;
            p.WaitForExit();
            code = p.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("shoji run: " + ex.Message);
            code = 127;
        }
        item.State = code == 0 ? "done" : "failed";
        item.ExitCode = code;
        item.Changed = DateTime.UtcNow;
        item.Pid = null;
        TaskFiles.Write(item);
        return code;
    }

    static int Task(string[] args)
    {
        string? Opt(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        string state = args.FirstOrDefault() ?? "";
        string? title = Opt("--title");
        string id = Opt("--id") ?? title ?? "";
        if (state is not ("running" or "waiting" or "done" or "failed" or "remove") || id.Length == 0)
        {
            Console.Error.WriteLine("Usage: shoji task running|waiting|done|failed|remove --title \"Backup\" [--id backup] [--detail \"text\"]");
            return 2;
        }
        if (state == "remove")
        {
            TaskFiles.Remove(id);
            return 0;
        }
        var item = TaskFiles.Read(id) ?? new TaskItem { Id = TaskFiles.SafeId(id), Kind = "command" };
        if (item.State != state || state == "running" && item.NeedsYou)
            item.Changed = DateTime.UtcNow;
        if (state == "running" && item.NeedsYou)
            item.Started = DateTime.UtcNow;
        item.State = state;
        item.Title = title ?? item.Title;
        item.Detail = Opt("--detail") ?? item.Detail;
        TaskFiles.Write(item);
        return 0;
    }

    /// <summary>Claude Code's hooks hand their event as JSON on stdin. Prints nothing and always succeeds.</summary>
    static void Hook(string agent)
    {
        using var doc = JsonDocument.Parse(Console.In.ReadToEnd());
        var root = doc.RootElement;
        string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? session = Text("session_id");
        if (string.IsNullOrEmpty(session))
            return;
        string id = agent + "-" + session;
        string name = agent == "claude" ? "Claude Code" : agent.Length == 0 ? "Agent" : char.ToUpperInvariant(agent[0]) + agent[1..];
        string folder = ShortFolder(Text("cwd"));
        var item = TaskFiles.Read(id) ?? new TaskItem { Id = TaskFiles.SafeId(id) };
        item.Kind = "agent";
        item.Transcript = Text("transcript_path") ?? item.Transcript;
        item.Title = Text("session_title") is { Length: > 0 } title ? $"{name} · {title}" : folder.Length > 0 ? $"{name} · {folder}" : name;

        string state;
        string? detail;
        switch (Text("hook_event_name"))
        {
            case "UserPromptSubmit":
                state = "running";
                detail = OneLine(Text("prompt"));
                item.Started = DateTime.UtcNow;
                break;
            case "Stop":
                // Still has background work that will wake it: not your turn yet.
                bool background = root.TryGetProperty("background_tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array && tasks.GetArrayLength() > 0;
                state = background ? "running" : "done";
                detail = OneLine(Text("last_assistant_message")) ?? item.Detail;
                break;
            case "Notification":
                // A permission to give or a question to answer; "idle" only repeats that it finished a minute ago.
                switch (Text("notification_type"))
                {
                    case "permission_prompt" or "elicitation_dialog" or "agent_needs_input": state = "waiting"; break;
                    case "idle_prompt": state = "done"; break;
                    default: return;
                }
                detail = state == "waiting" ? OneLine(Text("message")) ?? item.Detail : item.Detail;
                break;
            case "PermissionRequest":
                // Codex asks before a command it may not run by itself; what it wants is in "tool_input".
                state = "waiting";
                string? what = null;
                if (root.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
                    foreach (string field in new[] { "description", "command" })
                        if (what == null && input.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String)
                            what = OneLine(v.GetString());
                detail = what ?? (Text("tool_name") is { Length: > 0 } tool ? $"{name} → {tool}" : item.Detail);
                break;
            case "SessionEnd":
                TaskFiles.Remove(id);
                return;
            default:
                return;
        }
        if (item.State != state)
            item.Changed = DateTime.UtcNow;
        item.State = state;
        item.Detail = detail;
        TaskFiles.Write(item);
    }

    static string? OneLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        text = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= 160 ? text : text[..159] + "…";
    }

    internal static string ShortFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "";
        string name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return name.Length > 0 ? name : path;
    }
}

/// <summary>
/// Watches the tasks folder (and, as a fallback without hooks, Claude Code's own session logs) for the Tasks tile.
/// Finished commands stay for a while and then tidy themselves away.
/// </summary>
public sealed class TaskWatch : IDisposable
{
    public const string Key = "taskwatch";

    static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(20), KeepAgent = TimeSpan.FromHours(3), LogWindow = TimeSpan.FromMinutes(20);

    readonly Timer _timer;
    readonly Dictionary<string, string> _states = new();
    IReadOnlyList<TaskItem> _logTasks = Array.Empty<TaskItem>();
    int _tick, _busy;
    bool _first = true;

    /// <summary>What to show, the ones that need you first, then the newest.</summary>
    public IReadOnlyList<TaskItem> State { get; private set; } = Array.Empty<TaskItem>();

    /// <summary>A task just started needing you (waiting, done or failed). Raised on a timer thread.</summary>
    public event Action<TaskItem>? NeedsYou;

    public TaskWatch()
    {
        _timer = new Timer(_ => Poll(), null, 0, 1000);
    }

    /// <summary>For demo data: show these and watch nothing.</summary>
    public TaskWatch(IReadOnlyList<TaskItem> fixedItems)
    {
        _timer = new Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        State = fixedItems;
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return;
        try
        {
            var now = DateTime.UtcNow;
            var items = TaskFiles.ReadAll();
            foreach (var item in items.ToList())
            {
                bool stale = item.Kind == "agent" ? now - item.Changed > KeepAgent
                    : item.State is "done" or "failed" ? now - item.Changed > KeepFinished
                    : item.Pid is int pid && !Alive(pid); // the terminal was closed mid-run
                if (stale)
                {
                    TaskFiles.Remove(item.Id);
                    items.Remove(item);
                }
                else if (item.State == "waiting" && item.Kind == "agent" && MovedOn(item))
                {
                    // Answered in the terminal: no hook says so, but the session's log is being written again.
                    item.State = "running";
                    item.Detail = null;
                    item.Changed = now;
                    TaskFiles.Write(item);
                }
            }
            // Claude Code's logs every few seconds: sessions whose hooks aren't set up still show "finished, your turn".
            if (_tick++ % 5 == 0)
                _logTasks = ClaudeLogs.Read(now, LogWindow).Concat(CodexLogs.Read(now, LogWindow)).ToList();
            foreach (var fromLog in _logTasks)
                if (!items.Any(i => i.Id == fromLog.Id))
                    items.Add(fromLog);

            static int Rank(TaskItem i) => i.State switch { "waiting" => 0, "failed" => 1, "running" => 2, _ => 3 };
            State = items.OrderBy(Rank).ThenByDescending(i => i.Changed).ToArray();

            foreach (var item in items)
            {
                bool known = _states.TryGetValue(item.Id, out var before);
                _states[item.Id] = item.State;
                // Not for what was already there when the dashboard started, and not for a state seen before.
                if (!_first && item.NeedsYou && (!known || before != item.State))
                    NeedsYou?.Invoke(item);
            }
            foreach (var gone in _states.Keys.Where(k => !items.Any(i => i.Id == k)).ToList())
                _states.Remove(gone);
            _first = false;
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("taskwatch", $"Tasks could not be read: {ex.Message}", TimeSpan.FromHours(1));
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Takes a finished task off the list (a tap on its row).</summary>
    public void Dismiss(TaskItem item)
    {
        TaskFiles.Remove(item.Id);
        ClaudeLogs.Dismiss(item.Id);
        State = State.Where(i => i.Id != item.Id).ToArray();
    }

    static bool MovedOn(TaskItem item)
    {
        try
        {
            return item.Transcript is { Length: > 0 } log && File.Exists(log) && File.GetLastWriteTimeUtc(log) > item.Changed.AddSeconds(3);
        }
        catch
        {
            return false;
        }
    }

    static bool Alive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _timer.Dispose();
}

/// <summary>
/// The card that comes up on the dashboard, whichever page is showing, when a task needs you: it stays while an
/// agent waits for a permission or an answer, and for a minute after something finished or failed.
/// </summary>
public static class TaskCard
{
    /// <summary>The dashboard shows the card (Settings → Hardware → Tasks &amp; agents); the Tasks tile then leaves its toast out.</summary>
    public static bool Enabled { get; set; }

    static readonly TimeSpan ShowFinished = TimeSpan.FromSeconds(60);

    public static string Key(TaskItem item) => $"{item.Id}|{item.State}|{item.Changed.Ticks}";

    /// <summary>The one to show now: the first that waits for you, else what finished a moment ago. Null when nothing does.</summary>
    public static TaskItem? Pick(IReadOnlyList<TaskItem> items, DateTime nowUtc, ICollection<string> dismissed)
    {
        TaskItem? finished = null;
        foreach (var item in items)
        {
            if (dismissed.Contains(Key(item)))
                continue;
            if (item.State == "waiting")
                return item;
            if (finished == null && item.State is "done" or "failed" && nowUtc - item.Changed < ShowFinished && nowUtc >= item.Changed.AddSeconds(-5))
                finished = item;
        }
        return finished;
    }

    public static string Title(TaskItem item) => item.State switch
    {
        "waiting" => L.F("{0} is waiting for you", item.Title),
        "failed" => L.F("{0} failed", item.Title),
        _ => L.F("{0} finished", item.Title),
    };

    /// <summary>"Claude needs your permission to use Bash · 0:42" while waiting; what it last said once finished.</summary>
    public static string Detail(TaskItem item, DateTime nowUtc)
    {
        if (item.State != "waiting")
            return item.State == "failed" && item.ExitCode is int code ? L.F("exit {0}", code)
                : item.Kind == "agent" ? string.IsNullOrWhiteSpace(item.Detail) ? L.T("Your turn") : item.Detail! : item.Detail ?? "";
        var waited = nowUtc - item.Changed;
        string clock = waited.TotalSeconds < 0 ? "0:00" : waited.TotalHours >= 1 ? $"{(int)waited.TotalHours}:{waited.Minutes:00}:{waited.Seconds:00}" : $"{waited.Minutes}:{waited.Seconds:00}";
        return string.IsNullOrWhiteSpace(item.Detail) ? clock : $"{item.Detail} · {clock}";
    }
}

/// <summary>
/// Reads the end of Claude Code's session logs (~/.claude/projects/…/&lt;session&gt;.jsonl) to tell a session that
/// finished its turn from one that is still working. It cannot see a pending permission question: that needs the hooks.
/// </summary>
static class ClaudeLogs
{
    static readonly HashSet<string> Dismissed = new();

    public static void Dismiss(string id)
    {
        lock (Dismissed)
            Dismissed.Add(id);
    }

    public static IReadOnlyList<TaskItem> Read(DateTime nowUtc, TimeSpan window)
    {
        var list = new List<TaskItem>();
        try
        {
            string root = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
                ? dir
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            string projects = Path.Combine(root, "projects");
            if (!Directory.Exists(projects) || !ClaudeRunning())
                return list;
            foreach (var project in Directory.EnumerateDirectories(projects))
                foreach (var file in Directory.EnumerateFiles(project, "*.jsonl"))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (nowUtc - written > window)
                        continue;
                    if (Parse(file, written, nowUtc) is { } item && Keep(item))
                        list.Add(item);
                }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("claudelogs", $"Claude Code's logs could not be read: {ex.Message}", TimeSpan.FromHours(6));
        }
        return list;
    }

    /// <summary>False for a finished session that was tapped away; it comes back when the session moves on.</summary>
    internal static bool Keep(TaskItem item)
    {
        lock (Dismissed)
        {
            if (Dismissed.Contains(item.Id) && item.State == "done")
                return false;
            Dismissed.Remove(item.Id);
        }
        return true;
    }

    static readonly Dictionary<string, (bool Running, long At)> Programs = new();

    /// <summary>A session only counts while its program is open; a closed one isn't waiting for anybody.</summary>
    internal static bool Running(string program)
    {
        if (OperatingSystem.IsWindows())
            return true; // it runs inside node / a terminal there: can't tell cheaply, so trust the log's age
        // Looking through every process isn't free either: the answer holds for a few seconds.
        lock (Programs)
        {
            if (Programs.TryGetValue(program, out var known) && Environment.TickCount64 - known.At < 5000)
                return known.Running;
        }
        bool running;
        try
        {
            var found = Process.GetProcessesByName(program);
            foreach (var p in found)
                p.Dispose();
            running = found.Length > 0;
        }
        catch
        {
            running = true;
        }
        lock (Programs)
            Programs[program] = (running, Environment.TickCount64);
        return running;
    }

    static bool ClaudeRunning() => Running("claude");

    sealed record Parsed(long Length, DateTime Written, string? Title, string? Cwd, string? State, DateTime? When);

    static readonly Dictionary<string, Parsed> Seen = new();

    /// <summary>
    /// What the end of a session log says. Reading it means a few hundred kilobytes of text, and this runs every
    /// second, so the answer is kept until the file changes.
    /// </summary>
    static Parsed ParseLog(string file, DateTime written)
    {
        long length = new FileInfo(file).Length;
        lock (Seen)
        {
            if (Seen.TryGetValue(file, out var known) && known.Length == length && known.Written == written)
                return known;
        }
        string tail = Tail(file, 400_000);
        string? title = null, cwd = null, state = null;
        DateTime? when = null;
        var lines = tail.Split('\n');
        // Newest first; the first line may be cut in half and is skipped by the parse failing.
        for (int i = lines.Length - 1; i >= 0 && (state == null || title == null || cwd == null); i--)
        {
            string line = lines[i];
            if (line.Length < 20)
                continue;
            // Cheap look before parsing: only titles and real messages matter.
            bool isTitle = title == null && line.Contains("\"ai-title\"", StringComparison.Ordinal);
            bool isMessage = line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal) || line.Contains("\"type\":\"user\"", StringComparison.Ordinal);
            if (!isTitle && !isMessage)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "ai-title")
                {
                    title ??= e.TryGetProperty("aiTitle", out var a) ? a.GetString() : null;
                    continue;
                }
                if (type is not ("assistant" or "user") || e.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True)
                    continue;
                cwd ??= e.TryGetProperty("cwd", out var c) ? c.GetString() : null;
                if (state != null)
                    continue;
                bool finished = type == "assistant" && e.TryGetProperty("message", out var m)
                    && m.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "end_turn";
                state = finished ? "done" : "running";
                if (e.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var stamp))
                    when = stamp.ToUniversalTime();
            }
            catch
            {
                // a cut or foreign line
            }
        }
        var parsed = new Parsed(length, written, title, cwd, state, when);
        lock (Seen)
        {
            if (Seen.Count > 200)
                Seen.Clear();
            Seen[file] = parsed;
        }
        return parsed;
    }

    static TaskItem? Parse(string file, DateTime written, DateTime nowUtc)
    {
        var (_, _, title, cwd, state, when) = ParseLog(file, written);
        if (state == null)
            return null;
        // Still "running" but silent for minutes: a long tool, or a question the log doesn't show. Too vague to list.
        if (state == "running" && nowUtc - written > TimeSpan.FromMinutes(3))
            return null;
        // Finished long ago (the file is still touched now and then): old news, not something waiting on the screen.
        if (state == "done" && nowUtc - (when ?? written) > TimeSpan.FromMinutes(30))
            return null;
        string folder = TaskCli.ShortFolder(cwd);
        return new TaskItem
        {
            Id = "claude-" + Path.GetFileNameWithoutExtension(file),
            Kind = "agent",
            Title = !string.IsNullOrWhiteSpace(title) ? $"Claude Code · {title}" : folder.Length > 0 ? $"Claude Code · {folder}" : "Claude Code",
            Detail = folder.Length > 0 && !string.IsNullOrWhiteSpace(title) ? folder : null,
            State = state,
            Started = when ?? written,
            Changed = when ?? written,
        };
    }

    internal static string Tail(string file, int bytes)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > bytes)
            stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// The same for Codex without its hooks: the end of its session logs (~/.codex/sessions/…/rollout-*.jsonl) says
/// whether a turn is still going ("task_started") or has ended ("task_complete", "turn_aborted"). A pending approval
/// isn't written there: that needs the hooks, and with the hooks connected the logs aren't read at all.
/// </summary>
static class CodexLogs
{
    sealed record Parsed(long Length, DateTime Written, string? Cwd, string? State, DateTime? When, string? Said);

    static readonly Dictionary<string, Parsed> Seen = new();

    public static IReadOnlyList<TaskItem> Read(DateTime nowUtc, TimeSpan window)
    {
        var list = new List<TaskItem>();
        try
        {
            string sessions = Path.Combine(CodexHooks.Folder, "sessions");
            if (!Directory.Exists(sessions) || CodexHooks.Connected || !ClaudeLogs.Running("codex"))
                return list;
            // Sessions are filed by the day they started (local time): today's and yesterday's folders are enough.
            foreach (var day in new[] { DateTime.Now, DateTime.Now.AddDays(-1) })
            {
                string folder = Path.Combine(sessions, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
                if (!Directory.Exists(folder))
                    continue;
                foreach (var file in Directory.EnumerateFiles(folder, "rollout-*.jsonl"))
                {
                    var written = File.GetLastWriteTimeUtc(file);
                    if (nowUtc - written <= window && Parse(file, written, nowUtc) is { } item && ClaudeLogs.Keep(item))
                        list.Add(item);
                }
            }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("codexlogs", $"Codex's logs could not be read: {ex.Message}", TimeSpan.FromHours(6));
        }
        return list;
    }

    static Parsed ParseLog(string file, DateTime written)
    {
        long length = new FileInfo(file).Length;
        lock (Seen)
        {
            if (Seen.TryGetValue(file, out var known) && known.Length == length && known.Written == written)
                return known;
        }
        string? cwd = null, state = null, said = null;
        DateTime? when = null;
        var lines = ClaudeLogs.Tail(file, 400_000).Split('\n');
        for (int i = lines.Length - 1; i >= 0 && (state == null || cwd == null); i--)
        {
            string line = lines[i];
            bool turn = state == null && (line.Contains("\"task_started\"", StringComparison.Ordinal) || line.Contains("\"task_complete\"", StringComparison.Ordinal)
                || line.Contains("\"turn_started\"", StringComparison.Ordinal) || line.Contains("\"turn_complete\"", StringComparison.Ordinal) || line.Contains("\"turn_aborted\"", StringComparison.Ordinal));
            bool context = cwd == null && (line.Contains("\"turn_context\"", StringComparison.Ordinal) || line.Contains("\"session_meta\"", StringComparison.Ordinal));
            if (!turn && !context)
                continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var e = doc.RootElement;
                if (!e.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                    continue;
                string? type = e.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type is "turn_context" or "session_meta")
                {
                    cwd ??= payload.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                    continue;
                }
                if (type != "event_msg" || state != null || !payload.TryGetProperty("type", out var kind))
                    continue;
                switch (kind.GetString())
                {
                    case "task_started" or "turn_started": state = "running"; break;
                    case "task_complete" or "turn_complete":
                        state = "done";
                        said = payload.TryGetProperty("last_agent_message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                        break;
                    case "turn_aborted": state = "done"; break;
                    default: continue;
                }
                if (e.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var stamp))
                    when = stamp.ToUniversalTime();
            }
            catch
            {
                // a cut or foreign line
            }
        }
        var parsed = new Parsed(length, written, cwd, state, when, said);
        lock (Seen)
        {
            if (Seen.Count > 200)
                Seen.Clear();
            Seen[file] = parsed;
        }
        return parsed;
    }

    static TaskItem? Parse(string file, DateTime written, DateTime nowUtc)
    {
        var (_, _, cwd, state, when, said) = ParseLog(file, written);
        if (state == null || state == "done" && nowUtc - (when ?? written) > TimeSpan.FromMinutes(30))
            return null;
        // "Working" but silent for a long while: a closed terminal, or an approval the log doesn't show. Too vague to list.
        if (state == "running" && nowUtc - written > TimeSpan.FromMinutes(5))
            return null;
        string folder = TaskCli.ShortFolder(cwd);
        if (said != null)
        {
            said = string.Join(' ', said.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (said.Length > 160)
                said = said[..159] + "…";
        }
        // rollout-2026-11-15T10-22-31-<thread id>.jsonl
        string name = Path.GetFileNameWithoutExtension(file);
        return new TaskItem
        {
            Id = "codex-" + (name.Length > 36 ? name[^36..] : name),
            Kind = "agent",
            Title = folder.Length > 0 ? $"Codex · {folder}" : "Codex",
            Detail = state == "done" ? said : null,
            State = state,
            Started = when ?? written,
            Changed = when ?? written,
        };
    }
}

/// <summary>
/// Adds or removes Shoji's hooks in an agent's settings file. Claude Code (settings.json) and Codex (hooks.json)
/// share the shape: "hooks" → event → groups → { type: command, command }. Other hooks and settings in the file
/// are left as they are; a copy is kept next to it the first time.
/// </summary>
static class AgentHooks
{
    public static bool Has(string path, string marker)
    {
        try { return File.Exists(path) && File.ReadAllText(path).Contains(marker, StringComparison.Ordinal); }
        catch { return false; }
    }

    static string Command(string marker)
    {
        string exe = Environment.ProcessPath ?? "shoji";
        return (exe.Contains(' ') ? "\"" + exe + "\"" : exe) + marker;
    }

    /// <param name="timeout">Seconds a hook may take, per event (a cold start of Shoji can take a moment).</param>
    public static string? Edit(string path, string agent, string marker, (string Event, int Timeout)[] events, bool add)
    {
        try
        {
            System.Text.Json.Nodes.JsonObject root;
            if (File.Exists(path))
            {
                string text = File.ReadAllText(path);
                root = (string.IsNullOrWhiteSpace(text) ? null : System.Text.Json.Nodes.JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
                string backup = path + ".before-shoji";
                if (!File.Exists(backup))
                    File.Copy(path, backup);
            }
            else
            {
                if (!add)
                    return null;
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                root = new System.Text.Json.Nodes.JsonObject();
            }

            if (root["hooks"] is not System.Text.Json.Nodes.JsonObject hooks)
            {
                if (!add)
                    return null;
                root["hooks"] = hooks = new System.Text.Json.Nodes.JsonObject();
            }
            foreach (var (name, timeout) in events)
            {
                var groups = hooks[name] as System.Text.Json.Nodes.JsonArray;
                // Out with ours first (also so a moved Shoji gets its new path), then back in when adding.
                if (groups != null)
                {
                    for (int g = groups.Count - 1; g >= 0; g--)
                    {
                        if (groups[g] is not System.Text.Json.Nodes.JsonObject group || group["hooks"] is not System.Text.Json.Nodes.JsonArray list)
                            continue;
                        for (int h = list.Count - 1; h >= 0; h--)
                            if (list[h]?["command"]?.GetValue<string>() is { } command && command.EndsWith(marker, StringComparison.Ordinal))
                                list.RemoveAt(h);
                        if (list.Count == 0)
                            groups.RemoveAt(g);
                    }
                    if (groups.Count == 0 && !add)
                        hooks.Remove(name);
                }
                if (!add)
                    continue;
                if (groups == null)
                    hooks[name] = groups = new System.Text.Json.Nodes.JsonArray();
                var hook = new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = Command(marker), ["timeout"] = timeout };
                groups.Add(new System.Text.Json.Nodes.JsonObject { ["hooks"] = new System.Text.Json.Nodes.JsonArray(hook) });
            }
            if (!add && hooks.Count == 0)
                root.Remove("hooks");
            string temp = path + ".shoji.tmp";
            File.WriteAllText(temp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, path, overwrite: true);
            return null;
        }
        catch (Exception ex)
        {
            Log.Write($"{agent} hooks could not be {(add ? "added" : "removed")}: {ex.Message}");
            return ex.Message;
        }
    }
}

/// <summary>
/// Settings' one-click set-up for Claude Code: adds (or removes) hooks in ~/.claude/settings.json that call
/// "shoji hook claude", so the Tasks tile knows the moment a session needs a permission or has finished.
/// </summary>
public static class ClaudeHooks
{
    // Claude Code gives a session-end hook 1.5 s by default; a cold start can take longer.
    static readonly (string, int)[] Events = { ("UserPromptSubmit", 10), ("Notification", 10), ("Stop", 10), ("SessionEnd", 10) };
    const string Marker = " hook claude";

    public static string SettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    /// <summary>Claude Code has been used on this PC (its folder exists).</summary>
    public static bool Installed => Directory.Exists(Path.GetDirectoryName(SettingsPath)!);

    public static bool Connected => AgentHooks.Has(SettingsPath, Marker);

    /// <summary>Adds the hooks (once). Returns null when it worked, else why not.</summary>
    public static string? Connect() => AgentHooks.Edit(SettingsPath, "Claude Code", Marker, Events, add: true);

    /// <summary>Takes Shoji's hooks out again, leaving everything else.</summary>
    public static string? Disconnect() => AgentHooks.Edit(SettingsPath, "Claude Code", Marker, Events, add: false);
}

/// <summary>
/// The same for Codex: hooks in ~/.codex/hooks.json that call "shoji hook codex". Codex asks once, in its own
/// "/hooks" screen, whether hooks it hasn't seen before may run; until that is answered they are skipped.
/// </summary>
public static class CodexHooks
{
    // Codex ends a SessionEnd hook after three seconds at most.
    static readonly (string, int)[] Events = { ("UserPromptSubmit", 10), ("PermissionRequest", 10), ("Stop", 10), ("SessionEnd", 3) };
    const string Marker = " hook codex";

    public static string Folder => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public static string SettingsPath => Path.Combine(Folder, "hooks.json");

    public static bool Installed => Directory.Exists(Folder);

    static bool _connected;
    static long _connectedAt = -100_000;

    /// <summary>Asked every few seconds by the log reader, so the answer is kept for a moment.</summary>
    public static bool Connected
    {
        get
        {
            if (Environment.TickCount64 - _connectedAt > 5000)
            {
                _connected = AgentHooks.Has(SettingsPath, Marker);
                _connectedAt = Environment.TickCount64;
            }
            return _connected;
        }
    }

    public static string? Connect() => Changed(AgentHooks.Edit(SettingsPath, "Codex", Marker, Events, add: true));

    public static string? Disconnect() => Changed(AgentHooks.Edit(SettingsPath, "Codex", Marker, Events, add: false));

    static string? Changed(string? result)
    {
        _connectedAt = -100_000;
        return result;
    }
}
