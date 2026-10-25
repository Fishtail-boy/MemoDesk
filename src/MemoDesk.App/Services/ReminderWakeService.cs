using MemoDesk.Core.Reminders;
using Microsoft.Extensions.Logging;

namespace MemoDesk.App.Services;

/// <summary>后台一次性唤醒；新增/修改立即重算最近目标，UI 线程仅承担实际呈现。</summary>
public sealed class ReminderWakeService : IDisposable
{
    private readonly IReminderRepository _repository;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReminderWakeService> _logger;
    private readonly ReminderDeadlineTracker _deadlines = new();
    private readonly ITimer _timer;
    private readonly object _gate = new();
    private int _scanning;
    private int _requested;
    private bool _disposed;
    public ReminderWakeService(IReminderRepository repository, TimeProvider clock, ILogger<ReminderWakeService> logger)
    {
        _repository = repository; _clock = clock; _logger = logger;
        _timer = clock.CreateTimer(_ => Scan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }
    public event Action<IReadOnlyList<ReminderPlan>>? Due;
    public void Wake()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Interlocked.Exchange(ref _requested, 1);
            _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }
    private void Scan()
    {
        if (_disposed || Interlocked.Exchange(ref _scanning, 1) != 0) return;
        var next = TimeSpan.FromSeconds(30);
        Interlocked.Exchange(ref _requested, 0);
        try
        {
            var plans = _repository.GetWakeablePlans();
            _deadlines.Retain(plans);
            var due = new List<ReminderPlan>();
            foreach (var plan in plans)
            {
                var remaining = _deadlines.Remaining(plan, _clock);
                if (remaining <= TimeSpan.Zero) due.Add(plan);
                if (remaining < next) next = remaining;
            }
            if (due.Count > 0) Due?.Invoke(due);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "提醒精细计时失败，将再次对账"); }
        finally
        {
            lock (_gate)
            {
                Interlocked.Exchange(ref _scanning, 0);
                if (!_disposed)
                {
                    if (Interlocked.Exchange(ref _requested, 0) != 0) next = TimeSpan.Zero;
                    else if (next < TimeSpan.FromMilliseconds(200)) next = TimeSpan.FromMilliseconds(200);
                    _timer.Change(next, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }
    public void Dispose() { lock (_gate) { _disposed = true; _timer.Dispose(); } }
}
