using System.IO;

namespace TelegaScan.Services;

public static class DiskSpaceChecker
{
    public static (bool Ok, string Message) CheckPath(string folderPath, long requiredBytes = 512L * 1024 * 1024)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
            return (false, "Папка экспорта не указана.");

        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(folderPath));
            if (string.IsNullOrEmpty(root))
                return (false, "Не удалось определить диск для папки экспорта.");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return (false, $"Диск {root} недоступен.");

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                var free = FormatBytes(drive.AvailableFreeSpace);
                var need = FormatBytes(requiredBytes);
                return (false, $"Мало места на диске {root}: свободно {free}, рекомендуется минимум {need}.");
            }

            return (true, $"Свободно на диске: {FormatBytes(drive.AvailableFreeSpace)}");
        }
        catch (Exception ex)
        {
            return (false, "Проверка диска: " + ex.Message);
        }
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double x = bytes / 1024.0;
        if (x < 1024) return $"{x:0.#} КБ";
        x /= 1024;
        if (x < 1024) return $"{x:0.#} МБ";
        return $"{x / 1024:0.#} ГБ";
    }
}
