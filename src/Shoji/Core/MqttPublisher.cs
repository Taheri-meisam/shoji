using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>
/// Sends this PC's readings to an MQTT broker, so a smart home (Home Assistant, Node-RED, openHAB…) can show them and
/// act on them: "PC is in a game → dim the lights", "GPU over 80° → turn the fan on". Off by default.
/// </summary>
public sealed class MqttConfig
{
    public bool Enabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 1883;
    public bool Tls { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
    /// <summary>Topic the readings go under; empty = "shoji/&lt;this PC's name&gt;".</summary>
    public string? Topic { get; set; }
    /// <summary>Also announce the readings to Home Assistant, so they show up there as a device without any YAML.</summary>
    public bool Discovery { get; set; } = true;
    public int Seconds { get; set; } = 10;

    public string Signature => $"{Enabled}|{Host}|{Port}|{Tls}|{User}|{Password}|{Topic}|{Discovery}|{Seconds}";
}

/// <summary>
/// A small MQTT 3.1.1 publisher (QoS 0, retained): one JSON message with every reading on "&lt;topic&gt;/state" each few
/// seconds, "online" / "offline" on "&lt;topic&gt;/status" (offline is the broker's last-will, so a crash or power cut
/// shows too), and Home Assistant discovery messages. Publish-only: nothing sent to the PC is ever acted on.
/// </summary>
public sealed class MqttPublisher : IDisposable
{
    /// <summary>What the last connection attempt came to, for Settings ("Connected", "Connection refused"…).</summary>
    public static string? LastStatus { get; private set; }

    readonly MqttConfig _config;
    readonly string _base, _node;
    readonly CancellationTokenSource _cts = new();
    volatile Snapshot? _latest;

    public MqttPublisher(MqttConfig config)
    {
        _config = config;
        _node = "shoji_" + Slug(Environment.MachineName);
        _base = string.IsNullOrWhiteSpace(config.Topic) ? "shoji/" + Slug(Environment.MachineName) : config.Topic.Trim().Trim('/');
        LastStatus = null;
        _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>The newest readings; the loop sends them at its own pace.</summary>
    public void Publish(Snapshot s) => _latest = s;

    public void Dispose() => _cts.Cancel();

    /// <summary>The topic a setting of "" or null stands for (shown in Settings).</summary>
    public static string DefaultTopic => "shoji/" + Slug(Environment.MachineName);

    static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '_');
        return sb.Length == 0 ? "pc" : sb.ToString();
    }

    async Task LoopAsync(CancellationToken ct)
    {
        int wait = 5;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await SessionAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                string message = ex is SocketException or IOException ? ex.Message : ex.GetType().Name + ": " + ex.Message;
                if (LastStatus != message)
                    Log.Write($"MQTT {_config.Host}:{_config.Port}: {message}");
                LastStatus = message;
            }
            // Broker down or wrong password: try again, a little less often each time (up to every 2 minutes).
            try { await Task.Delay(TimeSpan.FromSeconds(wait), ct); }
            catch (OperationCanceledException) { return; }
            wait = Math.Min(wait * 2, 120);
        }
    }

    async Task SessionAsync(CancellationToken ct)
    {
        using var tcp = new TcpClient { NoDelay = true };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connect.CancelAfter(TimeSpan.FromSeconds(10));
            await tcp.ConnectAsync(_config.Host!.Trim(), _config.Port, connect.Token);
        }
        Stream stream = tcp.GetStream();
        if (_config.Tls)
        {
            var ssl = new SslStream(stream);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = _config.Host.Trim() }, ct);
            stream = ssl;
        }
        await using var closer = stream;

        int seconds = Math.Clamp(_config.Seconds, 2, 3600);
        int keepAlive = Math.Clamp(seconds * 3, 30, 65535);
        string statusTopic = _base + "/status";

        // CONNECT: clean session, last will "offline" (retained), optional user / password.
        var body = new MemoryStream();
        WriteString(body, "MQTT");
        body.WriteByte(4);
        bool user = !string.IsNullOrEmpty(_config.User), password = user && !string.IsNullOrEmpty(_config.Password);
        body.WriteByte((byte)(0x02 | 0x04 | 0x20 | (user ? 0x80 : 0) | (password ? 0x40 : 0)));
        body.WriteByte((byte)(keepAlive >> 8));
        body.WriteByte((byte)keepAlive);
        WriteString(body, _node + "_" + Guid.NewGuid().ToString("N")[..6]);
        WriteString(body, statusTopic);
        WriteString(body, "offline");
        if (user)
            WriteString(body, _config.User!);
        if (password)
            WriteString(body, _config.Password!);
        await SendAsync(stream, 0x10, body.ToArray(), ct);

        // CONNACK: 0x20, 2, flags, return code.
        var ack = new byte[4];
        using (var reply = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            reply.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.ReadExactlyAsync(ack, reply.Token);
        }
        if (ack[0] != 0x20 || ack[3] != 0)
            throw new IOException(ack[3] switch
            {
                4 or 5 => L.T("The broker refused the user name or password"),
                _ => L.F("The broker refused the connection (code {0})", ack[3]),
            });
        LastStatus = L.T("Connected");

        // Nothing is subscribed, so the broker only ever answers pings; read them away so they don't pile up, and
        // notice when it hangs up.
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var drain = Task.Run(async () =>
        {
            var buffer = new byte[64];
            try
            {
                while (await stream.ReadAsync(buffer, session.Token) > 0) { }
            }
            catch (Exception) { /* closed */ }
            session.Cancel();
        }, ct);

        try
        {
            await PublishAsync(stream, statusTopic, "online", true, session.Token);
            var announced = new HashSet<string>();
            while (true)
            {
                if (_latest is { } s)
                {
                    var values = Values(s);
                    if (_config.Discovery)
                        foreach (var (key, _) in values)
                            if (announced.Add(key))
                                await PublishAsync(stream, DiscoveryTopic(key), DiscoveryPayload(key), true, session.Token);
                    await PublishAsync(stream, _base + "/state", JsonSerializer.Serialize(values), true, session.Token);
                }
                else
                    await SendAsync(stream, 0xC0, Array.Empty<byte>(), session.Token); // PINGREQ while there is nothing to say yet
                await Task.Delay(TimeSpan.FromSeconds(seconds), session.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException(L.T("The broker closed the connection"));
        }
        catch (OperationCanceledException)
        {
            // Shoji is closing or MQTT was switched off: say goodbye properly, so "offline" shows at once.
            try
            {
                using var bye = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await PublishAsync(stream, statusTopic, "offline", true, bye.Token);
                await SendAsync(stream, 0xE0, Array.Empty<byte>(), bye.Token);
            }
            catch (Exception) { /* best effort */ }
            throw;
        }
    }

    // ---------------------------------------------------------------- what is sent

    /// <summary>key → (name, unit, Home Assistant device class, icon, binary).</summary>
    static readonly Dictionary<string, (string Name, string? Unit, string? Class, string? Icon, bool Binary)> Fields = new()
    {
        ["cpu_load"] = ("CPU load", "%", null, "mdi:cpu-64-bit", false),
        ["cpu_temp"] = ("CPU temperature", "°C", "temperature", null, false),
        ["cpu_power"] = ("CPU power", "W", "power", null, false),
        ["gpu_load"] = ("GPU load", "%", null, "mdi:expansion-card", false),
        ["gpu_temp"] = ("GPU temperature", "°C", "temperature", null, false),
        ["gpu_power"] = ("GPU power", "W", "power", null, false),
        ["gpu_fan"] = ("GPU fan", "rpm", null, "mdi:fan", false),
        ["memory_load"] = ("Memory", "%", null, "mdi:memory", false),
        ["power"] = ("PC power", "W", "power", null, false),
        ["energy_today"] = ("PC energy today", "kWh", "energy", null, false),
        ["fps"] = ("FPS", "fps", null, "mdi:speedometer", false),
        ["ping"] = ("Ping", "ms", null, "mdi:lan-pending", false),
        ["download"] = ("Download", "Mbit/s", "data_rate", null, false),
        ["upload"] = ("Upload", "Mbit/s", "data_rate", null, false),
        ["battery"] = ("Battery", "%", "battery", null, false),
        ["app"] = ("App in focus", null, null, "mdi:application", false),
        ["fullscreen"] = ("Fullscreen app (game)", null, null, "mdi:controller", true),
        ["in_use"] = ("In use", null, "occupancy", null, true),
        ["playing"] = ("Now playing", null, null, "mdi:music", false),
    };

    /// <summary>Only what the dashboard measures right now: a reading without a tile (or a sensor) is left out.</summary>
    static Dictionary<string, object> Values(Snapshot s)
    {
        var v = new Dictionary<string, object>();
        void Add(string key, double? value, int decimals = 0)
        {
            if (value is double d && double.IsFinite(d))
                v[key] = Math.Round(d, decimals);
        }
        Add("cpu_load", s.Cpu?.LoadPercent);
        Add("cpu_temp", s.Cpu?.TemperatureC);
        Add("cpu_power", s.Cpu?.PowerW);
        Add("gpu_load", s.Gpu?.LoadPercent);
        Add("gpu_temp", s.Gpu?.TemperatureC);
        Add("gpu_power", s.Gpu?.PowerW);
        Add("gpu_fan", s.Gpu?.FanRpm);
        Add("memory_load", s.Memory?.LoadPercent);
        Add("power", s.Power?.TotalW);
        Add("energy_today", s.Power?.TodayWh / 1000, 3);
        Add("fps", s.Fps?.Fps);
        Add("ping", s.Ping?.LastMs);
        Add("download", s.Network?.DownloadBytesPerSec * 8 / 1e6, 1);
        Add("upload", s.Network?.UploadBytesPerSec * 8 / 1e6, 1);
        if (s.Battery is { HasBattery: true } battery)
            Add("battery", battery.Percent);
        v["app"] = s.Foreground.ProcessName;
        v["fullscreen"] = s.Foreground.IsFullscreen ? "ON" : "OFF";
        v["in_use"] = s.IdleSeconds < 300 ? "ON" : "OFF";
        if (s.Media is { } media)
            v["playing"] = media.IsPlaying ? (media.Artist.Length > 0 ? $"{media.Artist} – {media.Title}" : media.Title) : "";
        return v;
    }

    string DiscoveryTopic(string key) => $"homeassistant/{(Fields[key].Binary ? "binary_sensor" : "sensor")}/{_node}/{key}/config";

    string DiscoveryPayload(string key)
    {
        var f = Fields[key];
        var config = new Dictionary<string, object>
        {
            ["name"] = f.Name,
            ["unique_id"] = $"{_node}_{key}",
            ["state_topic"] = _base + "/state",
            ["value_template"] = "{{ value_json." + key + " }}",
            ["availability_topic"] = _base + "/status",
            ["device"] = new Dictionary<string, object>
            {
                ["identifiers"] = new[] { _node },
                ["name"] = Environment.MachineName,
                ["manufacturer"] = "Shoji",
                ["model"] = "PC dashboard",
                ["sw_version"] = typeof(MqttPublisher).Assembly.GetName().Version?.ToString(3) ?? "",
            },
        };
        if (f.Unit != null)
        {
            config["unit_of_measurement"] = f.Unit;
            config["state_class"] = f.Class == "energy" ? "total_increasing" : "measurement";
        }
        if (f.Class != null)
            config["device_class"] = f.Class;
        if (f.Icon != null)
            config["icon"] = f.Icon;
        return JsonSerializer.Serialize(config);
    }

    // ---------------------------------------------------------------- the wire

    static Task PublishAsync(Stream stream, string topic, string payload, bool retain, CancellationToken ct)
    {
        var body = new MemoryStream();
        WriteString(body, topic);
        body.Write(Encoding.UTF8.GetBytes(payload));
        return SendAsync(stream, (byte)(0x30 | (retain ? 1 : 0)), body.ToArray(), ct);
    }

    static async Task SendAsync(Stream stream, byte header, byte[] body, CancellationToken ct)
    {
        var packet = new MemoryStream(body.Length + 5);
        packet.WriteByte(header);
        int length = body.Length; // "remaining length": 7 bits per byte, high bit = more follow
        do
        {
            byte b = (byte)(length % 128);
            length /= 128;
            packet.WriteByte(length > 0 ? (byte)(b | 0x80) : b);
        }
        while (length > 0);
        packet.Write(body);
        await stream.WriteAsync(packet.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    static void WriteString(Stream s, string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        s.WriteByte((byte)(bytes.Length >> 8));
        s.WriteByte((byte)bytes.Length);
        s.Write(bytes);
    }
}
