using System.IO;
using TL;
using WTelegram;

namespace TelegaScan.Services;

/// <summary>Обрывы MTProto / TCP при длительной загрузке больших файлов.</summary>
public static class TelegramDownloadHelper
{
    public static bool IsTransientDownloadError(Exception ex)
    {
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            if (cur is WTException or IOException or System.Net.Sockets.SocketException
                or System.Net.Http.HttpRequestException)
                return true;

            var msg = cur.Message;
            if (msg.Contains("Connection shut down", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("payload length", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("connection was closed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("TIMEOUT", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return ExportRetryHelper.IsRetryable(ex);
    }

    public static int GetDownloadMaxAttempts(Document doc, bool isVideo)
    {
        var mb = doc.size / (1024.0 * 1024.0);
        if (mb >= 500) return 16;
        if (mb >= 100) return 12;
        if (mb >= 20) return 10;
        return isVideo ? 8 : 5;
    }

    public static double GetStallTimeoutMinutes(Document doc, bool isVideo)
    {
        var mb = doc.size / (1024.0 * 1024.0);
        if (mb >= 500) return 25;
        if (mb >= 100) return 18;
        if (mb >= 20) return 12;
        return isVideo ? 10 : 5;
    }

    public static int RetryDelaySeconds(int attempt, long partialBytes) =>
        partialBytes > 0
            ? Math.Min(45, 4 + attempt * 3)
            : Math.Min(30, 2 + attempt * 2);
}
