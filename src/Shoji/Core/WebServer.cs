using System;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Sensors;

namespace Shoji.Core;

/// <summary>
/// A tiny web dashboard for a phone or tablet on the same network: GET / is the page, GET /api is the latest data
/// as JSON, and (only when the user allows it) POST /press?id=n presses control-deck button n. Every request needs
/// the secret key from the URL (shown, with a QR code, in Settings). Window titles are never exposed, and deck
/// buttons that run commands or scripts are never offered to the phone.
/// </summary>
public sealed class WebServer : IDisposable
{
    readonly int _port;
    readonly string _key;
    readonly TcpListener _listener;
    readonly CancellationTokenSource _cts = new();
    readonly SemaphoreSlim _slots = new(32); // a phone needs one or two; more than this is someone misbehaving
    readonly System.Collections.Generic.Dictionary<IPAddress, (int Count, DateTime Last)> _refused = new();
    volatile string _json = "{}";
    volatile DeckButton[]? _deck;
    DateTime _lastPress;

    /// <summary>Keys shorter than this are refused (<see cref="NewKey"/> makes 24 characters).</summary>
    public const int MinKeyLength = 16;

    public WebServer(int port, string key)
    {
        if (key.Length < MinKeyLength)
            throw new ArgumentException($"The web dashboard key must be at least {MinKeyLength} characters.", nameof(key));
        _port = port;
        _key = key;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start(); // Windows Firewall asks once whether to allow it on private networks
        _ = Task.Run(() => AcceptAsync(_cts.Token));
        Log.Write($"Web dashboard listening on port {port}");
    }

    public static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    /// <summary>
    /// LAN address of this PC, for the URL shown in Settings: a real (not virtual / VPN) adapter with a default gateway
    /// if there is one, so the QR code doesn't point at a Hyper-V, Docker or VPN address the phone can't reach.
    /// </summary>
    public static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => (Props: n.GetIPProperties(), Virtual: IsVirtual(n)))
                .Select(x => (x.Props, x.Virtual, Gateway: x.Props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)),
                              Address: x.Props.UnicastAddresses.Select(a => a.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))))
                .Where(x => x.Address != null)
                .OrderBy(x => (x.Virtual ? 2 : 0) + (x.Gateway ? 0 : 1))
                .Select(x => x.Address!.ToString())
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    static readonly string[] VirtualWords = { "virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "vbox", "docker", "wsl", "vpn", "tap-", "tunnel", "wireguard", "tailscale", "zerotier", "loopback" };
    static readonly string[] VirtualPrefixes = { "docker", "br-", "veth", "virbr", "vmnet", "vboxnet", "tun", "tap", "wg", "tailscale", "zt", "lxc", "lxd", "cni", "flannel" };

    static bool IsVirtual(NetworkInterface n)
    {
        if (n.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
            return true;
        string text = (n.Name + " " + n.Description).ToLowerInvariant();
        return VirtualWords.Any(text.Contains) || VirtualPrefixes.Any(p => n.Name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The deck buttons the phone may press, or null when phone control is off. Buttons that run a command or a
    /// script are left out: the page travels as plain HTTP on the local network.
    /// </summary>
    public void SetDeck(System.Collections.Generic.IEnumerable<DeckButton>? buttons) =>
        _deck = buttons?.Where(b => !IsShell(b)).ToArray();

    static bool IsShell(DeckButton b) => (b.Action ?? "").ToLowerInvariant() is "command" or "powershell";

    /// <summary>Presses a deck button for the phone (the host runs it on its UI thread); returns a short message.</summary>
    public Func<DeckButton, Task<string?>>? Press { get; set; }

    public static string Url(WebConfig c) => $"http://{LanAddress() ?? "localhost"}:{c.Port}/?key={c.Key}";

    /// <summary>Called by the dashboard on every refresh.</summary>
    public void Publish(Snapshot s, string theme)
    {
        var t = UI.Theme.Current;
        _json = JsonSerializer.Serialize(new
        {
            time = s.Time.ToString("HH:mm:ss"),
            date = s.Time.ToString(L.T("dddd d MMMM"), L.Culture),
            theme = new { name = theme, bg = t.Background, tile = t.Tile, accent = t.Accent, accent2 = t.Accent2, text = t.Text, dim = t.Dim, track = t.Track, warn = t.Warn, crit = t.Crit },
            fahrenheit = UI.Theme.Fahrenheit,
            cpu = s.Cpu == null ? null : new { name = s.Cpu.Name, load = s.Cpu.LoadPercent, temp = s.Cpu.TemperatureC, clock = s.Cpu.ClockMhz, power = s.Cpu.PowerW },
            gpu = s.Gpu == null ? null : new { name = s.Gpu.Name, load = s.Gpu.LoadPercent, temp = s.Gpu.TemperatureC, power = s.Gpu.PowerW, vramUsed = s.Gpu.VramUsedMb, vramTotal = s.Gpu.VramTotalMb },
            memory = s.Memory == null ? null : new { used = s.Memory.UsedGb, total = s.Memory.TotalGb, load = s.Memory.LoadPercent },
            network = s.Network == null ? null : new { down = s.Network.DownloadBytesPerSec, up = s.Network.UploadBytesPerSec },
            ping = s.Ping?.LastMs,
            fps = s.Fps?.Fps,
            app = s.Foreground.ProcessName,
            temps = s.Temps.Select(r => new { label = r.Label, value = r.Value }),
            sites = s.Sites.Select(x => new { name = x.Name, state = x.State.ToString().ToLowerInvariant(), ms = x.Ms, problem = x.Problem }),
            media = s.Media == null || string.IsNullOrEmpty(s.Media.Title) ? null : new { title = s.Media.Title, artist = s.Media.Artist, playing = s.Media.IsPlaying },
            deck = _deck?.Select((b, i) => new { id = i, label = b.Label, icon = DeckIcons.Glyph(b.Icon) == null ? b.Icon : null, glyph = DeckIcons.Glyph(b.Icon) }),
            history = new
            {
                cpu = HistoryStore.Shared.Recent("cpu.load", 60),
                gpu = HistoryStore.Shared.Recent("gpu.load", 60),
                down = HistoryStore.Shared.Recent("net.down", 60),
            },
        });
    }

    async Task AcceptAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested)
                    return;
                // A client that resets mid-handshake shouldn't stop the server.
                Log.Write($"Web dashboard accept failed: {ex.Message}");
                await Task.Delay(200, CancellationToken.None);
                continue;
            }
            if (!_slots.Wait(0))
            {
                client.Dispose(); // too many open connections: drop this one
                continue;
            }
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(client); }
                finally { _slots.Release(); }
            });
        }
    }

    /// <summary>Reads until the end of the request line ("GET /api?key=… HTTP/1.1"), at most 4 KB and 5 seconds.</summary>
    static async Task<string?> ReadRequestLineAsync(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[4096];
        int length = 0;
        while (length < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token);
            if (n == 0)
                break;
            length += n;
            int end = Encoding.ASCII.GetString(buffer, 0, length).IndexOf("\r\n", StringComparison.Ordinal);
            if (end >= 0)
                return Encoding.ASCII.GetString(buffer, 0, end);
        }
        return null; // closed early, or a line too long to be a real request
    }

    /// <summary>Wrong keys get slower answers (up to 2 s), so guessing from the network is pointless.</summary>
    async Task SlowDownAsync(IPAddress? ip)
    {
        if (ip == null)
            return;
        int count;
        lock (_refused)
        {
            var now = DateTime.UtcNow;
            if (_refused.Count > 1000)
                _refused.Clear();
            count = _refused.TryGetValue(ip, out var r) && now - r.Last < TimeSpan.FromMinutes(10) ? r.Count + 1 : 1;
            _refused[ip] = (count, now);
        }
        if (count > 3)
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(2000, (count - 3) * 250)));
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 5000;
                var stream = client.GetStream();
                var line = await ReadRequestLineAsync(stream);
                if (line == null)
                    return;
                var first = line.Split(' ');
                if (first.Length < 2 || first[0] is not ("GET" or "POST"))
                {
                    await Send(stream, 405, "text/plain", "Method not allowed");
                    return;
                }

                var target = first[1];
                var path = target.Split('?')[0];
                var key = System.Text.RegularExpressions.Regex.Match(target, @"[?&]key=([^&#]*)") is { Success: true } km ? km.Groups[1].Value : "";
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(_key)))
                {
                    await SlowDownAsync((client.Client.RemoteEndPoint as IPEndPoint)?.Address);
                    await Send(stream, 403, "text/plain; charset=utf-8", L.T("Wrong or missing key. Use the link or QR code from Shoji → Settings → Phone dashboard."));
                    return;
                }

                if (first[0] == "POST")
                {
                    if (path == "/press")
                        await PressAsync(stream, target);
                    else
                        await Send(stream, 405, "text/plain", "Method not allowed");
                }
                else if (path == "/api")
                    await Send(stream, 200, "application/json", _json);
                else if (path == "/")
                    await Send(stream, 200, "text/html; charset=utf-8", LocalPage());
                else
                    await Send(stream, 404, "text/plain", "Not found");
            }
            catch
            {
                // client went away
            }
        }
    }

    async Task PressAsync(NetworkStream stream, string target)
    {
        var deck = _deck;
        var press = Press;
        if (deck == null || press == null)
        {
            await Send(stream, 403, "application/json", Message(false, L.T("Phone buttons are off (Shoji → Settings → Phone dashboard).")));
            return;
        }
        var idText = System.Text.RegularExpressions.Regex.Match(target, @"[?&]id=([0-9]{1,4})(?:&|$)");
        int id = idText.Success ? int.Parse(idText.Groups[1].Value) : -1;
        if (id < 0 || id >= deck.Length)
        {
            await Send(stream, 404, "application/json", Message(false, L.T("That button is gone; the page will refresh.")));
            return;
        }
        lock (deck)
        {
            // A stuck finger or a script shouldn't fire a button dozens of times a second.
            if (DateTime.UtcNow - _lastPress < TimeSpan.FromMilliseconds(150))
                id = -1;
            else
                _lastPress = DateTime.UtcNow;
        }
        if (id < 0)
        {
            await Send(stream, 429, "application/json", Message(false, L.T("Slow down")));
            return;
        }
        string? message;
        try
        {
            message = await press(deck[id]);
        }
        catch (Exception ex)
        {
            Log.Write($"Phone deck button failed: {ex.Message}");
            await Send(stream, 200, "application/json", Message(false, ex.Message));
            return;
        }
        await Send(stream, 200, "application/json", Message(true, message ?? deck[id].Label));
    }

    static string Message(bool ok, string text) => JsonSerializer.Serialize(new { ok, message = text });

    static async Task Send(NetworkStream stream, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        string reason = status switch { 200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", 429 => "Too Many Requests", _ => "Error" };
        var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\n" +
                   "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(bytes);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { /* already stopped */ }
    }

    sealed record PageCache(string Language, string Html);
    static volatile PageCache? _page;

    /// <summary>
    /// <see cref="Page"/> in the current language: its few words are swapped in here (HTML- or JS-escaped), so the
    /// page's script stays one plain English constant. Cached per language.
    /// </summary>
    static string LocalPage()
    {
        if (_page is { } cached && cached.Language == L.Language)
            return cached.Html;
        string html = Page.Replace("<html lang=\"en\">", $"<html lang=\"{L.Language}\">");
        foreach (var (english, local) in new[]
                 {
                     ("Control deck", L.T("Control deck")), ("Memory", L.T("Memory")), ("Network", L.T("Network")),
                     ("Temperatures", L.T("Temperatures")), ("Websites", L.T("Websites")), ("Now playing", L.T("Now playing")),
                 })
            if (local != english)
                html = html.Replace($"<h2>{english}</h2>", $"<h2>{WebUtility.HtmlEncode(local)}</h2>");
        if (L.T("Connection lost, retrying…") is var lost && lost != "Connection lost, retrying…")
            html = html.Replace(">Connection lost, retrying…<", $">{WebUtility.HtmlEncode(lost)}<");
        foreach (var (english, local) in new[] { ("Done", L.T("Done")), ("No connection", L.T("No connection")), ("DOWN", L.T("DOWN")) })
            if (local != english)
                html = html.Replace($"'{english}'", JsonSerializer.Serialize(local)); // a JS string literal
        _page = new PageCache(L.Language, html);
        return html;
    }

    const string Page = """
<!doctype html>
<html lang="en"><head>
<meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
<meta name="theme-color" content="#0B0E14"><title>Shoji</title>
<style>
:root{--bg:#0B0E14;--tile:#151A24;--accent:#4FC3F7;--accent2:#CE93D8;--text:#E8EBF2;--dim:#8A93A6;--track:#252C3A;--warn:#FFB74D;--crit:#EF5350}
*{box-sizing:border-box}html,body{margin:0;background:var(--bg);color:var(--text);font:15px/1.35 "Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
main{display:grid;grid-template-columns:repeat(auto-fill,minmax(170px,1fr));gap:10px;padding:12px max(12px,env(safe-area-inset-left))}
.t{background:var(--tile);border-radius:14px;padding:12px 14px;min-height:96px}.w{grid-column:1/-1}
h2{margin:0 0 4px;font-size:12px;letter-spacing:.06em;text-transform:uppercase;color:var(--accent)}
.big{font-size:34px;font-weight:600;line-height:1.1}.dim{color:var(--dim);font-size:13px}.row{display:flex;justify-content:space-between;gap:8px}
canvas{width:100%;height:40px;display:block;margin-top:6px}.bar{height:6px;border-radius:3px;background:var(--track);overflow:hidden;margin-top:6px}.bar i{display:block;height:100%;background:var(--accent)}
.clock{font-size:56px;font-weight:300}.hot{color:var(--warn)}.crit{color:var(--crit)}#off{display:none;color:var(--warn)}
.deck{display:grid;grid-template-columns:repeat(auto-fill,minmax(92px,1fr));gap:8px;margin-top:6px}
.deck button{min-height:76px;border:0;border-radius:12px;background:var(--track);color:var(--text);font:inherit;font-size:13px;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:4px;padding:8px;touch-action:manipulation;-webkit-tap-highlight-color:transparent}
.deck button:active{background:var(--accent);color:var(--bg)}.deck .ic{font-size:26px;line-height:1}.mi{font-family:"Material Icons";font-weight:normal;font-style:normal;display:none;font-size:28px;line-height:1;letter-spacing:normal;text-transform:none;white-space:nowrap;-webkit-font-feature-settings:"liga";font-feature-settings:"liga"}
.miok .mi{display:inline-block}#toast{position:fixed;left:50%;bottom:22px;transform:translateX(-50%);background:var(--tile);color:var(--text);border:1px solid var(--accent);border-radius:20px;padding:8px 16px;opacity:0;transition:opacity .25s;pointer-events:none}
</style><link rel="stylesheet" href="https://fonts.googleapis.com/icon?family=Material+Icons"></head><body>
<div id="toast"></div>
<main id="m"><section class="t w"><div class="clock" id="time">--:--</div><div class="dim" id="date"></div><div id="off">Connection lost, retrying…</div></section></main>
<script>
const key=new URLSearchParams(location.search).get('key')||'';const m=document.getElementById('m');let F=false,deckSig='';
// Icon glyphs need the Material Icons font (from the internet); without it the buttons show their labels only.
document.fonts&&document.fonts.load('28px "Material Icons"').then(f=>{if(f.length)document.body.classList.add('miok')}).catch(()=>{});
function toast(t){const e=document.getElementById('toast');e.textContent=t;e.style.opacity=1;clearTimeout(toast.h);toast.h=setTimeout(()=>e.style.opacity=0,1600)}
async function press(id){try{const r=await fetch('/press?key='+encodeURIComponent(key)+'&id='+id,{method:'POST',cache:'no-store'});const d=await r.json();toast(d.message||'Done');if(r.status===404)deckSig=''}catch(e){toast('No connection')}}
function deck(list){const sig=JSON.stringify(list||[]);if(sig===deckSig)return;deckSig=sig;const old=document.getElementById('deck');if(!list||!list.length){if(old)old.remove();return}
const e=tile('deck','<h2>Control deck</h2><div class="deck"></div>',true),g=e.querySelector('.deck');
for(const b of list){const k=document.createElement('button');k.innerHTML=(b.glyph?`<span class="mi">${esc(b.glyph)}</span>`:b.icon?`<span class="ic">${esc(b.icon)}</span>`:'')+`<span>${esc(b.label)}</span>`;k.onclick=()=>press(b.id);g.appendChild(k)}}
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const T=c=>c==null?'–':Math.round(F?c*9/5+32:c)+'°';const P=v=>v==null?'–':Math.round(v)+'%';
const R=b=>{const x=b*8;return x>=1e9?(x/1e9).toFixed(2)+' Gbps':x>=1e6?(x/1e6).toFixed(1)+' Mbps':x>=1e3?Math.round(x/1e3)+' Kbps':Math.round(x)+' bps'};
function tile(id,html,wide){let e=document.getElementById(id);if(!e){e=document.createElement('section');e.id=id;e.className='t'+(wide?' w':'');m.appendChild(e)}e.innerHTML=html;return e}
function spark(id,data,color){const c=document.querySelector('#'+id+' canvas');if(!c||!data||data.length<2)return;const r=devicePixelRatio||1;c.width=c.clientWidth*r;c.height=c.clientHeight*r;
const g=c.getContext('2d'),w=c.width,h=c.height,max=Math.max(1,...data)*1.1;g.beginPath();data.forEach((v,i)=>{const x=i/(data.length-1)*w,y=h-v/max*h;i?g.lineTo(x,y):g.moveTo(x,y)});g.strokeStyle=color;g.lineWidth=2*r;g.stroke()}
function theme(t){const s=document.documentElement.style;for(const k of['bg','tile','accent','accent2','text','dim','track','warn','crit'])if(t[k])s.setProperty('--'+k,t[k].length===9?'#'+t[k].slice(3):t[k]);document.querySelector('meta[name=theme-color]').content=t.bg}
async function tick(){try{const r=await fetch('/api?key='+encodeURIComponent(key),{cache:'no-store'});if(!r.ok)throw 0;const d=await r.json();F=d.fahrenheit;theme(d.theme);
document.getElementById('off').style.display='none';document.getElementById('time').textContent=d.time.slice(0,5);document.getElementById('date').textContent=d.date+(d.app?' · '+d.app:'');
const cs=getComputedStyle(document.documentElement),A=cs.getPropertyValue('--accent'),B=cs.getPropertyValue('--accent2');
if(d.cpu){tile('cpu',`<h2>CPU</h2><div class="row"><span class="big">${P(d.cpu.load)}</span><span class="big ${d.cpu.temp>=85?'crit':d.cpu.temp>=70?'hot':''}">${T(d.cpu.temp)}</span></div><div class="dim">${esc(d.cpu.name)}</div><canvas></canvas>`);spark('cpu',d.history.cpu,A)}
if(d.gpu){tile('gpu',`<h2>GPU</h2><div class="row"><span class="big">${P(d.gpu.load)}</span><span class="big ${d.gpu.temp>=85?'crit':d.gpu.temp>=70?'hot':''}">${T(d.gpu.temp)}</span></div><div class="dim">${esc(d.gpu.name)}${d.gpu.power?' · '+Math.round(d.gpu.power)+' W':''}</div><canvas></canvas>`);spark('gpu',d.history.gpu,A)}
if(d.fps!=null)tile('fps',`<h2>FPS</h2><div class="big">${Math.round(d.fps)}</div><div class="dim">${esc(d.app)}</div>`);
if(d.memory)tile('mem',`<h2>Memory</h2><div class="big">${d.memory.used.toFixed(1)}<span class="dim"> / ${Math.round(d.memory.total)} GB</span></div><div class="bar"><i style="width:${d.memory.load}%"></i></div>`);
if(d.network){tile('net',`<h2>Network</h2><div>▼ ${R(d.network.down)}</div><div style="color:var(--accent2)">▲ ${R(d.network.up)}</div>${d.ping!=null?`<div class="dim">ping ${Math.round(d.ping)} ms</div>`:''}<canvas></canvas>`);spark('net',d.history.down,A)}
if(d.temps&&d.temps.length)tile('temps','<h2>Temperatures</h2>'+d.temps.map(x=>`<div class="row"><span class="dim">${esc(x.label)}</span><span>${T(x.value)}</span></div>`).join(''));
if(d.sites&&d.sites.length)tile('sites','<h2>Websites</h2>'+d.sites.map(x=>`<div class="row"><span><span style="color:${x.state==='down'?'var(--crit)':x.state==='warning'?'var(--warn)':'#5ad38c'}">●</span> ${esc(x.name)}</span><span class="${x.state==='down'?'crit':''}">${x.state==='down'?'DOWN':x.ms!=null?Math.round(x.ms)+' ms':'…'}</span></div>`).join(''));
deck(d.deck);
if(d.media)tile('media',`<h2>Now playing</h2><div style="font-weight:600">${esc(d.media.title)}</div><div class="dim">${esc(d.media.artist)} ${d.media.playing?'▶':'❚❚'}</div>`,true);
}catch(e){document.getElementById('off').style.display='block'}}
tick();setInterval(tick,1000);
</script></body></html>
""";
}
