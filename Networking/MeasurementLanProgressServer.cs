using System.Net;
using System.Net.Sockets;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MeaSound
{
    internal sealed class MeasurementLanProgressServer : IDisposable
    {
        private readonly object _stateLock = new();
        private readonly HashSet<string> _sessions = new(StringComparer.Ordinal);
        private TcpListener? _listener;
        private CancellationTokenSource? _cts;
        private Task? _acceptLoopTask;
        private string _password = string.Empty;

        private string _status = "Pøipraveno";
        private int _completedSteps;
        private int _totalSteps = 1;
        private string _measurementIndex = "0 / 1";
        private DateTime _updatedAtUtc = DateTime.UtcNow;
        private bool _showStatus = true;
        private bool _showMeasurementIndex = true;
        private bool _showStepProgress = true;
        private bool _showTimestamp = true;
        private int _refreshMs = 1000;

        private string _inputMode = "-";
        private string _signalType = "-";
        private string _sampleRate = "-";
        private string _bitDepth = "-";
        private string _inputDevice = "-";
        private string _outputDevice = "-";

        private byte[]? _polarImage;
        private byte[]? _fftImage;
        private byte[]? _spectrogramImage;

        public bool IsRunning => _listener != null;
        public int Port { get; private set; }
        public string PublicUrl { get; private set; } = string.Empty;

        public void SetPassword(string? password)
        {
            lock (_stateLock)
            {
                _password = password?.Trim() ?? string.Empty;
                _sessions.Clear();
            }
        }

        public void SetDisplayOptions(bool showStatus, bool showMeasurementIndex, bool showStepProgress, bool showTimestamp, int refreshMs)
        {
            lock (_stateLock)
            {
                _showStatus = showStatus;
                _showMeasurementIndex = showMeasurementIndex;
                _showStepProgress = showStepProgress;
                _showTimestamp = showTimestamp;
                _refreshMs = Math.Clamp(refreshMs, 300, 10000);
            }
        }

        public bool Start(int preferredPort, out string? error)
        {
            error = null;
            if (IsRunning)
                return true;

            try
            {
                _listener = new TcpListener(IPAddress.Any, preferredPort);
                _listener.Start();
                Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
                PublicUrl = $"http://{GetBestLocalIpv4Address()}:{Port}/";

                _cts = new CancellationTokenSource();
                _acceptLoopTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Stop();
                return false;
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            _listener = null;
            _cts?.Dispose();
            _cts = null;
            _acceptLoopTask = null;
            Port = 0;
            PublicUrl = string.Empty;
            lock (_stateLock) _sessions.Clear();
        }

        public void UpdateStatus(string status)
        {
            lock (_stateLock)
            {
                _status = string.IsNullOrWhiteSpace(status) ? "-" : status;
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        public void UpdateStepProgress(int completedSteps, int totalSteps)
        {
            lock (_stateLock)
            {
                _completedSteps = Math.Max(0, completedSteps);
                _totalSteps = Math.Max(1, totalSteps);
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        public void UpdateMeasurementIndex(string measurementIndex)
        {
            lock (_stateLock)
            {
                _measurementIndex = string.IsNullOrWhiteSpace(measurementIndex) ? "0 / 1" : measurementIndex;
                _updatedAtUtc = DateTime.UtcNow;
            }
        }

        public void UpdateMeasurementDetails(string inputMode, string signalType, string sampleRate, string bitDepth, string inputDevice, string outputDevice)
        {
            lock (_stateLock)
            {
                _inputMode = string.IsNullOrWhiteSpace(inputMode) ? "-" : inputMode;
                _signalType = string.IsNullOrWhiteSpace(signalType) ? "-" : signalType;
                _sampleRate = string.IsNullOrWhiteSpace(sampleRate) ? "-" : sampleRate;
                _bitDepth = string.IsNullOrWhiteSpace(bitDepth) ? "-" : bitDepth;
                _inputDevice = string.IsNullOrWhiteSpace(inputDevice) ? "-" : inputDevice;
                _outputDevice = string.IsNullOrWhiteSpace(outputDevice) ? "-" : outputDevice;
            }
        }

        public void UpdateGraphImage(string graphType, byte[]? pngBytes)
        {
            lock (_stateLock)
            {
                switch (graphType)
                {
                    case "polar":
                        _polarImage = pngBytes;
                        break;
                    case "fft":
                        _fftImage = pngBytes;
                        break;
                    case "spectrogram":
                        _spectrogramImage = pngBytes;
                        break;
                }
            }
        }

        public ProgressSnapshot GetSnapshot()
        {
            lock (_stateLock)
            {
                double percent = _totalSteps > 0 ? (_completedSteps * 100.0) / _totalSteps : 0;
                return new ProgressSnapshot(
                    Status: _status,
                    MeasurementIndex: _measurementIndex,
                    CompletedSteps: _completedSteps,
                    TotalSteps: _totalSteps,
                    ProgressPercent: Math.Clamp(percent, 0, 100),
                    UpdatedAtUtc: _updatedAtUtc,
                    InputMode: _inputMode,
                    SignalType: _signalType,
                    SampleRate: _sampleRate,
                    BitDepth: _bitDepth,
                    InputDevice: _inputDevice,
                    OutputDevice: _outputDevice);
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            if (_listener == null)
                return;

            while (!token.IsCancellationRequested)
            {
                TcpClient? client = null;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    _ = Task.Run(() => HandleClientAsync(client, token), token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    try { client?.Dispose(); } catch { }
                }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                HttpRequestData request = await ReadRequestAsync(stream, token).ConfigureAwait(false);
                string pathOnly = request.Path.Split('?', 2)[0];

                if (pathOnly.Equals("/login", StringComparison.OrdinalIgnoreCase) &&
                    request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    await HandleLoginAsync(stream, request, token).ConfigureAwait(false);
                    return;
                }

                if (!IsAuthorized(request))
                {
                    if (pathOnly.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
                    {
                        await WriteResponseAsync(stream, "401 Unauthorized", "application/json; charset=utf-8", "{\"error\":\"unauthorized\"}", token).ConfigureAwait(false);
                        return;
                    }

                    await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", BuildLoginPage(false), token).ConfigureAwait(false);
                    return;
                }

                if (pathOnly.StartsWith("/api/progress", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = BuildPayload();
                    string json = JsonSerializer.Serialize(payload);
                    await WriteResponseAsync(stream, "200 OK", "application/json; charset=utf-8", json, token).ConfigureAwait(false);
                    return;
                }

                if (pathOnly.StartsWith("/api/graph/", StringComparison.OrdinalIgnoreCase))
                {
                    byte[]? graph = GetGraphByPath(pathOnly);
                    if (graph == null)
                    {
                        await WriteResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Graf není k dispozici", token).ConfigureAwait(false);
                        return;
                    }

                    await WriteBinaryResponseAsync(stream, "200 OK", "image/png", graph, token).ConfigureAwait(false);
                    return;
                }

                await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", BuildHtmlPage(), token).ConfigureAwait(false);
            }
        }

        private async Task HandleLoginAsync(NetworkStream stream, HttpRequestData request, CancellationToken token)
        {
            string postedPassword = ParseFormValue(request.Body, "password");

            lock (_stateLock)
            {
                if (_password.Length == 0)
                {
                    _password = postedPassword;
                }
            }

            bool valid;
            string session = string.Empty;
            lock (_stateLock)
            {
                valid = _password.Length > 0 && string.Equals(_password, postedPassword, StringComparison.Ordinal);
                if (valid)
                {
                    session = Guid.NewGuid().ToString("N");
                    _sessions.Add(session);
                }
            }

            if (valid)
            {
                string header = $"Set-Cookie: msession={session}; Path=/; HttpOnly; SameSite=Lax";
                await WriteResponseAsync(stream, "302 Found", "text/plain; charset=utf-8", string.Empty, token, [header, "Location: /"]).ConfigureAwait(false);
                return;
            }

            await WriteResponseAsync(stream, "200 OK", "text/html; charset=utf-8", BuildLoginPage(true), token).ConfigureAwait(false);
        }

        private bool IsAuthorized(HttpRequestData request)
        {
            lock (_stateLock)
            {
                if (string.IsNullOrWhiteSpace(_password))
                    return false;
            }

            if (!request.Headers.TryGetValue("Cookie", out string? cookieHeader) || string.IsNullOrWhiteSpace(cookieHeader))
                return false;

            string session = ParseCookie(cookieHeader, "msession");
            if (string.IsNullOrWhiteSpace(session))
                return false;

            lock (_stateLock)
            {
                return _sessions.Contains(session);
            }
        }

        private object BuildPayload()
        {
            lock (_stateLock)
            {
                double percent = _totalSteps > 0 ? (_completedSteps * 100.0) / _totalSteps : 0;
                return new
                {
                    status = _status,
                    completedSteps = _completedSteps,
                    totalSteps = _totalSteps,
                    progressPercent = Math.Clamp(percent, 0, 100),
                    measurementIndex = _measurementIndex,
                    updatedAtUtc = _updatedAtUtc,
                    inputMode = _inputMode,
                    signalType = _signalType,
                    sampleRate = _sampleRate,
                    bitDepth = _bitDepth,
                    inputDevice = _inputDevice,
                    outputDevice = _outputDevice
                };
            }
        }

        private byte[]? GetGraphByPath(string pathOnly)
        {
            lock (_stateLock)
            {
                if (pathOnly.Contains("polar", StringComparison.OrdinalIgnoreCase))
                    return _polarImage;
                if (pathOnly.Contains("fft", StringComparison.OrdinalIgnoreCase))
                    return _fftImage;
                if (pathOnly.Contains("spectrogram", StringComparison.OrdinalIgnoreCase))
                    return _spectrogramImage;

                return null;
            }
        }

        private static async Task<HttpRequestData> ReadRequestAsync(NetworkStream stream, CancellationToken token)
        {
            byte[] buffer = new byte[4096];
            using var ms = new MemoryStream();
            int headerEndIndex = -1;
            int contentLength = 0;

            while (headerEndIndex < 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                if (read <= 0)
                    break;

                ms.Write(buffer, 0, read);
                byte[] current = ms.ToArray();
                headerEndIndex = FindHeaderEnd(current);
                if (headerEndIndex >= 0)
                {
                    string headerText = Encoding.UTF8.GetString(current, 0, headerEndIndex);
                    contentLength = GetContentLength(headerText);
                    while (current.Length - (headerEndIndex + 4) < contentLength)
                    {
                        read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                        if (read <= 0)
                            break;
                        ms.Write(buffer, 0, read);
                        current = ms.ToArray();
                    }
                }
            }

            string requestText = Encoding.UTF8.GetString(ms.ToArray());
            return ParseRequest(requestText);
        }

        private static int FindHeaderEnd(byte[] bytes)
        {
            for (int i = 0; i < bytes.Length - 3; i++)
            {
                if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                    return i;
            }

            return -1;
        }

        private static int GetContentLength(string headerText)
        {
            var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    string value = line.Substring("Content-Length:".Length).Trim();
                    if (int.TryParse(value, out int len) && len >= 0)
                        return len;
                }
            }

            return 0;
        }

        private static HttpRequestData ParseRequest(string request)
        {
            if (string.IsNullOrWhiteSpace(request))
                return new HttpRequestData();

            string[] parts = request.Split("\r\n\r\n", 2, StringSplitOptions.None);
            string headerBlock = parts[0];
            string body = parts.Length > 1 ? parts[1] : string.Empty;

            string[] lines = headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
                return new HttpRequestData();

            string[] requestLine = lines[0].Split(' ');
            var data = new HttpRequestData
            {
                Method = requestLine.Length > 0 ? requestLine[0] : "GET",
                Path = requestLine.Length > 1 ? requestLine[1] : "/",
                Body = body
            };

            for (int i = 1; i < lines.Length; i++)
            {
                int idx = lines[i].IndexOf(':');
                if (idx <= 0)
                    continue;

                string key = lines[i][..idx].Trim();
                string value = lines[i][(idx + 1)..].Trim();
                data.Headers[key] = value;
            }

            return data;
        }

        private static string ParseFormValue(string body, string key)
        {
            if (string.IsNullOrWhiteSpace(body))
                return string.Empty;

            string[] pairs = body.Split('&', StringSplitOptions.RemoveEmptyEntries);
            foreach (string pair in pairs)
            {
                string[] kv = pair.Split('=', 2);
                if (kv.Length != 2)
                    continue;

                if (!string.Equals(Uri.UnescapeDataString(kv[0]), key, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Uri.UnescapeDataString(kv[1].Replace('+', ' '));
            }

            return string.Empty;
        }

        private static string ParseCookie(string cookieHeader, string name)
        {
            string[] cookies = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (string cookie in cookies)
            {
                string[] kv = cookie.Split('=', 2);
                if (kv.Length != 2)
                    continue;

                if (string.Equals(kv[0].Trim(), name, StringComparison.OrdinalIgnoreCase))
                    return kv[1].Trim();
            }

            return string.Empty;
        }

        private static async Task WriteResponseAsync(NetworkStream stream, string status, string contentType, string body, CancellationToken token, IEnumerable<string>? extraHeaders = null)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            var builder = new StringBuilder();
            builder.Append($"HTTP/1.1 {status}\r\n");
            builder.Append($"Content-Type: {contentType}\r\n");
            builder.Append($"Content-Length: {bodyBytes.Length}\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("Connection: close\r\n");

            if (extraHeaders != null)
            {
                foreach (string header in extraHeaders)
                    builder.Append(header).Append("\r\n");
            }

            builder.Append("\r\n");
            byte[] headerBytes = Encoding.UTF8.GetBytes(builder.ToString());

            await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), token).ConfigureAwait(false);
            if (bodyBytes.Length > 0)
                await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static async Task WriteBinaryResponseAsync(NetworkStream stream, string status, string contentType, byte[] bodyBytes, CancellationToken token)
        {
            var builder = new StringBuilder();
            builder.Append($"HTTP/1.1 {status}\r\n");
            builder.Append($"Content-Type: {contentType}\r\n");
            builder.Append($"Content-Length: {bodyBytes.Length}\r\n");
            builder.Append("Cache-Control: no-store\r\n");
            builder.Append("Connection: close\r\n\r\n");

            byte[] headerBytes = Encoding.UTF8.GetBytes(builder.ToString());
            await stream.WriteAsync(headerBytes.AsMemory(0, headerBytes.Length), token).ConfigureAwait(false);
            await stream.WriteAsync(bodyBytes.AsMemory(0, bodyBytes.Length), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        private static string GetBestLocalIpv4Address()
        {
            try
            {
                var host = Dns.GetHostEntry(Dns.GetHostName());
                var ip = host.AddressList.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
                if (ip != null)
                    return ip.ToString();
            }
            catch { }

            return "localhost";
        }

        private static string BuildLoginPage(bool hasError)
        {
            string errorHtml = hasError ? "<div class='err'>Neplatné heslo.</div>" : string.Empty;
            return
            """
            <!doctype html>
            <html lang="cs">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>MeaSound - pøihlášení</title>
              <style>
                body { font-family: Arial, sans-serif; margin: 16px; background: #121212; color: #f5f5f5; }
                .card { background: #1f1f1f; border-radius: 12px; padding: 16px; max-width: 420px; margin: 0 auto; }
                input { width: 100%; box-sizing: border-box; padding: 10px; border-radius: 8px; border: 1px solid #444; background: #181818; color: #fff; }
                button { width: 100%; margin-top: 10px; padding: 10px; border: 0; border-radius: 8px; background: #2e7d32; color: white; font-weight: 600; }
                .err { color: #ff8a80; margin-top: 8px; }
              </style>
            </head>
            <body>
              <div class="card">
                <h3>MeaSound – pøístup chránìný heslem</h3>
                <form method="post" action="/login">
                  <input type="password" name="password" placeholder="Zadejte heslo" autocomplete="current-password" required />
                  <button type="submit">Pøihlásit</button>
                </form>
                __ERROR__
              </div>
            </body>
            </html>
            """.Replace("__ERROR__", errorHtml, StringComparison.Ordinal);
        }

        private string BuildHtmlPage()
        {
            bool showStatus;
            bool showMeasurementIndex;
            bool showStepProgress;
            bool showTimestamp;
            int refreshMs;

            lock (_stateLock)
            {
                showStatus = _showStatus;
                showMeasurementIndex = _showMeasurementIndex;
                showStepProgress = _showStepProgress;
                showTimestamp = _showTimestamp;
                refreshMs = _refreshMs;
            }

            return
            """
            <!doctype html>
            <html lang="cs">
            <head>
              <meta charset="utf-8" />
              <meta name="viewport" content="width=device-width, initial-scale=1" />
              <title>MeaSound - prùbìh mìøení</title>
              <style>
                body { font-family: Arial, sans-serif; margin: 16px; background: #121212; color: #f5f5f5; }
                .card { background: #1f1f1f; border-radius: 12px; padding: 16px; max-width: 620px; margin: 0 auto; }
                .label { color: #b0b0b0; font-size: 12px; text-transform: uppercase; margin-top: 12px; }
                .value { font-size: 18px; margin-top: 4px; }
                .progress { width: 100%; height: 18px; background: #333; border-radius: 10px; overflow: hidden; margin-top: 10px; }
                .bar { height: 100%; width: 0%; background: linear-gradient(90deg,#00c853,#64dd17); transition: width .35s ease; }
                .small { font-size: 12px; color: #9e9e9e; margin-top: 10px; }
                .meta { display: grid; grid-template-columns: 1fr; gap: 6px; margin-top: 14px; }
                .meta-item { background: #181818; border-radius: 8px; padding: 8px; font-size: 13px; }
                .graphs { margin-top: 14px; display: grid; gap: 10px; }
                .graphs img { width: 100%; border-radius: 8px; border: 1px solid #333; }
              </style>
            </head>
            <body>
              <div class="card">
                <h2>MeaSound – prùbìh mìøení</h2>
                <div id="statusWrap">
                  <div class="label">Stav</div>
                  <div id="status" class="value">-</div>
                </div>

                <div id="indexWrap">
                  <div class="label">Mìøení</div>
                  <div id="index" class="value">0 / 1</div>
                </div>

                <div id="stepsWrap">
                  <div class="label">Kroky</div>
                  <div id="steps" class="value">0 / 1 (0 %)</div>
                  <div class="progress"><div id="bar" class="bar"></div></div>
                </div>

                <div id="updated" class="small">Aktualizuji…</div>

                <div class="meta">
                  <div class="meta-item"><b>Vstupní režim:</b> <span id="inputMode">-</span></div>
                  <div class="meta-item"><b>Typ signálu:</b> <span id="signalType">-</span></div>
                  <div class="meta-item"><b>Vzorkování / bity:</b> <span id="format">-</span></div>
                  <div class="meta-item"><b>Input:</b> <span id="inputDevice">-</span></div>
                  <div class="meta-item"><b>Output:</b> <span id="outputDevice">-</span></div>
                </div>

                <div class="graphs">
                  <img id="polarImg" alt="Polar graf" />
                  <img id="fftImg" alt="FFT graf" />
                  <img id="specImg" alt="Spektrogram" />
                </div>
              </div>

              <script>
                const opt = {
                  showStatus: __SHOW_STATUS__,
                  showMeasurementIndex: __SHOW_INDEX__,
                  showStepProgress: __SHOW_STEPS__,
                  showTimestamp: __SHOW_TIMESTAMP__,
                  refreshMs: __REFRESH_MS__
                };

                document.getElementById('statusWrap').style.display = opt.showStatus ? '' : 'none';
                document.getElementById('indexWrap').style.display = opt.showMeasurementIndex ? '' : 'none';
                document.getElementById('stepsWrap').style.display = opt.showStepProgress ? '' : 'none';
                document.getElementById('updated').style.display = opt.showTimestamp ? '' : 'none';

                async function refresh() {
                  try {
                    const r = await fetch('/api/progress', { cache: 'no-store' });
                    const d = await r.json();
                    const pct = Math.max(0, Math.min(100, d.progressPercent || 0));
                    document.getElementById('status').textContent = d.status || '-';
                    document.getElementById('index').textContent = d.measurementIndex || '0 / 1';
                    document.getElementById('steps').textContent = `${d.completedSteps || 0} / ${d.totalSteps || 1} (${pct.toFixed(1)} %)`;
                    document.getElementById('bar').style.width = `${pct}%`;
                    const ts = d.updatedAtUtc ? new Date(d.updatedAtUtc) : new Date();
                    document.getElementById('updated').textContent = `Naposledy aktualizováno: ${ts.toLocaleTimeString()}`;

                    document.getElementById('inputMode').textContent = d.inputMode || '-';
                    document.getElementById('signalType').textContent = d.signalType || '-';
                    document.getElementById('format').textContent = `${d.sampleRate || '-'} / ${d.bitDepth || '-'}`;
                    document.getElementById('inputDevice').textContent = d.inputDevice || '-';
                    document.getElementById('outputDevice').textContent = d.outputDevice || '-';

                    const stamp = Date.now();
                    document.getElementById('polarImg').src = `/api/graph/polar.png?t=${stamp}`;
                    document.getElementById('fftImg').src = `/api/graph/fft.png?t=${stamp}`;
                    document.getElementById('specImg').src = `/api/graph/spectrogram.png?t=${stamp}`;
                  } catch {
                    document.getElementById('updated').textContent = 'Spojení nedostupné';
                  }
                }

                refresh();
                setInterval(refresh, opt.refreshMs);
              </script>
            </body>
            </html>
            """
            .Replace("__SHOW_STATUS__", showStatus ? "true" : "false", StringComparison.Ordinal)
            .Replace("__SHOW_INDEX__", showMeasurementIndex ? "true" : "false", StringComparison.Ordinal)
            .Replace("__SHOW_STEPS__", showStepProgress ? "true" : "false", StringComparison.Ordinal)
            .Replace("__SHOW_TIMESTAMP__", showTimestamp ? "true" : "false", StringComparison.Ordinal)
            .Replace("__REFRESH_MS__", refreshMs.ToString(), StringComparison.Ordinal);
        }

        public void Dispose() => Stop();

        private sealed class HttpRequestData
        {
            public string Method { get; init; } = "GET";
            public string Path { get; init; } = "/";
            public string Body { get; init; } = string.Empty;
            public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        internal readonly record struct ProgressSnapshot(
            string Status,
            string MeasurementIndex,
            int CompletedSteps,
            int TotalSteps,
            double ProgressPercent,
            DateTime UpdatedAtUtc,
            string InputMode = "-",
            string SignalType = "-",
            string SampleRate = "-",
            string BitDepth = "-",
            string InputDevice = "-",
            string OutputDevice = "-");
    }
}
