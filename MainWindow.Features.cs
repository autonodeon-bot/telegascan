using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using System.Windows.Threading;
using TelegaScan.Helpers;
using TelegaScan.Services;

namespace TelegaScan;

public partial class MainWindow
{
    private readonly AccountStore _accountStore = AccountStore.Load();
    private readonly FileLogService _fileLog = new();
    private readonly ToastNotificationService _toast = new();
    private DispatcherTimer _schedulerTimer = null!;
    private readonly HashSet<string> _queueFolderNames = new(StringComparer.OrdinalIgnoreCase);

    private int? _lastMarkedVisibleIndex;
    private ExportQueueItem? _dragQueueItem;
    private System.Windows.Point _dragStartPoint;

    private void InitFeatureServices()
    {
        _fileLog.Enabled = _settings.EnableFileLog;
        _toast.Enabled = _settings.EnableToast;
        _fileLog.SetLogFile(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegaScan",
            "TelegaScan.log"));

        ChkEnableToast.IsChecked = _settings.EnableToast;
        ChkEnableFileLog.IsChecked = _settings.EnableFileLog;
        ChkScheduler.IsChecked = _settings.SchedulerEnabled;
        TxtSchedulerTime.Text = _settings.SchedulerTime;
        CmbRetryCount.SelectedIndex = _settings.AutoRetryCount switch { 1 => 0, 2 => 1, >= 5 => 3, _ => 2 };

        RefreshAccountsCombo();
        RefreshProfileCombo();

        _schedulerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _schedulerTimer.Tick += SchedulerTimer_OnTick;
        _schedulerTimer.Start();
    }

    private void RefreshAccountsCombo()
    {
        CmbAccounts.ItemsSource = null;
        CmbAccounts.ItemsSource = _accountStore.Accounts;
        CmbAccounts.DisplayMemberPath = nameof(TelegramAccount.DisplayName);
        var active = _accountStore.GetActive();
        if (active != null) CmbAccounts.SelectedItem = active;
    }

    private void RefreshProfileCombo()
    {
        var names = QueueProfileService.ListProfileNames().ToList();
        CmbQueueProfiles.ItemsSource = names;
        if (names.Count > 0) CmbQueueProfiles.SelectedIndex = 0;
    }

    private string GetSessionPath()
    {
        var acc = _accountStore.GetActive();
        if (acc != null) return AccountStore.SessionPathFor(acc);
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegaScan",
            "WTelegram.session");
    }

    private void CmbAccounts_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAccounts.SelectedItem is TelegramAccount acc)
        {
            _accountStore.ActiveAccountId = acc.Id;
            _accountStore.Save();
            TxtPhone.Text = acc.Phone;
            if (IsLoaded)
                SwitchQueueForAccount();
        }
    }

    private async void BtnAddAccount_Click(object sender, RoutedEventArgs e)
    {
        var phone = TxtPhone.Text.Trim();
        if (string.IsNullOrEmpty(phone))
        {
            MessageBox.Show(
                "Введите номер телефона с «+» в поле выше, затем нажмите «+ Аккаунт».\n\nПосле этого нажмите «Подключиться» для входа.",
                "Новый аккаунт",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            TxtPhone.Focus();
            return;
        }

        var existing = _accountStore.Accounts.FirstOrDefault(a => a.Phone == phone);
        if (existing != null)
        {
            CmbAccounts.SelectedItem = existing;
            AppendLog($"Аккаунт {phone} уже в списке.");
            return;
        }

        if (_client != null)
        {
            await _client.DisposeAsync();
            _client = null;
            _chatItems.Clear();
            BtnDisconnect.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Не подключено";
        }

        var acc = new TelegramAccount { Phone = phone, DisplayName = phone };
        _accountStore.Accounts.Add(acc);
        _accountStore.ActiveAccountId = acc.Id;
        _accountStore.Save();
        RefreshAccountsCombo();
        TxtPhone.Text = phone;
        AppendLog($"Добавлен аккаунт {phone}. Нажмите «Подключиться».");
    }

    private void DeveloperLink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось открыть ссылку: " + ex.Message);
        }
        e.Handled = true;
    }

    private void CmbTypeFilter_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_chatItems).Refresh();

    private void TypeFilter_Changed(object sender, RoutedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_chatItems).Refresh();

    private bool MatchesTypeFilter(DialogListItem d)
    {
        if (ChkArchivedOnly.IsChecked == true && !d.IsArchived) return false;
        if (CmbTypeFilter.SelectedItem is not ComboBoxItem item) return true;
        var tag = item.Tag?.ToString() ?? "all";
        return tag switch
        {
            "personal" => d.Kind == DialogKind.Personal,
            "group" => d.Kind == DialogKind.Group,
            "supergroup" => d.Kind == DialogKind.Supergroup,
            "channel" => d.Kind == DialogKind.Channel,
            "bot" => d.Kind == DialogKind.Bot,
            _ => true
        };
    }

    private List<DialogListItem> GetVisibleChatList()
    {
        var view = CollectionViewSource.GetDefaultView(_chatItems);
        return _chatItems.Where(c => view.Filter == null || view.Filter(c)).Cast<DialogListItem>().ToList();
    }

    private void MarkRangeVisible(int from, int to, bool value)
    {
        var list = GetVisibleChatList();
        from = Math.Clamp(from, 0, list.Count - 1);
        to = Math.Clamp(to, 0, list.Count - 1);
        if (list.Count == 0) return;
        for (var i = Math.Min(from, to); i <= Math.Max(from, to); i++)
            list[i].IsMarked = value;
        RefreshMarkedCount();
    }

    private void SchedulerTimer_OnTick(object? sender, EventArgs e)
    {
        if (!ChkScheduler.IsChecked == true || _client is null) return;
        if (!TimeSpan.TryParse(TxtSchedulerTime.Text.Trim(), out var target)) return;

        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");
        if (_settings.LastSchedulerRunDate == today) return;

        var delta = Math.Abs((now.TimeOfDay - target).TotalMinutes);
        if (delta > 1) return;

        if (_exportQueue.Any(q => q.Status == QueueItemStatus.Pending) && !_queueRunner.IsRunning)
        {
            _settings.LastSchedulerRunDate = today;
            _settings.Save();
            AppendLog("Планировщик: автозапуск очереди.");
            BtnRunQueue_Click(BtnRunQueue, new RoutedEventArgs());
        }
    }

    private void BtnPauseQueue_Click(object sender, RoutedEventArgs e)
    {
        if (!_queueRunner.IsRunning) return;
        if (_queueRunner.IsPaused)
        {
            _queueRunner.Resume();
            BtnPauseQueue.Content = "⏸ Пауза";
            AppendLog("Скачивание возобновлено.");
        }
        else
        {
            _queueRunner.Pause();
            BtnPauseQueue.Content = "▶ Продолжить";
            AppendLog("Скачивание на паузе.");
        }
    }

    private void BtnRetryFailed_Click(object sender, RoutedEventArgs e)
    {
        var n = 0;
        foreach (var q in _exportQueue.Where(q => q.Status == QueueItemStatus.Failed || q.Status == QueueItemStatus.Cancelled))
        {
            q.Status = QueueItemStatus.Pending;
            q.StatusHint = "";
            n++;
        }
        if (n == 0) { AppendLog("Нет чатов с ошибкой для повтора."); return; }
        AppendLog($"В очередь на повтор: {n} чатов.");
        RefreshQueueUi();
        if (!_queueRunner.IsRunning) BtnRunQueue_Click(BtnRunQueue, new RoutedEventArgs());
    }

    private void BtnRetryQueueItem_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ExportQueueItem item) return;
        if (item.Status == QueueItemStatus.Running) return;
        item.Status = QueueItemStatus.Pending;
        item.StatusHint = "";
        RefreshQueueUi();
        if (!_queueRunner.IsRunning) BtnRunQueue_Click(BtnRunQueue, new RoutedEventArgs());
    }

    private async void BtnEstimateMarked_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) { AppendLog("Сначала подключитесь."); return; }
        var marked = _chatItems.Where(c => c.IsMarked).ToList();
        if (marked.Count == 0) { AppendLog("Отметьте чаты для оценки."); return; }

        var folderRoot = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(folderRoot)) { AppendLog("Укажите папку экспорта."); return; }

        BtnEstimateMarked.IsEnabled = false;
        AppendLog($"Оценка {marked.Count} чатов…");
        var opts = BuildExportOptions();
        long totalBytes = 0;
        var totalMsgs = 0;
        var totalMedia = 0;

        try
        {
            foreach (var chat in marked.Take(50))
            {
                try
                {
                    var est = await ChatEstimateService.EstimateAsync(_client, chat, folderRoot, opts, CancellationToken.None);
                    totalBytes += est.EstimatedBytes;
                    totalMsgs += est.MessageCount;
                    totalMedia += est.MediaCount;
                    var outDir = ExportPathHelper.ResolveChatOutputDir(folderRoot, chat, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    var diff = IncrementalPreviewService.FormatDiffSummary(est.NewSinceExport, 0, est.NewSinceExport > 0);
                    AppendLog($"  {chat.Title}: {est.MessageCount} сообщ., ~{est.MediaCount} медиа, ~{DiskSpaceChecker.FormatBytes(est.EstimatedBytes)} ({diff})");
                }
                catch (Exception ex) { AppendLog($"  {chat.Title}: {ex.Message}"); }
            }
            if (marked.Count > 50) AppendLog($"  … и ещё {marked.Count - 50} (оценены первые 50)");

            var disk = DiskSpaceChecker.CheckPath(folderRoot, Math.Max(totalBytes, 512L * 1024 * 1024));
            AppendLog($"Итого: ~{totalMsgs} сообщ., ~{totalMedia} медиа, ~{DiskSpaceChecker.FormatBytes(totalBytes)}. {disk.Message}");
        }
        finally { BtnEstimateMarked.IsEnabled = true; }
    }

    private void BtnTextOnlyPreset_Click(object sender, RoutedEventArgs e)
    {
        CmbMediaQuality.SelectedIndex = 3; // none
        ChkPhotos.IsChecked = false;
        ChkVideos.IsChecked = false;
        ChkDocuments.IsChecked = false;
        ChkVoice.IsChecked = false;
        ChkStickers.IsChecked = false;
        AppendLog("Пресет «Только текст»: медиа отключено, HTML/JSON остаются.");
    }

    private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = (CmbQueueProfiles.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = $"Профиль {DateTime.Now:dd.MM.yy HH:mm}";
            CmbQueueProfiles.Text = name;
        }

        var profile = new QueueProfile
        {
            Name = name,
            Chats = _exportQueue.Select(q => new QueueProfileChat
            {
                PeerKey = q.PeerKey,
                Title = q.Title,
                Subtitle = q.Subtitle
            }).ToList()
        };
        if (profile.Chats.Count == 0)
        {
            profile.Chats = _chatItems.Where(c => c.IsMarked).Select(c => new QueueProfileChat
            {
                PeerKey = c.PeerKey,
                Title = c.Title,
                Subtitle = c.Subtitle
            }).ToList();
        }

        QueueProfileService.Save(profile);
        RefreshProfileCombo();
        CmbQueueProfiles.Text = name;
        AppendLog($"Профиль сохранён: {name} ({profile.Chats.Count} чатов).");
    }

    private void BtnLoadProfile_Click(object sender, RoutedEventArgs e)
    {
        var name = (CmbQueueProfiles.Text ?? "").Trim();
        if (string.IsNullOrEmpty(name)) { AppendLog("Выберите или введите имя профиля."); return; }

        var profile = QueueProfileService.Load(name);
        var added = 0;
        foreach (var pc in profile.Chats)
        {
            var chat = DialogPeerKey.FindChat(_chatItems, pc.PeerKey, pc.Title);
            if (chat is null) continue;
            if (_exportQueue.Any(q => q.IsSameDialog(chat) && IsInActiveQueue(q))) continue;
            _exportQueue.Add(new ExportQueueItem(chat));
            added++;
        }
        ReindexQueue();
        RefreshQueueUi();
        AppendLog($"Профиль «{name}»: добавлено {added} чатов в очередь.");
    }

    private void ListQueue_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _dragQueueItem = (e.OriginalSource as DependencyObject) is { } src
            ? VisualTreeExtensions.FindParent<ListBoxItem>(src)?.DataContext as ExportQueueItem
            : null;
    }

    private void ListQueue_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragQueueItem is null) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < 4 && Math.Abs(pos.Y - _dragStartPoint.Y) < 4) return;
        DragDrop.DoDragDrop(ListQueue, _dragQueueItem, DragDropEffects.Move);
    }

    private void ListQueue_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(ExportQueueItem)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void ListQueue_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ExportQueueItem))) return;
        var source = e.Data.GetData(typeof(ExportQueueItem)) as ExportQueueItem;
        if (source is null) return;

        var target = (e.OriginalSource as DependencyObject) is { } obj
            ? VisualTreeExtensions.FindParent<ListBoxItem>(obj)?.DataContext as ExportQueueItem
            : null;
        if (target is null || source == target) return;

        var oldIdx = _exportQueue.IndexOf(source);
        var newIdx = _exportQueue.IndexOf(target);
        if (oldIdx < 0 || newIdx < 0) return;

        _exportQueue.Move(oldIdx, newIdx);
        ReindexQueue();
        RefreshQueueUi();
    }

    private void SaveFeatureSettingsFromUi()
    {
        _settings.EnableFileLog = ChkEnableFileLog.IsChecked == true;
        _settings.EnableToast = ChkEnableToast.IsChecked == true;
        _settings.SchedulerEnabled = ChkScheduler.IsChecked == true;
        _settings.SchedulerTime = TxtSchedulerTime.Text.Trim();
        _fileLog.Enabled = _settings.EnableFileLog;
        _toast.Enabled = _settings.EnableToast;
        if (CmbRetryCount.SelectedItem is ComboBoxItem ri && int.TryParse(ri.Tag?.ToString(), out var rc))
            _settings.AutoRetryCount = rc;
    }

    private int GetRetryCount() =>
        CmbRetryCount.SelectedItem is ComboBoxItem ri && int.TryParse(ri.Tag?.ToString(), out var n) ? n : _settings.AutoRetryCount;

    private void NotifyExportComplete(string title, string message)
    {
        if (_settings.EnableToast) _toast.Show(title, message);
    }
}
