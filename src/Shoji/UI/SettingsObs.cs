using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Control deck → OBS Studio: where OBS's WebSocket server is, and a button to try it.</summary>
public sealed partial class SettingsWindow
{
    void AddObs(AppConfig config)
    {
        var obs = config.Obs;
        Section("OBS Studio", L.T("Deck buttons with \"OBS Studio\" record, stream, switch scenes and mute inputs, and light up with OBS's state."));
        var card = Card();
        Setting(card, L.T("Host"), L.T("localhost when OBS runs on this PC"), TextField(obs.Host, 200, v => Change(c => c.Obs.Host = v ?? "localhost")), icon: "\uE774");
        Setting(card, L.T("Port"), null, TextField(obs.Port.ToString(), 90, v => Change(c => c.Obs.Port = int.TryParse(v, out int n) && n is > 0 and < 65536 ? n : 4455),
            check: v => (v == null || int.TryParse(v, out int n) && n is > 0 and < 65536, v ?? "4455"), invalidHint: L.T("A port number (OBS uses 4455): this wasn't saved")), icon: "\uE839");
        Setting(card, L.T("Password"), L.T("From OBS → Tools → WebSocket Server Settings → Show Connect Info. Empty if authentication is off."), ObsPassword(obs.Password), icon: "\uE72E");

        var status = Ref(new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }, TextBlock.ForegroundProperty, TextSecondary);
        Button? test = null;
        test = Button(L.T("Test connection"), async () =>
        {
            FlushPending(); // a host or password still being typed counts
            test!.IsEnabled = false;
            status.Text = L.T("Connecting…");
            status.SetResourceReference(TextBlock.ForegroundProperty, TextSecondary);
            var (ok, message) = await ObsClient.TestAsync(AppConfig.Load().Obs);
            test.IsEnabled = true;
            status.Text = message;
            status.SetResourceReference(TextBlock.ForegroundProperty, ok ? SettingsPalette.Good : SettingsPalette.Warn);
        });
        test.Margin = new Thickness(0);
        Setting(card, L.T("Test connection"), null, new StackPanel { Orientation = Orientation.Horizontal, Children = { status, test } }, last: true, icon: "\uE703");
        _panel.Children.Add(Hint(L.T("In OBS: Tools → WebSocket Server Settings → Enable WebSocket server (OBS 28 or later). " +
                                 "Targets: record, pause, stream, replay (saves the replay buffer), replaybuffer, virtualcam, scene:Name, mute:Mic/Aux.")));
    }

    /// <summary>A masked box for the OBS password; saved when it loses focus or on Enter.</summary>
    FrameworkElement ObsPassword(string? value)
    {
        var box = new PasswordBox { Password = value ?? "", Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
        string last = box.Password;
        void Commit()
        {
            PendingCommits.Remove(Commit);
            if (box.Password == last)
                return;
            last = box.Password;
            string password = box.Password;
            Change(c => c.Obs.Password = password.Length == 0 ? null : password);
        }
        box.PasswordChanged += (_, _) =>
        {
            if (box.Password != last)
                PendingCommits.Add(Commit);
        };
        box.LostKeyboardFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
                Commit();
        };
        return box;
    }
}
