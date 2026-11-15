using System.Windows;
using System.Windows.Controls;
using Shoji.Core;
using Shoji.Sensors;

namespace Shoji.UI;

/// <summary>Hardware sensors → Tasks &amp; agents: connects Claude Code's hooks to the Tasks &amp; agents tile, and says how other tools report.</summary>
public sealed partial class SettingsWindow
{
    void AddAgents()
    {
        Section(L.T("Tasks & agents"), L.T("For the Tasks & agents tile: what is still working, what has finished, and what is waiting for you."));
        var card = Card();
        bool connected = ClaudeHooks.Connected;
        FrameworkElement right;
        if (!ClaudeHooks.Installed)
            right = Pill(L.T("Not installed"), PillKind.Neutral);
        else if (connected)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var pill = Pill(L.T("Connected"), PillKind.Good);
            pill.Margin = new Thickness(0, 0, 10, 6);
            row.Children.Add(pill);
            row.Children.Add(Button(L.T("Disconnect"), () => { ClaudeHooks.Disconnect(); Rebuild(); }));
            right = row;
        }
        else
            right = AccentButton(L.T("Connect"), () => { ClaudeHooks.Connect(); Rebuild(); });
        Setting(card, "Claude Code",
            connected ? L.T("The tile knows the moment a session needs a permission or has finished (hooks in Claude Code's settings.json call Shoji).")
            : L.T("Without this the tile only sees that a session finished. Connect adds hooks to Claude Code's settings.json (a copy of the file is kept); new sessions then report when they need a permission or an answer."),
            right, icon: "");
        if (CodexHooks.Installed)
        {
            bool codex = AgentHooks.Has(CodexHooks.SettingsPath, " hook codex");
            FrameworkElement codexRight;
            if (codex)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var pill = Pill(L.T("Connected"), PillKind.Good);
                pill.Margin = new Thickness(0, 0, 10, 0);
                row.Children.Add(pill);
                row.Children.Add(Button(L.T("Disconnect"), () => { CodexHooks.Disconnect(); Rebuild(); }));
                codexRight = row;
            }
            else
                codexRight = AccentButton(L.T("Connect"), () => { CodexHooks.Connect(); Rebuild(); });
            Setting(card, "Codex",
                codex ? L.T("The tile knows the moment a session asks for approval or has finished (hooks in Codex's hooks.json call Shoji). Codex asks once whether these hooks may run: type /hooks in Codex and allow them.")
                : L.T("Without this the tile only sees that a session is working or finished. Connect adds hooks to Codex's hooks.json (a copy of the file is kept); afterwards type /hooks in Codex once and allow them."),
                codexRight, icon: "\uE99A");
        }
        Setting(card, L.T("Card when something needs you"), L.T("A card on the dashboard, on whichever page is showing, while an agent waits for a permission or an answer, and for a minute after something finished or failed. Tap it to put it away."),
            Switch(_config.TaskCard, v => Change(c => c.TaskCard = v)), icon: "\uE7C9");
        Setting(card, L.T("Other tools"), L.T("Scripts can report a task with \"Shoji.exe task running --title Backup\" and later \"Shoji.exe task done --title Backup\" (or failed, waiting, remove)."),
            Button(L.T("Copy example"), () => Clipboard.SetText("Shoji.exe task done --title Backup")), last: true, icon: "");
    }
}
