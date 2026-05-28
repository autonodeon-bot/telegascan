using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TelegaScan.Services;

/// <summary>Загрузка иконки приложения для окна WPF и области уведомлений.</summary>
public static class AppIconService
{
    private static Icon? _trayIcon;
    private static BitmapImage? _windowIcon;

    public static BitmapImage WindowIcon =>
        _windowIcon ??= LoadWindowIcon();

    public static Icon TrayIcon =>
        _trayIcon ??= LoadTrayIcon();

    private static BitmapImage LoadWindowIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/AppIcon.ico", UriKind.Absolute);
        var img = new BitmapImage();
        img.BeginInit();
        img.UriSource = uri;
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.EndInit();
        img.Freeze();
        return img;
    }

    private static Icon LoadTrayIcon()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(path))
            return new Icon(path);

        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var extracted = Icon.ExtractAssociatedIcon(exe);
            if (extracted is not null)
                return extracted;
        }

        using var stream = Application.GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico"))?.Stream;
        if (stream is not null)
            return new Icon(stream);

        return SystemIcons.Application;
    }

    public static void ApplyTo(Window window)
    {
        window.Icon = WindowIcon;
    }
}
