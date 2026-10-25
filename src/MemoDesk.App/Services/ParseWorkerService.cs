using MemoDesk.Core.Parsing;
using Microsoft.Extensions.Hosting;

namespace MemoDesk.App.Services;

/// <summary>后台整理宿主：跟随应用生命周期运行备忘优先的解析协调器。</summary>
public sealed class ParseWorkerService : BackgroundService
{
    private readonly MemoParseCoordinator _coordinator;

    public ParseWorkerService(MemoParseCoordinator coordinator)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _coordinator.RunAsync(stoppingToken);
}
