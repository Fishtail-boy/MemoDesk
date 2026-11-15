using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using MemoDesk.Infrastructure.Settings;

namespace MemoDesk.App.Services;

internal sealed record CodexQuotaWindow(string Label, double Remaining, DateTimeOffset? ResetsAt);
internal sealed record CodexQuota(IReadOnlyList<CodexQuotaWindow> Windows, string Status);

internal sealed class IslandQuotaService
{
    private static readonly HttpClient DeepSeek = new(new HttpClientHandler { AllowAutoRedirect = false });

    internal static async Task<CodexQuota> ReadCodexAsync(CancellationToken cancellationToken)
    {
        var executable = FindCodex();
        if (executable is null) return new([], "需要安装并登录 Codex CLI，或让 codex.exe 可通过 PATH 找到。");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(12));
        var token = timeout.Token;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen");
        start.ArgumentList.Add("stdio://");
        try
        {
            using var process = Process.Start(start);
            if (process is null) return new([], "无法启动 Codex。");
            var drainErrors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            try
            {
                await SendAsync(process, """
                    {"id":1,"method":"initialize","params":{"clientInfo":{"name":"memodesk","title":"MemoDesk","version":"1.0"}}}
                    """, token);
                if ((await ReceiveAsync(process, 1, token)).ValueKind == JsonValueKind.Undefined)
                    return new([], "Codex 客户端初始化失败。");
                await SendAsync(process, """{"method":"initialized"}""", token);
                await SendAsync(process, """{"id":2,"method":"account/rateLimits/read"}""", token);
                return ParseCodex(await ReceiveAsync(process, 2, token));
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                try { await drainErrors; }
                catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new([], "Codex 额度查询超时。");
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException or JsonException)
        {
            return new([], "无法读取 Codex 额度。");
        }
    }

    private static string? FindCodex()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        string[] candidates =
        [
            Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"),
            .. pathDirectories.Select(directory => directory.Trim().Trim('"'))
                .Where(Path.IsPathFullyQualified).Select(directory => Path.Combine(directory, "codex.exe")),
            Path.Combine(local, "Microsoft", "WindowsApps", "codex.exe"),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static async Task SendAsync(Process process, string line, CancellationToken token)
    {
        await process.StandardInput.WriteLineAsync(line.AsMemory(), token);
        await process.StandardInput.FlushAsync(token);
    }

    private static async Task<JsonElement> ReceiveAsync(Process process, int id, CancellationToken token)
    {
        while (await process.StandardOutput.ReadLineAsync(token) is { } line)
        {
            using var frame = JsonDocument.Parse(line);
            if (frame.RootElement.TryGetProperty("id", out var value)
                && value.ValueKind == JsonValueKind.Number && value.GetInt32() == id)
            {
                return frame.RootElement.TryGetProperty("result", out var result) ? result.Clone() : default;
            }
        }
        return default;
    }

    internal static CodexQuota ParseCodex(JsonElement result)
    {
        var windows = new List<CodexQuotaWindow>();
        if (result.ValueKind != JsonValueKind.Object)
            return new(windows, "额度暂不可用，请确认 Codex 已登录。");

        if (result.TryGetProperty("rateLimits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "primary", "secondary" })
            {
                if (!limits.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object
                    || !window.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number
                    || !used.TryGetDouble(out var percent) || !double.IsFinite(percent))
                    continue;

                var label = window.TryGetProperty("windowDurationMins", out var duration)
                    && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes)
                    ? minutes switch { 300 => "5 小时", 10080 => "每周", _ => $"{minutes} 分钟" }
                    : key == "primary" ? "主要额度" : "次要额度";
                DateTimeOffset? resetsAt = null;
                if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind == JsonValueKind.Number
                    && reset.TryGetInt64(out var seconds) && seconds is > 0 and <= 253402300799)
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                windows.Add(new(label, Math.Clamp(100 - percent, 0, 100), resetsAt));
            }
        }
        return new(windows, windows.Count == 0 ? "当前账号未返回可显示的用量窗口。" : "");
    }

    internal static async Task<string> ReadDeepSeekAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(settings.ApiBaseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != "https" || !endpoint.IsDefaultPort || endpoint.Host != "api.deepseek.com"
            || endpoint.UserInfo.Length != 0 || endpoint.AbsolutePath.TrimEnd('/') is not ("" or "/v1"))
            return "余额查询需要使用 DeepSeek 官方接口。";

        var key = settings.ApiKey;
        if (string.IsNullOrWhiteSpace(key)) return "请在设置中填写 DeepSeek API Key。";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.deepseek.com/user/balance");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            using var response = await DeepSeek.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return $"余额查询失败（HTTP {(int)response.StatusCode}）。";

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var data = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            return ParseDeepSeek(data.RootElement);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "DeepSeek 余额查询超时。";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or FormatException)
        {
            return "无法读取 DeepSeek 余额。";
        }
    }

    internal static string ParseDeepSeek(JsonElement result)
    {
        if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("balance_infos", out var balances)
            || balances.ValueKind != JsonValueKind.Array)
            return "DeepSeek 未返回余额。";

        var values = new List<string>();
        foreach (var balance in balances.EnumerateArray())
        {
            if (balance.ValueKind == JsonValueKind.Object
                && balance.TryGetProperty("currency", out var currency) && currency.ValueKind == JsonValueKind.String
                && balance.TryGetProperty("total_balance", out var amount) && amount.ValueKind == JsonValueKind.String)
                values.Add($"{currency.GetString()} {amount.GetString()}");
        }
        return values.Count == 0 ? "DeepSeek 未返回余额。" : string.Join("　·　", values);
    }
}
