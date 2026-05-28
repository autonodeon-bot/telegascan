using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using WTelegram;

namespace TelegaScan.Services;

public static class ExportRetryHelper
{
    public static bool IsRetryable(Exception ex)
    {
        var msg = ex.GetBaseException().Message;
        if (msg.Contains("FLOOD_WAIT", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase)) return true;
        if (ex is IOException or SocketException or HttpRequestException or WTException) return true;
        if (msg.Contains("Connection shut down", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("payload length", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection was closed", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static async Task RunWithRetryAsync(
        Func<CancellationToken, Task> action,
        int maxAttempts,
        CancellationToken ct,
        Action<string>? log = null)
    {
        maxAttempts = Math.Clamp(maxAttempts, 1, 8);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await action(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex))
            {
                var delay = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));
                log?.Invoke($"Повтор {attempt}/{maxAttempts - 1} через {(int)delay.TotalSeconds} с: {ex.GetBaseException().Message}");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }
}
