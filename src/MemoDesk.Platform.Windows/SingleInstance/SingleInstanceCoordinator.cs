using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Diagnostics;
using System.Security.Principal;

namespace MemoDesk.Platform.Windows.SingleInstance;

/// <summary>
/// 单实例协调（设计 6.3）：命名互斥量判定主实例，命名管道把后续启动的命令转交给主实例。
/// 互斥量仅作为"是否存在主实例"的标记使用，不持有线程所有权，避免跨线程释放问题。
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private static readonly string Scope = WindowsIdentity.GetCurrent().User!.Value + "." + Process.GetCurrentProcess().SessionId;
    private readonly string _mutexName;
    private readonly string _pipeName;
    private readonly string _legacyMutexName;
    private readonly string _legacyPipeName;
    private const int MaxCommandLength = 4096;

    private Mutex? _mutex;
    private Mutex? _legacyMutex;
    private CancellationTokenSource? _cancellation;
    private Task? _listenTask;

    public bool IsPrimary { get; private set; }
    public bool LegacyInstanceDetected { get; private set; }

    public SingleInstanceCoordinator(Guid? isolationId = null)
    {
        var suffix = Scope + (isolationId is { } id ? "." + id.ToString("N") : "");
        _mutexName = @"Local\MemoDesk.SingleInstance." + suffix;
        _pipeName = PipeNameFor(isolationId);
        var isolationSuffix = isolationId is { } isolated ? "." + isolated.ToString("N") : "";
        _legacyMutexName = @"Local\MemoDesk.SingleInstance" + isolationSuffix;
        _legacyPipeName = "MemoDesk.SingleInstance.Pipe" + isolationSuffix;
    }

    private static string PipeNameFor(Guid? isolationId) => "MemoDesk.SingleInstance.Pipe." + Scope
        + (isolationId is { } id ? "." + id.ToString("N") : "");

    /// <summary>尝试成为主实例。返回 false 时表示已有实例在运行。</summary>
    public bool TryAcquirePrimary()
    {
        if (_mutex is not null)
        {
            throw new InvalidOperationException("TryAcquirePrimary 只能调用一次。");
        }

        var mutex = new Mutex(initiallyOwned: false, _mutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            IsPrimary = false;
            return false;
        }

        try
        {
            // Atomically claim the old marker too. Either startup order (and simultaneous starts)
            // must leave only one process allowed to open the databases.
            var legacy = new Mutex(initiallyOwned: false, _legacyMutexName, out var legacyCreated);
            if (!legacyCreated)
            {
                legacy.Dispose();
                mutex.Dispose();
                LegacyInstanceDetected = true;
                return false;
            }
            _legacyMutex = legacy;
        }
        catch
        {
            mutex.Dispose();
            throw;
        }

        LegacyInstanceDetected = false;
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
        var token = _cancellation.Token;
        _listenTask = Task.WhenAll(
            Task.Run(() => ListenLoopAsync(_pipeName, onCommand, token)),
            Task.Run(() => ListenLoopAsync(_legacyPipeName, onCommand, token)));
    }

    /// <summary>作为非主实例，把命令转发给主实例。</summary>
    public static async Task<bool> TrySendCommandAsync(string command, TimeSpan timeout, Guid? isolationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        if (command.Length > MaxCommandLength) return false;

        using var deadline = new CancellationTokenSource(timeout);
        while (!deadline.IsCancellationRequested)
        {
            try
            {
                using var client = new NamedPipeClientStream(
                    ".", PipeNameFor(isolationId), PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await client.ConnectAsync(250, deadline.Token).ConfigureAwait(false);

                using var writer = new StreamWriter(client, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true,
                };
                await writer.WriteLineAsync(command.AsMemory(), deadline.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) { return false; }
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

    private static async Task ListenLoopAsync(string pipeName, Func<string, Task> onCommand, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                using var reader = new StreamReader(server, Encoding.UTF8);
                using var readDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readDeadline.CancelAfter(TimeSpan.FromSeconds(3));
                var command = await ReadCommandAsync(reader, readDeadline.Token).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    await onCommand(command).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) { /* Idle client: serve the next connection. */ }
            catch (IOException)
            {
                // The legacy name is machine-wide and may be busy in another session.
                // Back off rather than spinning when recreating a pipe fails.
                try { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            catch (Exception) { Trace.TraceError("MemoDesk 单实例命令处理失败，继续监听。"); }
        }
    }

    private static async Task<string?> ReadCommandAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= MaxCommandLength) return null;
            result.Append(character[0]);
        }
        return result.ToString();
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
        _legacyMutex?.Dispose();
        _legacyMutex = null;
        IsPrimary = false;
    }
}
