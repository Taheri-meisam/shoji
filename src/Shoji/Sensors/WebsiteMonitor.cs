using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Shoji.Core;

namespace Shoji.Sensors;

public enum SiteState { Checking, Up, Warning, Down }

/// <summary>Latest state of one website.</summary>
public sealed record SiteStatus(
    string Name,
    string Url,
    SiteState State,
    int? StatusCode,
    double? Ms,
    // Why it's down or in warning ("timeout", "HTTP 503", "text not found", "certificate expires in 6 days"…).
    string? Problem,
    int? CertDaysLeft,
    int? DomainDaysLeft,
    double? UptimePercent,
    IReadOnlyList<double> RecentMs,
    DateTime? DownSince);

/// <summary>
/// Checks websites on a schedule: response, speed, SSL certificate, domain registration and (optionally) that the
/// page still contains some text. One failed check only gives a warning and a quick retry; two in a row mean down.
/// </summary>
public sealed class WebsiteMonitor : IDisposable
{
    const int SlowMs = 2000;

    sealed class Site
    {
        public required SiteRef Ref { get; init; }
        public required Uri Uri { get; init; }
        public string Name => string.IsNullOrWhiteSpace(Ref.Name) ? Uri.Host : Ref.Name!;
        public SiteState State = SiteState.Checking;
        public int? Status;
        public double? Ms;
        public string? Problem;
        public int Failures;
        public DateTime? DownSince;
        public DateTime? CertExpires;
        public string? CertError;
        public DateTime? DomainExpires;
        public DateTime DomainCheckedAt = DateTime.MinValue;
        public readonly Queue<(DateTime At, bool Up, double? Ms)> History = new();
    }

    readonly List<Site> _sites = new();
    readonly TimeSpan _interval;
    readonly double _certDays;
    readonly HttpClient _http, _rdap;
    readonly CancellationTokenSource _cts = new();

    public WebsiteMonitor(IEnumerable<SiteRef> sites, int intervalSeconds, double certDays)
    {
        _interval = TimeSpan.FromSeconds(Math.Clamp(intervalSeconds, 15, 3600));
        _certDays = certDays;
        foreach (var s in sites)
        {
            if (Normalize(s.Url) is { } uri)
                _sites.Add(new Site { Ref = s, Uri = uri });
        }

        // Accept any certificate so the page can still be measured, but remember its expiry and any problem.
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (request, cert, _, errors) =>
            {
                foreach (var site in _sites.Where(x => x.Uri.Host.Equals(request.RequestUri?.Host, StringComparison.OrdinalIgnoreCase)))
                {
                    site.CertExpires = cert?.NotAfter.ToUniversalTime();
                    site.CertError = errors == SslPolicyErrors.None ? null : Describe(errors);
                }
                return true;
            },
        };
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Shoji-Uptime/1.0 (+site monitor on a desktop dashboard)");
        _rdap = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _rdap.DefaultRequestHeaders.UserAgent.ParseAdd("Shoji/1.9");
        if (_sites.Count > 0)
            _ = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>"example.com" → https://example.com/</summary>
    public static Uri? Normalize(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0)
            return null;
        if (!url.Contains("://"))
            url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") ? u : null;
    }

    static string Describe(SslPolicyErrors e) =>
        e.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) ? L.T("certificate is for another name")
        : e.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors) ? L.T("certificate not trusted or expired")
        : L.T("certificate problem");

    async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.WhenAll(_sites.Select(s => CheckAsync(s, ct)));
            // After a failure, look again soon instead of waiting a whole interval.
            var wait = _sites.Any(s => s.Failures > 0) ? TimeSpan.FromSeconds(Math.Min(15, _interval.TotalSeconds)) : _interval;
            try { await Task.Delay(wait, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    async Task CheckAsync(Site s, CancellationToken ct)
    {
        int? status = null;
        double? ms = null;
        string? problem = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            using var request = new HttpRequestMessage(HttpMethod.Get, s.Uri);
            request.Headers.ConnectionClose = true; // a fresh connection each time, so the certificate is seen every check
            var sw = Stopwatch.StartNew();
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            ms = sw.Elapsed.TotalMilliseconds;
            status = (int)response.StatusCode;
            if (status >= 400)
                problem = $"HTTP {status} {response.ReasonPhrase}";
            else if (!string.IsNullOrWhiteSpace(s.Ref.Contains))
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!body.Contains(s.Ref.Contains!, StringComparison.OrdinalIgnoreCase))
                    problem = L.F("page doesn't contain \"{0}\"", s.Ref.Contains);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            problem = L.T("no answer in 15 s");
        }
        catch (HttpRequestException ex)
        {
            problem = ex.InnerException?.Message is { Length: > 0 } inner ? Short(inner) : Short(ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = Short(ex.Message);
        }

        if (problem == null && s.CertError != null && s.Uri.Scheme == "https")
            problem = s.CertError;

        await CheckDomainAsync(s, ct);

        lock (s)
        {
            bool up = problem == null;
            s.Status = status;
            s.Ms = ms;
            s.Failures = up ? 0 : s.Failures + 1;
            var now = DateTime.UtcNow;
            s.History.Enqueue((now, up, ms));
            while (s.History.Count > 0 && now - s.History.Peek().At > TimeSpan.FromHours(24))
                s.History.Dequeue();

            if (!up && s.Failures >= 2)
            {
                s.State = SiteState.Down;
                s.DownSince ??= now;
                s.Problem = problem;
            }
            else
            {
                if (up) s.DownSince = null;
                string? warning = !up ? L.F("{0} (checking again)", problem) : Warning(s, ms);
                s.State = warning == null ? SiteState.Up : SiteState.Warning;
                s.Problem = warning;
            }
        }
    }

    string? Warning(Site s, double? ms)
    {
        int? cert = DaysLeft(s.CertExpires), domain = DaysLeft(s.DomainExpires);
        if (_certDays > 0 && cert <= _certDays) return cert <= 0 ? L.T("certificate has expired") : L.F("certificate expires in {0} days", cert);
        if (_certDays > 0 && domain <= _certDays * 2) return domain <= 0 ? L.T("domain registration has expired") : L.F("domain expires in {0} days", domain);
        if (ms > SlowMs) return L.F("slow: {0:0.0} s", ms / 1000);
        return null;
    }

    static int? DaysLeft(DateTime? utc) => utc is DateTime d ? (int)Math.Floor((d - DateTime.UtcNow).TotalDays) : null;

    /// <summary>Domain registration expiry from RDAP (the modern WHOIS), twice a day. Tries example.com before sub.example.com's parents.</summary>
    async Task CheckDomainAsync(Site s, CancellationToken ct)
    {
        if (DateTime.UtcNow - s.DomainCheckedAt < TimeSpan.FromHours(12) || System.Net.IPAddress.TryParse(s.Uri.Host, out _))
            return;
        s.DomainCheckedAt = DateTime.UtcNow;
        var labels = s.Uri.Host.Split('.');
        for (int n = 2; n <= labels.Length; n++)
        {
            var domain = string.Join('.', labels[^n..]);
            try
            {
                using var response = await _rdap.GetAsync($"https://rdap.org/domain/{domain}", ct);
                if (!response.IsSuccessStatusCode)
                    continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                if (!doc.RootElement.TryGetProperty("events", out var events))
                    continue;
                foreach (var e in events.EnumerateArray())
                {
                    if (e.TryGetProperty("eventAction", out var a) && a.GetString() == "expiration"
                        && e.TryGetProperty("eventDate", out var d) && DateTime.TryParse(d.GetString(), null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date))
                    {
                        s.DomainExpires = date;
                        return;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log.Write($"Domain lookup for {domain} failed: {ex.Message}");
            }
        }
    }

    static string Short(string message) => message.Length <= 80 ? message.TrimEnd('.') : message[..80] + "…";

    public IReadOnlyList<SiteStatus> Read()
    {
        var list = new List<SiteStatus>();
        foreach (var s in _sites)
        {
            lock (s)
            {
                double? uptime = s.History.Count == 0 ? null : s.History.Count(h => h.Up) * 100.0 / s.History.Count;
                var recent = s.History.Skip(Math.Max(0, s.History.Count - 40)).Select(h => h.Ms ?? 0).ToList();
                list.Add(new SiteStatus(s.Name, s.Uri.ToString(), s.State, s.Status, s.Ms, s.Problem,
                    DaysLeft(s.CertExpires), DaysLeft(s.DomainExpires), uptime, recent, s.DownSince));
            }
        }
        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
        _rdap.Dispose();
    }
}
