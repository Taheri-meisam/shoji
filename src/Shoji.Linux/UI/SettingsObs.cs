using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Shoji.Core;

namespace Shoji.UI;

/// <summary>Control deck → OBS Studio: where OBS's WebSocket server is, and a button to try it.</summary>
public sealed partial class SettingsWindow
{
    void AddObs(AppConfig config)
    {
        var obs = config.Obs;
        Section("OBS Studio", "Deck buttons with \"OBS Studio\" record, stream, switch scenes and mute inputs, and light up with OBS's state.");
        var card = Card();
        Setting(card, "Host", "localhost when OBS runs on this PC", TextField(obs.Host, 200, v => Change(c => c.Obs.Host = v ?? "localhost")), icon: "computer");
        Setting(card, "Port", null, TextField(obs.Port.ToString(), 90, v => Change(c => c.Obs.Port = int.TryParse(v, out int n) && n is > 0 and < 65536 ? n : 4455),
            check: v => (v == null || int.TryParse(v, out int n) && n is > 0 and < 65536, v ?? "4455"), invalidHint: "A port number (OBS uses 4455): this wasn't saved"), icon: "lan");
        var password = TextField(obs.Password, 200, v => Change(c => c.Obs.Password = v));
        password.PasswordChar = '•';
        Setting(card, "Password", "From OBS → Tools → WebSocket Server Settings → Show Connect Info. Empty if authentication is off.", password, icon: "password");

        var status = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 360, VerticalAlignment = VerticalAlignment.Center, Foreground = Look.Text2, Margin = new Thickness(0, 0, 12, 0) };
        Button? test = null;
        test = Button("Test connection", async () =>
        {
            FlushPending(); // a host or password still being typed counts
            test!.IsEnabled = false;
            status.Text = L.T("Connecting…");
            status.Foreground = Look.Text2;
            var (ok, message) = await ObsClient.TestAsync(AppConfig.Load().Obs);
            test.IsEnabled = true;
            status.Text = message;
            status.Foreground = ok ? Look.Good : Look.Warn;
        });
        test.Margin = new Thickness(0);
        Setting(card, "Test connection", null, new StackPanel { Orientation = Orientation.Horizontal, Children = { status, test } }, last: true, icon: "cable");
        _panel.Children.Add(Hint("In OBS: Tools → WebSocket Server Settings → Enable WebSocket server (OBS 28 or later). " +
                                 "Targets: record, pause, stream, replay (saves the replay buffer), replaybuffer, virtualcam, scene:Name, mute:Mic/Aux."));
    }
}
