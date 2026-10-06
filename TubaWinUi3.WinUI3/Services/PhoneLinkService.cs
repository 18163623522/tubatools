using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TubaWinUi3.Services;

/// <summary>手机端聊天消息（局域网「连接手机」）。</summary>
public sealed class PhoneChatMessage
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    /// <summary>"phone" 或 "pc"。</summary>
    public string From { get; set; } = "";
    /// <summary>"text" / "image" / "file"。</summary>
    public string Type { get; set; } = "text";
    public string Text { get; set; } = "";
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public long Time { get; set; }
}

/// <summary>
/// 「连接手机」局域网控制服务：HttpListener + JSON。
/// 配对码（或二维码里携带的同一配对码）换取会话令牌，之后所有接口都要带 X-Token。
/// 接口：info / monitor / screenshot / exec(PowerShell) / winget / chat。
/// </summary>
public static class PhoneLinkService
{
    public const int DefaultPort = 18765;
    internal const int MaxUploadBytes = 512 * 1024 * 1024;

    private static HttpListener? _listener;
    private static CancellationTokenSource? _cts;
    private static readonly object _stateLock = new();
    private static readonly SemaphoreSlim _monitorLock = new(1, 1);
    private static readonly ConcurrentDictionary<string, PairedDevice> _tokens = new();
    private static readonly List<PhoneChatMessage> _messages = [];
    private static readonly Dictionary<string, string> _filePaths = [];
    private static readonly ConcurrentDictionary<string, WingetJob> _jobs = new();
    private static long _seq;
    private static int _failedPairs;
    private static DateTime _lockedUntil = DateTime.MinValue;

    public static bool IsRunning { get; private set; }
    public static int Port { get; private set; } = DefaultPort;
    public static string PairCode { get; private set; } = NewPairCode();
    public static event Action? StateChanged;
    public static event Action<PhoneChatMessage>? MessageAdded;

    public static string InboxDir => Path.Combine(ConfigManager.GetDataDir(), "PhoneLink", "Inbox");

    public static IReadOnlyList<string> PairedDeviceNames => _tokens.Values.Select(d => d.Name).Distinct().ToList();

    public static IReadOnlyList<PhoneChatMessage> GetMessages()
    {
        lock (_messages) return _messages.ToList();
    }

    // ───────────────────────── 地址 / 配对 ─────────────────────────

    /// <summary>所有可用于局域网连接的 IPv4 地址（排除回环、APIPA、未启用/虚拟网卡）。</summary>
    public static List<string> GetLocalIps()
    {
        var result = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    result.Add(s);
                }
            }
        }
        catch { }
        if (result.Count == 0) result.Add("127.0.0.1");
        return result;
    }

    /// <summary>二维码内容：tubalink://IP:端口?code=配对码&amp;name=电脑名。</summary>
    public static string BuildQrPayload(string ip) =>
        $"tubalink://{ip}:{Port}?code={PairCode}&name={Uri.EscapeDataString(Environment.MachineName)}";

    internal static string NewPairCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static void RefreshPairCode()
    {
        PairCode = NewPairCode();
        StateChanged?.Invoke();
    }

    /// <summary>校验配对码并签发令牌。连续输错 5 次锁定 30 秒，防止局域网内暴力枚举。</summary>
    internal static (string? Token, string? Error) TryPair(string? code, string? deviceName)
    {
        string token;
        lock (_stateLock)
        {
            if (DateTime.UtcNow < _lockedUntil) return (null, "尝试次数过多，请稍后再试");
            if (string.IsNullOrEmpty(code) || !FixedTimeEquals(code.Trim(), PairCode))
            {
                if (++_failedPairs >= 5)
                {
                    _failedPairs = 0;
                    _lockedUntil = DateTime.UtcNow.AddSeconds(30);
                    PairCode = NewPairCode();
                }
                return (null, "配对码错误");
            }
            _failedPairs = 0;
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            var name = string.IsNullOrWhiteSpace(deviceName) ? "手机" : deviceName.Trim();
            if (name.Length > 40) name = name[..40];
            _tokens[token] = new PairedDevice(name, DateTime.Now);
        }
        StateChanged?.Invoke();
        return (token, null);
    }

    internal static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    private static bool IsAuthorized(HttpListenerRequest req)
    {
        var token = req.Headers["X-Token"];
        if (string.IsNullOrEmpty(token)) return false;
        foreach (var kv in _tokens)
            if (FixedTimeEquals(kv.Key, token)) return true;
        return false;
    }

    public static void RevokeAll()
    {
        _tokens.Clear();
        PairCode = NewPairCode();
        StateChanged?.Invoke();
    }

    // ───────────────────────── 启停 ─────────────────────────

    public static void Start()
    {
        if (IsRunning) return;
        Directory.CreateDirectory(InboxDir);
        _cts = new CancellationTokenSource();
        var listener = new HttpListener();
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                listener.Prefixes.Clear();
                listener.Prefixes.Add($"http://+:{Port}/");
                listener.Start();
                break;
            }
            catch when (attempt < 9)
            {
                listener.Close();
                listener = new HttpListener();
                Port++;
            }
        }
        _listener = listener;
        IsRunning = true;
        PairCode = NewPairCode();
        StateChanged?.Invoke();
        var ct = _cts.Token;
        _ = Task.Run(() => ListenLoop(listener, ct));
    }

    public static void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        _cts?.Cancel();
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        _listener = null;
        _tokens.Clear();
        StateChanged?.Invoke();
    }

    private static async Task ListenLoop(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = Task.Run(() => HandleSafe(ctx, ct), ct);
        }
    }

    private static async Task HandleSafe(HttpListenerContext ctx, CancellationToken ct)
    {
        try { await Handle(ctx, ct).ConfigureAwait(false); }
        catch (Exception ex)
        {
            try { await WriteJson(ctx, 500, new { error = ex.Message }).ConfigureAwait(false); } catch { }
        }
        finally { try { ctx.Response.Close(); } catch { } }
    }

    // ───────────────────────── 路由 ─────────────────────────

    private static async Task Handle(HttpListenerContext ctx, CancellationToken ct)
    {
        var req = ctx.Request;
        var path = req.Url?.AbsolutePath.TrimEnd('/') ?? "";
        var method = req.HttpMethod;

        if (path == "/api/ping") { await WriteJson(ctx, 200, new { app = "TubaLink", host = Environment.MachineName }); return; }

        if (path == "/api/pair" && method == "POST")
        {
            var body = await ReadJson(req);
            var (token, error) = TryPair(Str(body, "code"), Str(body, "deviceName"));
            if (token is null) await WriteJson(ctx, 403, new { error });
            else await WriteJson(ctx, 200, new { token, host = Environment.MachineName });
            return;
        }

        if (!IsAuthorized(req)) { await WriteJson(ctx, 401, new { error = "未授权，请重新配对" }); return; }

        switch (path)
        {
            case "/api/info":
                await WriteJson(ctx, 200, new
                {
                    host = Environment.MachineName,
                    os = RuntimeInformation.OSDescription,
                    user = Environment.UserName,
                    arch = RuntimeInformation.OSArchitecture.ToString()
                });
                return;
            case "/api/monitor":
                await WriteJson(ctx, 200, await ReadMonitorAsync());
                return;
            case "/api/screenshot":
                {
                    var q = ParseQuery(req);
                    int.TryParse(q.GetValueOrDefault("width"), out var w);
                    var jpg = CaptureScreenJpeg(w is >= 320 and <= 3840 ? w : 1280);
                    ctx.Response.ContentType = "image/jpeg";
                    ctx.Response.ContentLength64 = jpg.Length;
                    await ctx.Response.OutputStream.WriteAsync(jpg, ct);
                    return;
                }
            case "/api/exec" when method == "POST":
                {
                    var body = await ReadJson(req);
                    var cmd = Str(body, "command");
                    if (string.IsNullOrWhiteSpace(cmd)) { await WriteJson(ctx, 400, new { error = "命令为空" }); return; }
                    var timeout = Math.Clamp(Int(body, "timeoutSec", 60), 1, 600);
                    var (code, output) = await RunPowerShellAsync(cmd, timeout, ct);
                    await WriteJson(ctx, 200, new { exitCode = code, output });
                    return;
                }
            case "/api/winget/search":
                {
                    var q = ParseQuery(req).GetValueOrDefault("q") ?? "";
                    var r = await WingetStoreService.SearchOnlineAsync(q, ct);
                    await WriteJson(ctx, 200, new
                    {
                        error = r.Error,
                        results = r.Results.Take(50).Select(x => new
                        {
                            id = x.PackageIdentifier,
                            name = x.PackageName,
                            version = x.LatestVersion,
                            publisher = x.Publisher
                        })
                    });
                    return;
                }
            case "/api/winget/install" when method == "POST":
                {
                    var id = Str(await ReadJson(req), "id");
                    if (!IsValidWingetId(id)) { await WriteJson(ctx, 400, new { error = "软件包 ID 不合法" }); return; }
                    await WriteJson(ctx, 200, new { jobId = StartWingetInstall(id!) });
                    return;
                }
            case "/api/winget/job":
                {
                    var jid = ParseQuery(req).GetValueOrDefault("id") ?? "";
                    if (!_jobs.TryGetValue(jid, out var job)) { await WriteJson(ctx, 404, new { error = "任务不存在" }); return; }
                    await WriteJson(ctx, 200, new { done = job.Done, exitCode = job.ExitCode, output = job.Snapshot() });
                    return;
                }
            case "/api/chat" when method == "GET":
                {
                    long.TryParse(ParseQuery(req).GetValueOrDefault("after"), out var after);
                    List<PhoneChatMessage> list;
                    lock (_messages) list = _messages.Where(m => m.Seq > after).ToList();
                    await WriteJson(ctx, 200, new { messages = list });
                    return;
                }
            case "/api/chat/text" when method == "POST":
                {
                    var text = Str(await ReadJson(req), "text");
                    if (string.IsNullOrWhiteSpace(text)) { await WriteJson(ctx, 400, new { error = "消息为空" }); return; }
                    var m = AddMessage("phone", "text", text!, "", 0, null);
                    await WriteJson(ctx, 200, m);
                    return;
                }
            case "/api/chat/upload" when method == "POST":
                {
                    var q = ParseQuery(req);
                    var name = SanitizeFileName(q.GetValueOrDefault("name"));
                    var isImage = q.GetValueOrDefault("kind") == "image";
                    if (req.ContentLength64 > MaxUploadBytes) { await WriteJson(ctx, 413, new { error = "文件过大" }); return; }
                    Directory.CreateDirectory(InboxDir);
                    var dest = UniquePath(InboxDir, name);
                    long size;
                    await using (var fs = File.Create(dest))
                    {
                        await req.InputStream.CopyToAsync(fs, ct);
                        size = fs.Length;
                    }
                    var m = AddMessage("phone", isImage ? "image" : "file", "", Path.GetFileName(dest), size, dest);
                    await WriteJson(ctx, 200, m);
                    return;
                }
        }

        if (path.StartsWith("/api/chat/file/", StringComparison.Ordinal) && method == "GET")
        {
            var id = path["/api/chat/file/".Length..];
            string? file;
            lock (_messages) _filePaths.TryGetValue(id, out file);
            if (file is null || !File.Exists(file)) { await WriteJson(ctx, 404, new { error = "文件不存在" }); return; }
            var fi = new FileInfo(file);
            ctx.Response.ContentType = "application/octet-stream";
            ctx.Response.ContentLength64 = fi.Length;
            ctx.Response.AddHeader("Content-Disposition", "attachment; filename*=UTF-8''" + Uri.EscapeDataString(fi.Name));
            await using var fs = File.OpenRead(file);
            await fs.CopyToAsync(ctx.Response.OutputStream, ct);
            return;
        }

        await WriteJson(ctx, 404, new { error = "未找到" });
    }

    // ───────────────────────── 聊天 ─────────────────────────

    public static PhoneChatMessage AddPcText(string text) => AddMessage("pc", "text", text, "", 0, null);

    public static PhoneChatMessage AddPcFile(string path)
    {
        var fi = new FileInfo(path);
        var ext = fi.Extension.ToLowerInvariant();
        var isImage = ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp";
        return AddMessage("pc", isImage ? "image" : "file", "", fi.Name, fi.Length, fi.FullName);
    }

    private static PhoneChatMessage AddMessage(string from, string type, string text, string fileName, long size, string? filePath)
    {
        var m = new PhoneChatMessage
        {
            Seq = Interlocked.Increment(ref _seq),
            Id = Guid.NewGuid().ToString("N"),
            From = from,
            Type = type,
            Text = text,
            FileName = fileName,
            Size = size,
            Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };
        lock (_messages)
        {
            _messages.Add(m);
            if (filePath is not null) _filePaths[m.Id] = filePath;
        }
        MessageAdded?.Invoke(m);
        return m;
    }

    internal static string SanitizeFileName(string? name)
    {
        var n = Path.GetFileName((name ?? "").Replace('\\', '/'));
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        n = n.Trim().TrimEnd('.');
        return string.IsNullOrEmpty(n) ? "file" : n.Length > 120 ? n[..120] : n;
    }

    internal static string UniquePath(string dir, string name)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return p;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 1; ; i++)
        {
            p = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }

    // ───────────────────────── 监控 / 截图 ─────────────────────────

    private static async Task<MonitorSample> ReadMonitorAsync()
    {
        await _monitorLock.WaitAsync();
        try
        {
            return await Task.Run(() =>
            {
                LiteMonitorService.Instance.EnsureInit();
                return LiteMonitorService.Instance.Read();
            });
        }
        finally { _monitorLock.Release(); }
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

    public static byte[] CaptureScreenJpeg(int maxWidth)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w <= 0 || h <= 0) { w = 1920; h = 1080; }
        using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(0, 0, 0, 0, new Size(w, h));

        Bitmap output = full;
        Bitmap? scaled = null;
        if (w > maxWidth)
        {
            var nh = (int)Math.Round(h * (maxWidth / (double)w));
            scaled = new Bitmap(maxWidth, nh, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(scaled);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(full, 0, 0, maxWidth, nh);
            output = scaled;
        }
        try
        {
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
            using var ep = new EncoderParameters(1);
            ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
            using var ms = new MemoryStream();
            output.Save(ms, codec, ep);
            return ms.ToArray();
        }
        finally { scaled?.Dispose(); }
    }

    // ───────────────────────── PowerShell / winget ─────────────────────────

    internal static ProcessStartInfo BuildPowerShellStartInfo(string command)
    {
        // 统一 UTF-8 输出；使用 EncodedCommand 避免任何引号/转义问题
        var script = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';" + command;
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-EncodedCommand");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        return psi;
    }

    private const int MaxOutputChars = 200_000;

    internal static async Task<(int ExitCode, string Output)> RunPowerShellAsync(string command, int timeoutSec, CancellationToken ct)
    {
        using var p = Process.Start(BuildPowerShellStartInfo(command));
        if (p is null) return (-1, "无法启动 powershell");
        var sb = new StringBuilder();
        void Append(string? s)
        {
            if (s is null) return;
            lock (sb) { if (sb.Length < MaxOutputChars) sb.AppendLine(s); }
        }
        p.OutputDataReceived += (_, e) => Append(e.Data);
        p.ErrorDataReceived += (_, e) => Append(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        try
        {
            await p.WaitForExitAsync(cts.Token);
            p.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            lock (sb) sb.AppendLine($"[超时 {timeoutSec} 秒，已终止]");
            return (-1, sb.ToString());
        }
        lock (sb) return (p.ExitCode, sb.ToString());
    }

    private static readonly Regex WingetIdRegex = new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]{0,127}$", RegexOptions.Compiled);

    internal static bool IsValidWingetId(string? id) => !string.IsNullOrEmpty(id) && WingetIdRegex.IsMatch(id);

    private sealed class WingetJob
    {
        private readonly StringBuilder _sb = new();
        public volatile bool Done;
        public int ExitCode = -1;
        public void Append(string? s) { if (s is null) return; lock (_sb) { if (_sb.Length < MaxOutputChars) _sb.AppendLine(s); } }
        public string Snapshot() { lock (_sb) return _sb.ToString(); }
    }

    private static string StartWingetInstall(string id)
    {
        var jobId = Guid.NewGuid().ToString("N");
        var job = new WingetJob();
        _jobs[jobId] = job;
        _ = Task.Run(async () =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "winget",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (var a in new[] { "install", "--id", id, "-e", "--source", "winget", "--silent",
                             "--accept-package-agreements", "--accept-source-agreements" })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p is null) { job.Append("无法启动 winget"); return; }
                p.OutputDataReceived += (_, e) => job.Append(e.Data);
                p.ErrorDataReceived += (_, e) => job.Append(e.Data);
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                await p.WaitForExitAsync();
                p.WaitForExit();
                job.ExitCode = p.ExitCode;
            }
            catch (Exception ex) { job.Append("安装失败：" + ex.Message); }
            finally { job.Done = true; }
        });
        return jobId;
    }

    // ───────────────────────── HTTP 辅助 ─────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, IncludeFields = true };

    private static async Task WriteJson(HttpListenerContext ctx, int status, object obj)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(obj, JsonOpts);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private static async Task<JsonElement> ReadJson(HttpListenerRequest req)
    {
        using var sr = new StreamReader(req.InputStream, Encoding.UTF8);
        var s = await sr.ReadToEndAsync();
        if (string.IsNullOrWhiteSpace(s)) return default;
        try { return JsonDocument.Parse(s).RootElement.Clone(); }
        catch { return default; }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name, int def) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static Dictionary<string, string> ParseQuery(HttpListenerRequest req)
    {
        var d = new Dictionary<string, string>();
        var q = req.Url?.Query ?? "";
        if (q.StartsWith('?')) q = q[1..];
        foreach (var part in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = part.IndexOf('=');
            var k = Uri.UnescapeDataString((i < 0 ? part : part[..i]).Replace('+', ' '));
            var v = i < 0 ? "" : Uri.UnescapeDataString(part[(i + 1)..].Replace('+', ' '));
            d[k] = v;
        }
        return d;
    }

    private sealed record PairedDevice(string Name, DateTime Since);
}
