using System.Diagnostics;
using TL;

namespace TelegaScan.Services;

/// <summary>
/// Повтор запросов при FLOOD_WAIT и мягкие задержки, чтобы снизить риск лимитов.
/// </summary>
public static class RateLimitedTelegram
{
    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        TimeSpan minDelayBetweenCalls,
        CancellationToken cancellationToken = default)
    {
        await Task.Delay(minDelayBetweenCalls, cancellationToken).ConfigureAwait(false);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                var wait = ParseFloodWaitSeconds(ex.Message);
                if (wait.HasValue)
                {
                    Debug.WriteLine($"FLOOD_WAIT: ждём {wait.Value} с");
                    await Task.Delay(TimeSpan.FromSeconds(wait.Value + 1), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw;
            }
        }
    }

    public static async Task ExecuteAsync(
        Func<Task> action,
        TimeSpan minDelayBetweenCalls,
        CancellationToken cancellationToken = default)
    {
        await ExecuteAsync(async () =>
        {
            await action().ConfigureAwait(false);
            return 0;
        }, minDelayBetweenCalls, cancellationToken).ConfigureAwait(false);
    }

    private static int? ParseFloodWaitSeconds(string message)
    {
        if (string.IsNullOrEmpty(message))
            return null;
        const string prefix = "FLOOD_WAIT_";
        var idx = message.IndexOf(prefix, StringComparison.Ordinal);
        if (idx < 0)
            return null;
        var start = idx + prefix.Length;
        var end = start;
        while (end < message.Length && char.IsDigit(message[end]))
            end++;
        if (end == start)
            return null;
        return int.TryParse(message.AsSpan(start, end - start), out var sec) ? Math.Clamp(sec, 1, 3600) : null;
    }
}
