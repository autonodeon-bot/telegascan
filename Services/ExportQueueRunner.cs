namespace TelegaScan.Services;

/// <summary>
/// Фоновый обработчик очереди: скачивает по одному, подхватывает новые элементы во время работы.
/// </summary>
public sealed class ExportQueueRunner
{
    private readonly Func<IReadOnlyList<ExportQueueItem>> _getQueue;
    private readonly Func<CancellationToken, Task<ExportQueueItem?>> _dequeuePendingAsync;
    private readonly Func<ExportQueueItem, CancellationToken, Task> _exportOneAsync;
    private readonly Action _onIdleStopping;
    private readonly Action<Exception?> _onFinished;
    private readonly ManualResetEventSlim _pauseGate = new(true);

    private CancellationTokenSource? _cts;
    private Task? _workerTask;

    public bool IsRunning { get; private set; }
    public bool IsPaused { get; private set; }

    public ExportQueueRunner(
        Func<IReadOnlyList<ExportQueueItem>> getQueue,
        Func<CancellationToken, Task<ExportQueueItem?>> dequeuePendingAsync,
        Func<ExportQueueItem, CancellationToken, Task> exportOneAsync,
        Action onIdleStopping,
        Action<Exception?> onFinished)
    {
        _getQueue = getQueue;
        _dequeuePendingAsync = dequeuePendingAsync;
        _exportOneAsync = exportOneAsync;
        _onIdleStopping = onIdleStopping;
        _onFinished = onFinished;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        _pauseGate.Set();
        _workerTask = Task.Run(() => WorkerLoopAsync(_cts.Token));
    }

    public void Stop() => _cts?.Cancel();

    public void Pause()
    {
        if (!IsRunning || IsPaused) return;
        IsPaused = true;
        _pauseGate.Reset();
    }

    public void Resume()
    {
        if (!IsRunning || !IsPaused) return;
        IsPaused = false;
        _pauseGate.Set();
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        Exception? fault = null;
        var idleRounds = 0;
        const int maxIdleRounds = 150;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Run(() => _pauseGate.Wait(ct), ct).ConfigureAwait(false);

                var next = await _dequeuePendingAsync(ct).ConfigureAwait(false);
                if (next is null)
                {
                    idleRounds++;
                    if (idleRounds >= maxIdleRounds)
                    {
                        _onIdleStopping();
                        break;
                    }
                    await Task.Delay(200, ct).ConfigureAwait(false);
                    continue;
                }

                idleRounds = 0;
                await _exportOneAsync(next, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* остановка */ }
        catch (Exception ex) { fault = ex; }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            _pauseGate.Set();
            _onFinished(fault);
        }
    }
}
