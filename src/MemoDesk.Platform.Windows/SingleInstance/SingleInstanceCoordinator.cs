using System.IO;
using System.IO.Pipes;
using System.Text;

namespace MemoDesk.Platform.Windows.SingleInstance;

/// <summary>
/// 单实例协调（设计 6.3）：命名互斥量判定主实例，命名管道把后续启动的命令转交给主实例。
/// 互斥量仅作为"是否存在主实例"的标记使用，不持有线程所有权，避免跨线程释放问题。
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\MemoDesk.SingleInstance";
    private const string PipeName = "MemoDesk.SingleInstance.Pipe";

    private Mutex? _mutex;
    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;

    public bool IsPrimary { get; private set; }

    /// <summary>尝试成为主实例。返回 false 时表示已有实例在运行。</summary>
    public bool TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            throw new InvalidOperationException("TryAcquirePrimary 只能调用一次。");
        }

        var mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            IsPrimary = false;
            return false;
        }

        _mutex = mutex;
        IsPrimary = true;
        return true;
    }

    /// <summary>开始监听后续实例转发的命令。命令回调在主实例的线程池线程上执行。</summary>
    public void StartListening(Func<string, Task> onCommand)
    {
        ArgumentNullException.ThrowIfNull(onCommand);

        if (!IsPrimary)
        {
            throw new InvalidOperationException("只有主实例可以监听命令。");
        }

        if (_listenTask is not null)
        {
            throw new InvalidOperationException("监听已启动。");
        }

        _cancellation = new CancellationTokenSource();
        _listenTask = Task.Run(() => ListenLoopAsync(onCommand, _cancellation.Token));
    }

    /// <summary>作为非主实例，把命令转发给主实例。</summary>
    public static async Task<bool> TrySendCommandAsync(string command, TimeSpan timeout)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeName, PipeDirection.Out, PipeOptions.Asynchronous);
                await client.ConnectAsync(250).ConfigureAwait(false);

                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(command).ConfigureAwait(false);
                return true;
            }
            catch (TimeoutException)
            {
                await Task.Delay(100).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 主实例的管道服务暂时忙（单连接循环），稍后重试。
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        return false;
    }

    private static async Task ListenLoopAsync(Func<string, Task> onCommand, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var command = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    await onCommand(command).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // 客户端提前断开等瞬时错误：重建管道继续监听。
                continue;
            }
        }
    }

    public void Dispose()
    {
        if (_cancellation is not null)
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            _cancellation = null;
        }

        if (_listenTask is not null)
        {
            try
            {
                _listenTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // 关闭期间的监听异常无需上抛。
            }

            _listenTask = null;
        }

        _mutex?.Dispose();
        _mutex = null;
        IsPrimary = false;
    }
}
