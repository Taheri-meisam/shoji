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
        string name = agent == "claude" ? "Claude Code" : char.ToUpperInvariant(agent[0]) + agent[1..];
        string folder = ShortFolder(Text("cwd"));
        var item = TaskFiles.Read(id) ?? new TaskItem { Id = TaskFiles.SafeId(id) };
        item.Kind = "agent";
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
            }
            // Claude Code's logs every few seconds: sessions whose hooks aren't set up still show "finished, your turn".
            if (_tick++ % 5 == 0)
                _logTasks = ClaudeLogs.Read(now, LogWindow);
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
                    if (Parse(file, written, nowUtc) is { } item)
                    {
                        lock (Dismissed)
                        {
                            // Dismissed stays dismissed until the session moves on.
                            if (Dismissed.Contains(item.Id) && item.State == "done")
                                continue;
                            Dismissed.Remove(item.Id);
                        }
                        list.Add(item);
                    }
                }
        }
        catch (Exception ex)
        {
            Log.WriteThrottled("claudelogs", $"Claude Code's logs could not be read: {ex.Message}", TimeSpan.FromHours(6));
        }
        return list;
    }

    /// <summary>A session only counts while a Claude Code is open; a closed one isn't waiting for anybody.</summary>
    static bool _running;
    static long _runningAt = -100_000;

    static bool ClaudeRunning()
    {
        if (OperatingSystem.IsWindows())
            return true; // it runs inside node / a terminal there: can't tell cheaply, so trust the log's age
        // Looking through every process isn't free either: the answer holds for a few seconds.
        if (Environment.TickCount64 - _runningAt < 5000)
            return _running;
        try
        {
            var found = Process.GetProcessesByName("claude");
            foreach (var p in found)
                p.Dispose();
            _running = found.Length > 0;
        }
        catch
        {
            _running = true;
        }
        _runningAt = Environment.TickCount64;
        return _running;
    }

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

    static string Tail(string file, int bytes)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > bytes)
            stream.Seek(-bytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

/// <summary>
/// Settings' one-click set-up for Claude Code: adds (or removes) hooks in ~/.claude/settings.json that call
/// "shoji hook claude", so the Tasks tile knows the moment a session needs a permission or has finished.
/// Other hooks and settings in that file are left as they are; a copy is kept next to it the first time.
/// </summary>
public static class ClaudeHooks
{
    static readonly string[] Events = { "UserPromptSubmit", "Notification", "Stop", "SessionEnd" };
    const string Marker = " hook claude";

    public static string SettingsPath => Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "settings.json");

    /// <summary>Claude Code has been used on this PC (its folder exists).</summary>
    public static bool Installed => Directory.Exists(Path.GetDirectoryName(SettingsPath)!);

    public static bool Connected
    {
        get
        {
            try { return File.Exists(SettingsPath) && File.ReadAllText(SettingsPath).Contains(Marker, StringComparison.Ordinal); }
            catch { return false; }
        }
    }

    static string Command
    {
        get
        {
            string exe = Environment.ProcessPath ?? "shoji";
            return (exe.Contains(' ') ? "\"" + exe + "\"" : exe) + Marker;
        }
    }

    /// <summary>Adds the hooks (once). Returns null when it worked, else why not.</summary>
    public static string? Connect() => Edit(add: true);

    /// <summary>Takes Shoji's hooks out again, leaving everything else.</summary>
    public static string? Disconnect() => Edit(add: false);

    static string? Edit(bool add)
    {
        try
        {
            string path = SettingsPath;
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
            foreach (string name in Events)
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
                            if (list[h]?["command"]?.GetValue<string>() is { } command && command.EndsWith(Marker, StringComparison.Ordinal))
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
                var hook = new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = Command };
                // Claude Code gives a session-end hook 1.5 s by default; a cold start can take longer.
                hook["timeout"] = 10;
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
            Log.Write($"Claude Code hooks could not be {(add ? "added" : "removed")}: {ex.Message}");
            return ex.Message;
        }
    }
}
