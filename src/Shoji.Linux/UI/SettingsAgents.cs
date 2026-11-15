using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Shoji.Core;
using Shoji.Sensors;

namespace Shoji.UI;

/// <summary>Hardware sensors → Coding agents: connects Claude Code's hooks to the Tasks &amp; agents tile, and says how commands get there.</summary>
public sealed partial class SettingsWindow
{
    void AddAgents()
    {
        Section("Tasks & agents", "For the Tasks & agents tile: what is still working, what has finished, and what is waiting for you.");
        var card = Card();
        bool connected = ClaudeHooks.Connected;
        Control right;
        if (!ClaudeHooks.Installed)
            right = Pill("Not installed", PillKind.Neutral);
        else if (connected)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pill = Pill("Connected", PillKind.Good);
            pill.Margin = new Thickness(0, 0, 10, 6);
            row.Children.Add(pill);
            row.Children.Add(Button("Disconnect", () => { ClaudeHooks.Disconnect(); Rebuild(); }));
            right = row;
        }
        else
            right = AccentButton("Connect", () => { ClaudeHooks.Connect(); Rebuild(); });
        Setting(card, "Claude Code",
            connected ? "The tile knows the moment a session needs a permission or has finished (hooks in Claude Code's settings.json call Shoji)."
            : "Without this the tile only sees that a session finished. Connect adds hooks to Claude Code's settings.json (a copy of the file is kept); new sessions then report when they need a permission or an answer.",
            right, icon: "smart_toy");
        if (CodexHooks.Installed)
        {
            bool codex = AgentHooks.Has(CodexHooks.SettingsPath, " hook codex");
            Control codexRight;
            if (codex)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var pill = Pill("Connected", PillKind.Good);
                pill.Margin = new Thickness(0, 0, 10, 6);
                row.Children.Add(pill);
                row.Children.Add(Button("Disconnect", () => { CodexHooks.Disconnect(); Rebuild(); }));
                codexRight = row;
            }
            else
                codexRight = AccentButton("Connect", () => { CodexHooks.Connect(); Rebuild(); });
            Setting(card, "Codex",
                codex ? "The tile knows the moment a session asks for approval or has finished (hooks in Codex's hooks.json call Shoji). Codex asks once whether these hooks may run: type /hooks in Codex and allow them."
                : "Without this the tile only sees that a session is working or finished. Connect adds hooks to Codex's hooks.json (a copy of the file is kept); afterwards type /hooks in Codex once and allow them.",
                codexRight, icon: "smart_toy");
        }
        Setting(card, "Card when something needs you", "A card on the dashboard, on whichever page is showing, while an agent waits for a permission or an answer, and for a minute after something finished or failed. Tap it to put it away.",
            Switch(_config.TaskCard, v => Change(c => c.TaskCard = v)), icon: "pan_tool");
        Setting(card, "Any command", "Put \"shoji run\" in front of a long command and the tile shows it until it ends, then says whether it worked. Scripts can report by hand with \"shoji task done --title Backup\".",
            Button("Copy example", () => Clipboard?.SetTextAsync("shoji run npm test")), last: true, icon: "terminal",
            code: "shoji run npm test");
    }
}
