using System.Windows.Forms;

namespace TelegaScan.Services;

/// <summary>Уведомления Windows (balloon) без внешних пакетов.</summary>
public sealed class ToastNotificationService : IDisposable
{
    private NotifyIcon? _icon;

    public bool Enabled { get; set; } = true;

    /// <summary>Иконка в системном трее (видна, пока приложение запущено).</summary>
    public void ShowTrayIcon()
    {
        try
        {
            _icon ??= CreateNotifyIcon();
            _icon.Visible = true;
        }
        catch
        {
            /* tray недоступен */
        }
    }

    public void Show(string title, string message, ToolTipIcon icon = ToolTipIcon.Info)
    {
        if (!Enabled) return;
        try
        {
            _icon ??= CreateNotifyIcon();
            _icon.Visible = true;
            _icon.ShowBalloonTip(4000, title, message, icon);
        }
        catch
        {
            /* tray недоступен */
        }
    }

    public void Dispose()
    {
        if (_icon is null) return;
        _icon.Visible = false;
        _icon.Dispose();
        _icon = null;
    }

    private static NotifyIcon CreateNotifyIcon() => new()
    {
        Icon = (Icon)AppIconService.TrayIcon.Clone(),
        Text = "TelegaScan"
    };
}
