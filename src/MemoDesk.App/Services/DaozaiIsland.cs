using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using MemoDesk.Core.Capturing;
using MemoDesk.Core.Domain;
using MemoDesk.Core.Items;
using MemoDesk.Core.Reminders;
using MemoDesk.Core.Time;
using MemoDesk.Infrastructure;
using MemoDesk.Infrastructure.Queries;
using MemoDesk.Infrastructure.Settings;
using Microsoft.Extensions.Logging;

namespace MemoDesk.App.Services;

/// <summary>Owns the native island process and its private request/response stream.</summary>
internal sealed class DaozaiIsland : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AppPaths _paths;
    private readonly AppSettings _settings;
    private readonly ItemQueryService _query;
    private readonly ItemService _items;
    private readonly IReminderRepository _reminders;
    private readonly MemoCaptureService _capture;
    private readonly TimeProvider _clock;
    private readonly ILogger<DaozaiIsland> _logger;
    private readonly Dispatcher _dispatcher;
    private Process? _process;
    private CancellationTokenSource? _stop;

    public DaozaiIsland(AppPaths paths, AppSettings settings, ItemQueryService query, ItemService items,
        IReminderRepository reminders, MemoCaptureService capture, TimeProvider clock, ILogger<DaozaiIsland> logger)
    {
        _paths = paths;
        _settings = settings;
        _query = query;
        _items = items;
        _reminders = reminders;
        _capture = capture;
        _clock = clock;
        _logger = logger;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    public bool IsRunning => _process is { HasExited: false };
    private static string Executable => Path.Combine(AppContext.BaseDirectory, "Island", "daozai.exe");
    public static bool IsAvailable => File.Exists(Executable);
    public event Action? Exited;
    public event Action<Capture>? Captured;
    public event Action<string>? OpenPage;
    public event Action<int, string, int, int, int, int, bool>? ShowWorkbench;
    public event Action? CloseWorkbench;

    public bool Start()
    {
        if (IsRunning) return true;
        Stop();
        if (!IsAvailable) return false;

        var start = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = Path.GetDirectoryName(Executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add("--memodesk");
        start.Environment["DAOZAI_DATA_DIR"] = Path.Combine(_paths.RootDirectory, "island");
        start.Environment["MEMODESK_PARENT_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        try
        {
            var process = Process.Start(start);
            if (process is null) return false;
            _process = process;
            _stop = new CancellationTokenSource();
            _ = process.StandardError.BaseStream.CopyToAsync(Stream.Null, _stop.Token);
            var token = _stop.Token;
            _ = Task.Run(() => ServeAsync(process, token));
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            _logger.LogWarning(ex, "Daozai 岛启动失败。");
            return false;
        }
    }

    private async Task ServeAsync(Process process, CancellationToken token)
    {
        IslandRow[]? previousRows = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) is { } line)
            {
                object response;
                try
                {
                    var request = JsonSerializer.Deserialize<IslandRequest>(line, Json);
                    response = request switch
                    {
                        null => new { error = "请求无效。" },
                        { Op: "list" } => Snapshot(request, ref previousRows),
                        _ => Handle(request),
                    };
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning("Daozai 请求处理失败（{ErrorType}）。", ex.GetType().Name);
                    response = new { error = "备忘操作失败，请稍后重试。" };
                }

                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), token)
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            }
            if (!token.IsCancellationRequested) await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException)
        {
            if (!token.IsCancellationRequested) _logger.LogWarning("Daozai 岛连接已关闭。");
        }
        finally
        {
            if (!token.IsCancellationRequested && !_dispatcher.HasShutdownStarted)
                _ = _dispatcher.InvokeAsync(() =>
                {
                    if (!ReferenceEquals(_process, process)) return;
                    Stop();
                    Exited?.Invoke();
                });
        }
    }

    private object Handle(IslandRequest request)
    {
        if (request.Op == "host")
        {
            var processId = _process?.Id ?? 0;
            _ = _dispatcher.InvokeAsync(() => ShowWorkbench?.Invoke(processId, request.Page ?? "", request.X, request.Y, request.Width, request.Height, request.Dark));
            return new { };
        }
        if (request.Op == "hide-host")
        {
            _ = _dispatcher.InvokeAsync(() => CloseWorkbench?.Invoke());
            return new { };
        }
        if (request.Op is "workbench" or "vault" or "settings" or "restart")
        {
            _ = _dispatcher.InvokeAsync(() => OpenPage?.Invoke(request.Op));
            return new { };
        }

        if (request.Op == "add")
        {
            var text = request.Text?.Trim();
            if (text is not { Length: > 0 and <= 16000 } || request.Id is not { } id || id == Guid.Empty)
                return new { error = "请输入有效的备忘内容。" };
            var saved = _capture.Capture(text, _settings.TimeZoneId, id, id);
            if (!saved.AlreadyExisted)
                _ = _dispatcher.InvokeAsync(() => Captured?.Invoke(saved.Capture));
            return new { item = ToRow(saved.Memo, _settings.TimeZone) };
        }

        var item = request.Id is { } itemId ? _items.Get(itemId) : null;
        if (item is null || item.Status is ItemStatus.Archived or ItemStatus.Deleted)
            return new { error = "备忘已归档或删除。" };
        if (request.Version != item.RowVersion)
            return new { error = "备忘已更新，请刷新后重试。" };

        if (request.Op is "complete" or "reopen")
            return (request.Op == "complete" ? _items.Complete(item.Id) : _items.Reopen(item.Id))
                ? new { } : new { error = "备忘状态已变化。" };
        if (request.Op == "delete")
            return _items.Delete(item.Id) ? new { } : new { error = "无法删除备忘。" };
        if (request.Op != "edit") return new { error = "未知操作。" };
        return Edit(request, item);
    }

    private object Edit(IslandRequest request, Item item)
    {
        var text = request.Text?.Trim() ?? item.Content;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 16000) return new { error = "请输入有效的备忘内容。" };

        var kind = ReminderEditKind.Unchanged;
        DateTimeOffset? target = null;
        var plan = _reminders.GetActiveForItem(item.Id);
        if (request.SetDue)
        {
            kind = request.Due is null ? ReminderEditKind.Clear : ReminderEditKind.Set;
            if (request.Due is { } due)
            {
                if (!DateTime.TryParseExact(due, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var local) || TimeZoneInfo.Local.IsInvalidTime(local)
                    || TimeZoneInfo.Local.IsAmbiguousTime(local))
                    return new { error = "所选提醒时间无效。" };
                target = new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUniversalTime();
                if (plan is not null && (plan.ReminderAtUtc - target.Value).Duration() < TimeSpan.FromMinutes(1))
                    kind = ReminderEditKind.Unchanged;
            }
            if (kind != ReminderEditKind.Unchanged && (request.PlanId != plan?.Id || request.PlanVersion != plan?.PlanVersion))
                return new { error = "提醒已更新，请刷新后重试。" };
        }

        var result = _items.UpdateWithReminder(item with { Content = text },
            new ReminderEditIntent(kind, target, plan?.Id, plan?.PlanVersion, plan is not null));
        return result.IsSuccess ? new { } : new { error = result.Error ?? "备忘已更新，请刷新后重试。" };
    }

    private object Snapshot(IslandRequest request, ref IslandRow[]? previousRows)
    {
        var zone = _settings.TimeZone;
        var now = _clock.GetUtcNow();
        var open = _query.GetUnifiedMemos(MemoFilter.Unprocessed);
        var plans = _reminders.GetActiveBetween(DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .ToDictionary(row => row.ItemId);
        var rows = open.Select(item => ToRow(item, zone, plans.GetValueOrDefault(item.Id))).ToArray();
        var updated = now.ToUnixTimeSeconds();
        var notices = request.Since <= 0 ? [] : rows.Where(row => row.DueAt > request.Since && row.DueAt <= updated).ToArray();
        IslandRow[]? items = null;
        if (previousRows is null || !previousRows.AsSpan().SequenceEqual(rows))
        {
            previousRows = items = rows;
        }
        return new { items, notices, updated };
    }

    private IslandRow ToRow(Item item, TimeZoneInfo zone) => ToRow(item, zone, _reminders.GetActiveForItem(item.Id));

    private static IslandRow ToRow(Item item, TimeZoneInfo zone, ReminderPlan? plan) =>
        new IslandRow(item.Id, item.RowVersion, item.Content,
            item.CompletedAtUtc is { } done ? BusinessTime.FormatLocal(done, zone, "yyyy-MM-dd") : null,
            plan?.ReminderAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            plan?.ReminderAtUtc.ToUnixTimeSeconds(), plan?.Id, plan?.PlanVersion);

    public void Stop()
    {
        var process = _process;
        _process = null;
        _stop?.Cancel();
        _stop?.Dispose();
        _stop = null;
        if (process is null) return;
        try
        {
            process.StandardInput.Close();
            if (!process.WaitForExit(1200)) process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        finally { process.Dispose(); }
    }

    public void Dispose() => Stop();

    private sealed record IslandRequest(string Op, Guid? Id, int? Version, string? Text, bool SetDue, string? Due,
        Guid? PlanId, int? PlanVersion, long Since, string? Page, int X, int Y, int Width, int Height, bool Dark);
    private sealed record IslandRow(Guid Id, int Version, string Text, string? DoneOn, string? Due, long? DueAt,
        Guid? PlanId, int? PlanVersion);
}
