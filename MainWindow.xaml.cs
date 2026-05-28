using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using TelegaScan.Helpers;
using TelegaScan.Services;
using WTelegram;

namespace TelegaScan;

// Обёртка для записи истории в ListBox
public sealed class HistoryDisplayItem
{
    private readonly ExportHistoryEntry _e;
    public HistoryDisplayItem(ExportHistoryEntry e) => _e = e;
    public string DisplayLine => $"{_e.Date.ToLocalTime():dd.MM.yy HH:mm}  {_e.ChatTitle}  [{_e.MessageCount} сообщ., {_e.MediaCount} медиа, {_e.Duration.TotalSeconds:F0}с]";
    public string OutputPath => _e.OutputPath;
}

public partial class MainWindow
{
    private readonly AppSettingsStore _settings = AppSettingsStore.Load();
    private readonly ObservableCollection<DialogListItem> _chatItems = new();
    private readonly ObservableCollection<ExportQueueItem> _exportQueue = new();

    private Client? _client;
    private CancellationTokenSource? _exportCts;
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private readonly ExportQueueRunner _queueRunner;
    private bool _connectBusy;
    private bool _isExporting;
    private int _queueSessionDone;
    private int _queueSessionFailed;
    private DateTime? _mediaPhaseStartedUtc;
    private CancellationTokenSource? _countEnrichmentCts;
    private bool _chatSortDescending;
    private string? _loadedQueueAccountKey;
    private ExportQueueItem? _activeQueueEntry;

    public MainWindow()
    {
        InitializeComponent();
        AppIconService.ApplyTo(this);
        InitFeatureServices();
        _toast.ShowTrayIcon();

        TxtApiId.Text = _settings.ApiId;
        TxtApiHash.Text = _settings.ApiHash;
        TxtPhone.Text = _settings.PhoneNumber;
        TxtExportFolder.Text = string.IsNullOrEmpty(_settings.LastExportFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TelegaScanExports")
            : _settings.LastExportFolder;
        _chatSortDescending = _settings.ChatSortDescending;
        ListChats.ItemsSource = _chatItems;
        var chatView = CollectionViewSource.GetDefaultView(_chatItems);
        chatView.Filter = ChatFilter;
        ApplyChatSort();
        ListQueue.ItemsSource = _exportQueue;
        SetupQueueListView();
        LoadPersistedQueue();
        _queueRunner = new ExportQueueRunner(
            () => _exportQueue,
            DequeueNextPendingAsync,
            ExportQueueEntryAsync,
            OnQueueIdleStopping,
            OnQueueWorkerFinished);
        RefreshQueueUi();
        UpdateSortDirectionButton();

        SliderHistoryDelay.ValueChanged += (_, _) => LblHistoryDelay.Text = $"{(int)SliderHistoryDelay.Value} мс";
        SliderMediaDelay.ValueChanged += (_, _) => LblMediaDelay.Text = $"{(int)SliderMediaDelay.Value} мс";
        SliderParallel.ValueChanged += (_, _) => LblParallel.Text = $"{(int)SliderParallel.Value}";

        // Горячие клавиши
        KeyDown += MainWindow_KeyDown;

        // Загружаем историю
        LoadHistory();

        WTelegram.Helpers.Log = (level, msg) => System.Diagnostics.Debug.WriteLine($"[WTelegram {level}] {msg}");

        if (_exportQueue.Count > 0)
            AppendLog($"Восстановлена очередь: {_exportQueue.Count} чатов. Подключитесь и нажмите «Скачать» для продолжения.");
    }

    private bool ChatFilter(object obj) =>
        obj is DialogListItem d
        && (TxtFilter.Text.Trim().Length == 0 || d.Title.Contains(TxtFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase))
        && MatchesTypeFilter(d);

    private void TxtFilter_OnTextChanged(object sender, TextChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_chatItems).Refresh();

    private DialogChatSortMode GetChatSortMode() =>
        CmbChatSort.SelectedItem is ComboBoxItem { Tag: string tag }
            ? tag switch
            {
                "name" => DialogChatSortMode.Name,
                "count" => DialogChatSortMode.MessageCount,
                _ => DialogChatSortMode.LastMessage
            }
            : DialogChatSortMode.LastMessage;

    private void CmbChatSort_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        ApplyChatSort();
        if (GetChatSortMode() == DialogChatSortMode.MessageCount && _client != null)
            StartMessageCountEnrichment();
    }

    private void BtnSortDirection_Click(object sender, RoutedEventArgs e)
    {
        _chatSortDescending = !_chatSortDescending;
        _settings.ChatSortDescending = _chatSortDescending;
        ApplyChatSort();
    }

    private void UpdateSortDirectionButton()
    {
        if (BtnSortDirection is null) return;
        BtnSortDirection.Content = _chatSortDescending ? "↓" : "↑";
        BtnSortDirection.ToolTip = GetChatSortMode() switch
        {
            DialogChatSortMode.Name => _chatSortDescending ? "Имена: Я → А" : "Имена: А → Я",
            DialogChatSortMode.MessageCount => _chatSortDescending ? "Сообщений: больше сначала" : "Сообщений: меньше сначала",
            _ => _chatSortDescending ? "Дата: новые сначала" : "Дата: старые сначала"
        };
    }

    private void ApplyChatSort()
    {
        if (CmbChatSort is null) return;
        var view = CollectionViewSource.GetDefaultView(_chatItems);
        if (view is null) return;

        var primary = _chatSortDescending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        view.SortDescriptions.Clear();
        switch (GetChatSortMode())
        {
            case DialogChatSortMode.Name:
                view.SortDescriptions.Add(new SortDescription(nameof(DialogListItem.Title), primary));
                break;
            case DialogChatSortMode.MessageCount:
                view.SortDescriptions.Add(new SortDescription(nameof(DialogListItem.MessageCount), primary));
                view.SortDescriptions.Add(new SortDescription(nameof(DialogListItem.Title), ListSortDirection.Ascending));
                break;
            default:
                view.SortDescriptions.Add(new SortDescription(nameof(DialogListItem.LastMessageUtc), primary));
                view.SortDescriptions.Add(new SortDescription(nameof(DialogListItem.Title), ListSortDirection.Ascending));
                break;
        }
        UpdateSortDirectionButton();
    }

    private string GetQueueStoreKey()
    {
        var acc = _accountStore.GetActive();
        if (acc != null) return acc.Id;
        var phone = _settings.PhoneNumber.Trim();
        return string.IsNullOrEmpty(phone) ? "default" : phone;
    }

    private void LoadPersistedQueue()
    {
        var key = GetQueueStoreKey();
        _loadedQueueAccountKey = key;
        var saved = QueuePersistenceService.Load(key);
        if (saved.Count == 0) return;

        foreach (var p in saved.OrderBy(x => x.Position))
        {
            var item = ExportQueueItem.Restore(p.PeerKey, p.Title, p.Subtitle);
            item.Status = p.Status == QueueItemStatus.Running ? QueueItemStatus.Pending : p.Status;
            item.Position = p.Position;
            _exportQueue.Add(item);
        }
        ReindexQueue(persist: false);
    }

    private void PersistQueue() =>
        QueuePersistenceService.Save(GetQueueStoreKey(), _exportQueue);

    private void RestoreQueueAfterDialogsLoaded()
    {
        var unmatched = 0;
        foreach (var item in _exportQueue)
        {
            var chat = DialogPeerKey.FindChat(_chatItems, item.PeerKey, item.Title);
            if (chat != null)
                item.AttachChat(chat);
            else if (item.Status is QueueItemStatus.Pending or QueueItemStatus.Running)
                unmatched++;
        }

        var pending = _exportQueue.Count(q => q.Status == QueueItemStatus.Pending);
        if (_exportQueue.Count > 0)
        {
            var msg = pending > 0
                ? $"Очередь: {pending} чатов ожидают. Нажмите «Скачать» для продолжения."
                : $"Очередь: {_exportQueue.Count} чатов (все обработаны или с ошибками).";
            if (unmatched > 0)
                msg += $" Не найдено в списке: {unmatched}.";
            AppendLog(msg);
        }
        RefreshQueueUi();
        PersistQueue();
        RevalidateQueueCompletion();
    }

    private void RevalidateQueueCompletion()
    {
        var root = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;

        var opts = BuildExportOptions();
        var revived = 0;
        foreach (var item in _exportQueue.Where(q => q.Status == QueueItemStatus.Done).ToList())
        {
            if (!item.HasChat) continue;
            var dir = ExportCompletionTracker.FindExistingChatDir(root, item.Chat);
            if (dir is null) continue;
            var state = IncrementalStateService.Load(dir);
            if (ExportCompletionTracker.IsFullyComplete(dir, state, opts)) continue;
            item.Status = QueueItemStatus.Pending;
            item.StatusHint = "докачка";
            revived++;
        }

        if (revived <= 0) return;
        AppendLog($"В очередь возвращено {revived} чат(ов) с незавершённой докачкой (медиа или файлы).");
        ReindexQueue();
        RefreshQueueUi();
        PersistQueue();
    }

    private void SwitchQueueForAccount()
    {
        var key = GetQueueStoreKey();
        if (key == _loadedQueueAccountKey) return;
        PersistQueue();
        _exportQueue.Clear();
        _loadedQueueAccountKey = key;
        LoadPersistedQueue();
        RefreshQueueUi();
        if (_client != null && _chatItems.Count > 0)
            RestoreQueueAfterDialogsLoaded();
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        if (_queueRunner.IsRunning)
        {
            foreach (var q in _exportQueue.Where(q => q.Status == QueueItemStatus.Running))
            {
                q.Status = QueueItemStatus.Pending;
                q.StatusHint = "";
            }
        }
        _settings.ChatSortDescending = _chatSortDescending;
        PersistQueue();
    }

    private void StartMessageCountEnrichment()
    {
        if (_client is null || _chatItems.Count == 0) return;
        if (_isExporting || _queueRunner.IsRunning) return;
        if (_chatItems.All(c => c.MessageCount >= 0)) return;

        _countEnrichmentCts?.Cancel();
        _countEnrichmentCts = new CancellationTokenSource();
        var ct = _countEnrichmentCts.Token;
        AppendLog("Подсчёт сообщений в чатах для сортировки…");

        _ = Task.Run(async () =>
        {
            try
            {
                await ChatExportService.EnrichMessageCountsAsync(
                    _client, _chatItems.ToList(), ct,
                    () => _isExporting || _queueRunner.IsRunning).ConfigureAwait(false);
                Dispatcher.Invoke(() =>
                {
                    AppendLog("Подсчёт сообщений завершён.");
                    if (GetChatSortMode() == DialogChatSortMode.MessageCount)
                        ApplyChatSort();
                });
            }
            catch (OperationCanceledException) { /* */ }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => AppendLog("Подсчёт сообщений: " + ex.Message));
            }
        }, ct);
    }

    private void SaveSettingsFromUi()
    {
        _settings.ApiId = TxtApiId.Text.Trim();
        _settings.ApiHash = TxtApiHash.Text.Trim();
        _settings.PhoneNumber = TxtPhone.Text.Trim();
        _settings.LastExportFolder = TxtExportFolder.Text.Trim();
        _settings.ChatSortDescending = _chatSortDescending;
        SaveFeatureSettingsFromUi();
        _settings.Save();
    }

    private void Credentials_OnLostFocus(object sender, RoutedEventArgs e) => SaveSettingsFromUi();
    // ───── Подключение через client.Login() — без колбэков с фоновых потоков ─────

    private async void BtnConnect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_connectBusy || _isExporting) return;
        await DoConnect();
    }

    /// <summary>
    /// Основной метод подключения. Использует client.Login() — возвращает строку,
    /// говорящую, что ещё нужно (номер, код, пароль…). Мы отдаём управление обратно
    /// в UI и ждём ввода — никаких Dispatcher.Invoke с фоновых потоков.
    /// </summary>
    private async Task DoConnect()
    {
        if (_connectBusy) return;
        _connectBusy = true;
        BtnConnect.IsEnabled = false;

        if (!int.TryParse(TxtApiId.Text.Trim(), out var apiId))
        {
            TxtStatus.Text = "api_id должен быть числом.";
            BtnConnect.IsEnabled = true;
            _connectBusy = false;
            return;
        }

        var apiHash = TxtApiHash.Text.Trim();
        var phone = TxtPhone.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiHash) || string.IsNullOrWhiteSpace(phone))
        {
            TxtStatus.Text = "Заполните api_hash и телефон.";
            BtnConnect.IsEnabled = true;
            _connectBusy = false;
            return;
        }

        try
        {
            var old = _client;
            _client = null;
            if (old != null) await old.DisposeAsync();

            var sessionPath = GetSessionPath();
            var dir = Path.GetDirectoryName(sessionPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            TxtStatus.Text = "Подключение к серверам Telegram…";

            string MinimalConfig(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => sessionPath,
                _ => null!
            };

            _client = new Client(MinimalConfig);
            _client.OnUpdates += _ => Task.CompletedTask;
            TxtStatus.Text = "Авторизация…";

            string? loginInfo = phone;

            while (_client.User == null)
            {
                var needed = await _client.Login(loginInfo);

                switch (needed)
                {
                    case "verification_code":
                        TxtStatus.Text = "Код отправлен — проверьте Telegram или SMS.";
                        ShowCodeCard(isPassword: false);
                        loginInfo = await WaitForUserInput();
                        break;

                    case "password":
                        TxtStatus.Text = "Нужен пароль двухэтапной аутентификации.";
                        ShowCodeCard(isPassword: true);
                        loginInfo = await WaitForUserInput();
                        break;

                    case "name":
                        loginInfo = "User";
                        break;

                    default:
                        loginInfo = null;
                        break;
                }
            }

            HideCodeCard();
            var userName = $"{_client.User.first_name} {_client.User.last_name}".Trim();
            var acc = _accountStore.GetOrCreateDefault(phone);
            acc.DisplayName = string.IsNullOrWhiteSpace(userName) ? phone : $"{userName} ({phone})";
            acc.Phone = phone;
            _accountStore.Save();
            RefreshAccountsCombo();

            TxtStatus.Text = $"Вошли как {userName} (id {_client.User.id}). Загрузка чатов…";

            List<DialogListItem> dialogs;
            try
            {
                dialogs = await Task.Run(() => ChatExportService.LoadDialogsAsync(_client));
            }
            catch (Exception dlgEx)
            {
                TxtStatus.Text = $"Авторизованы как {userName}, но чаты не загрузились: {dlgEx.GetBaseException().Message}";
                dialogs = [];
            }

            _chatItems.Clear();
            foreach (var d in dialogs) _chatItems.Add(d);
            ApplyChatSort();
            StartMessageCountEnrichment();

            if (dialogs.Count > 0)
                TxtStatus.Text = $"{userName} — {dialogs.Count} диалогов. Выберите чат.";
            BtnDisconnect.Visibility = Visibility.Visible;
            BtnExport.IsEnabled = ListChats.SelectedItem != null;
            BtnPreview.IsEnabled = BtnExport.IsEnabled;
            RestoreQueueAfterDialogsLoaded();
            RevalidateQueueCompletion();
            SaveSettingsFromUi();
        }
        catch (Exception ex)
        {
            HideCodeCard();
            TxtStatus.Text = "Ошибка: " + ex.GetBaseException().Message;
            if (_client != null) { await _client.DisposeAsync(); _client = null; }
        }
        finally
        {
            BtnConnect.IsEnabled = true;
            _connectBusy = false;
        }
    }

    // ───── Карточка ввода кода / пароля ─────

    private TaskCompletionSource<string>? _inputTcs;

    private void ShowCodeCard(bool isPassword)
    {
        CardCode.Visibility = Visibility.Visible;
        if (isPassword)
        {
            LblCodeTitle.Text = "2. Пароль 2FA";
            LblCodeHint.Text = "Введите пароль облачного хранилища Telegram (двухэтапная аутентификация).";
            TxtCode.Visibility = Visibility.Collapsed;
            TxtPassword.Visibility = Visibility.Visible;
            TxtPassword.Clear();
            TxtPassword.Focus();
        }
        else
        {
            LblCodeTitle.Text = "2. Код из Telegram";
            LblCodeHint.Text = "Код пришёл в уведомлениях приложения Telegram или по SMS. Введите его ниже.";
            TxtPassword.Visibility = Visibility.Collapsed;
            TxtCode.Visibility = Visibility.Visible;
            TxtCode.Clear();
            TxtCode.Focus();
        }
        _inputTcs = new TaskCompletionSource<string>();
    }

    private void HideCodeCard()
    {
        CardCode.Visibility = Visibility.Collapsed;
    }

    private Task<string> WaitForUserInput()
    {
        return _inputTcs?.Task ?? Task.FromResult("");
    }

    private void BtnSendCode_OnClick(object sender, RoutedEventArgs e)
    {
        var value = TxtPassword.Visibility == Visibility.Visible
            ? TxtPassword.Password
            : TxtCode.Text.Trim();
        _inputTcs?.TrySetResult(value);
    }

    // ───── Остальное ─────

    private async void BtnDisconnect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_client != null) await _client.DisposeAsync();
        _client = null;
        _chatItems.Clear();
        TxtStatus.Text = "Не подключено";
        BtnExport.IsEnabled = false;
        BtnDisconnect.Visibility = Visibility.Collapsed;
        TxtSelectedChat.Text = "Чат не выбран";
    }

    private void ListChats_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateSelectedChatLabel();
        UpdateExportButtons();
    }

    private void UpdateSelectedChatLabel()
    {
        var marked = _chatItems.Count(c => c.IsMarked);
        var pending = _exportQueue.Count(q => q.Status == QueueItemStatus.Pending);
        var inQueue = _exportQueue.Count;
        if (marked > 0)
        {
            TxtSelectedChat.Text = $"Отмечено: {marked}" +
                (inQueue > 0 ? $"  |  в очереди: {pending} ожидает / {inQueue} всего" : "");
            return;
        }
        TxtSelectedChat.Text = ListChats.SelectedItem is DialogListItem d
            ? $"Просмотр: {d.Title}" + (inQueue > 0 ? $"  |  очередь: {pending}" : "")
            : inQueue > 0 ? $"В очереди: {pending} ожидает" : "Отметьте чаты чекбоксами";
    }

    private void ChatMark_Changed(object sender, RoutedEventArgs e) => RefreshMarkedCount();

    private void ChatCheckBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is CheckBox { DataContext: DialogListItem item })
        {
            item.IsMarked = !item.IsMarked;
            e.Handled = true;
        }
    }

    private void ListChats_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;
        if (VisualTreeExtensions.FindParent<CheckBox>(source) != null) return;

        var container = VisualTreeExtensions.FindParent<ListBoxItem>(source);
        if (container?.DataContext is not DialogListItem item) return;

        var visible = GetVisibleChatList();
        var idx = visible.IndexOf(item);

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && _lastMarkedVisibleIndex.HasValue && idx >= 0)
        {
            MarkRangeVisible(_lastMarkedVisibleIndex.Value, idx, true);
            e.Handled = true;
            return;
        }

        item.IsMarked = !item.IsMarked;
        if (idx >= 0) _lastMarkedVisibleIndex = idx;
        RefreshMarkedCount();
        e.Handled = true;
    }

    private void RefreshMarkedCount()
    {
        var marked = _chatItems.Count(c => c.IsMarked);
        LblMarkedCount.Text = $"Отмечено: {marked}";
        UpdateSelectedChatLabel();
    }

    private IEnumerable<DialogListItem> GetVisibleChats()
    {
        var view = CollectionViewSource.GetDefaultView(_chatItems);
        return _chatItems.Where(c => view.Filter == null || view.Filter(c));
    }

    private void BtnMarkAllVisible_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in GetVisibleChats()) c.IsMarked = true;
        RefreshMarkedCount();
    }

    private void BtnUnmarkAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in _chatItems) c.IsMarked = false;
        RefreshMarkedCount();
    }

    private void BtnInvertMarks_Click(object sender, RoutedEventArgs e)
    {
        foreach (var c in GetVisibleChats()) c.IsMarked = !c.IsMarked;
        RefreshMarkedCount();
    }

    private void ListQueue_OnSelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void BtnPickFolder_OnClick(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = Directory.Exists(TxtExportFolder.Text) ? TxtExportFolder.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TxtExportFolder.Text = dlg.SelectedPath;
            SaveSettingsFromUi();
        }
    }

    private ExportOptions BuildExportOptions()
    {
        var pageItem = (ComboBoxItem)CmbPageSize.SelectedItem;
        var perPage = int.TryParse(pageItem.Tag?.ToString(), out var ps) ? ps : 250;
        var qualityTag = ((ComboBoxItem)CmbMediaQuality.SelectedItem).Tag?.ToString() ?? "original";
        var quality = qualityTag switch
        {
            "compressed" => MediaQuality.Compressed,
            "thumbs" => MediaQuality.Thumbs,
            "none" => MediaQuality.None,
            _ => MediaQuality.Original
        };

        return new ExportOptions
        {
            MessagesPerPage = perPage,
            DelayBetweenHistoryRequests = TimeSpan.FromMilliseconds((int)SliderHistoryDelay.Value),
            DelayAfterEachMediaFile = TimeSpan.FromMilliseconds((int)SliderMediaDelay.Value),
            HistoryBatchSize = 100,
            Quality = quality,
            Mode = RbChannel.IsChecked == true ? ExportMode.Channel : ExportMode.Chat,
            ParallelDownloads = (int)SliderParallel.Value,
            FromDate = DpFrom.SelectedDate,
            ToDate = DpTo.SelectedDate,
            DownloadPhotos = ChkPhotos.IsChecked == true,
            DownloadVideos = ChkVideos.IsChecked == true,
            DownloadDocuments = ChkDocuments.IsChecked == true,
            DownloadVoice = ChkVoice.IsChecked == true,
            DownloadStickers = ChkStickers.IsChecked == true,
            Incremental = ChkIncremental.IsChecked == true,
            ExportJson = ChkExportJson.IsChecked == true,
            ExportSqlite = ChkExportSqlite.IsChecked == true,
            GenerateStatistics = ChkStatistics.IsChecked == true,
            SaveLongTexts = ChkLongTexts.IsChecked == true,
            GroupAlbums = ChkAlbums.IsChecked == true,
            GroupThreads = ChkThreads.IsChecked == true,
            TrackForwarded = ChkForwarded.IsChecked == true,
            DeduplicateMedia = ChkDedup.IsChecked == true,
            CreateZip = ChkZip.IsChecked == true
        };
    }

    private void SetExporting(bool exporting)
    {
        _isExporting = exporting;
        UpdateExportButtons();
        BtnCancelExport.IsEnabled = exporting;
    }

    private void UpdateExportButtons()
    {
        var hasClient = _client != null;
        var hasPending = _exportQueue.Any(q => q.Status == QueueItemStatus.Pending);
        BtnExport.IsEnabled = hasClient && !_isExporting && ListChats.SelectedItem != null;
        BtnPreview.IsEnabled = hasClient && !_isExporting && ListChats.SelectedItem != null;
        BtnRunQueue.IsEnabled = hasClient && hasPending && !_queueRunner.IsRunning;
        BtnRunQueue.Content = _queueRunner.IsRunning
            ? "⏳ Скачивание…"
            : (hasPending ? $"▶ Скачать очередь ({_exportQueue.Count(q => q.Status == QueueItemStatus.Pending)})" : "▶ Скачать очередь");
    }

    private async void BtnExport_OnClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || _isExporting || ListChats.SelectedItem is not DialogListItem chat) return;
        var folderRoot = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(folderRoot)) { AppendLog("Укажите папку экспорта."); return; }

        Directory.CreateDirectory(folderRoot);
        var outDir = ExportPathHelper.ResolveChatOutputDir(folderRoot, chat, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        if (Directory.Exists(Path.Combine(outDir, "media")))
            AppendLog("Папка уже существует — докачка: существующие файлы будут пропущены.");

        var options = BuildExportOptions();
        _exportCts = new CancellationTokenSource();
        SetExporting(true);
        ResetExportProgressUi();
        ExportProgressBar.IsIndeterminate = true;
        AppendLog($"--- Старт: {chat.Title} → {outDir} ---");

        var progress = new Progress<ExportProgressReport>(r => Dispatcher.Invoke(() => ApplyExportProgress(r)));
        var success = false;
        try
        {
            await _exportGate.WaitAsync(_exportCts.Token);
            try
            {
                await Task.Run(() => ChatExportService.ExportChatToHtmlAsync(_client, chat.Peer, chat.Title, outDir, options, progress, _exportCts.Token), _exportCts.Token);
                success = true;
            }
            finally { _exportGate.Release(); }
        }
        catch (OperationCanceledException) { AppendLog("Экспорт остановлен."); }
        catch (Exception ex) { AppendLog("Ошибка: " + ex.Message); }
        finally
        {
            ExportProgressBar.IsIndeterminate = false;
            SetExporting(false);
        }

        if (success)
        {
            LoadHistory();
            try { SystemSounds.Exclamation.Play(); } catch { /* */ }
            NotifyExportComplete("TelegaScan", $"Экспорт завершён: {chat.Title}");
            AppendLog($"Файлы: {outDir}");
        }
    }

    private void BtnCancelExport_OnClick(object sender, RoutedEventArgs e)
    {
        _exportCts?.Cancel();
        if (_queueRunner.IsRunning)
            _queueRunner.Stop();
        else
            AppendLog("Остановка экспорта…");
    }

    private void RbMode_Checked(object sender, RoutedEventArgs e)
    {
        if (LblModeHint is null) return;
        if (RbChannel?.IsChecked == true)
            LblModeHint.Text = "Канал: год/месяц/дата-тема + _index.html навигация.";
        else
            LblModeHint.Text = "Чат/группа: HTML + медиа по год/месяц.";
    }

    // ─── Drag & Drop на папку ─────────────────────────────────────
    private void TxtExportFolder_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TxtExportFolder_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            var path = paths[0];
            if (File.Exists(path)) path = Path.GetDirectoryName(path) ?? path;
            TxtExportFolder.Text = path;
            SaveSettingsFromUi();
        }
    }

    // ─── Горячие клавиши ─────────────────────────────────────────
    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control && BtnExport.IsEnabled)
        {
            BtnExport_OnClick(BtnExport, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Q && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            BtnAddToQueue_Click(BtnAddMarkedToQueue, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && BtnRunQueue.IsEnabled)
        {
            BtnRunQueue_Click(BtnRunQueue, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && BtnCancelExport.IsEnabled)
        {
            _exportCts?.Cancel();
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenExportFolder();
            e.Handled = true;
        }
    }

    private void OpenExportFolder()
    {
        var folder = TxtExportFolder.Text.Trim();
        if (Directory.Exists(folder))
            Process.Start("explorer.exe", folder);
    }

    // ─── Предпросмотр ────────────────────────────────────────────
    private async void BtnPreview_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || ListChats.SelectedItem is not DialogListItem chat) return;
        BtnPreview.IsEnabled = false;
        AppendLog("Предпросмотр: подсчёт сообщений…");
        try
        {
            var opts = new ExportOptions
            {
                HistoryBatchSize = 100,
                DelayBetweenHistoryRequests = TimeSpan.FromMilliseconds((int)SliderHistoryDelay.Value),
                FromDate = DpFrom.SelectedDate,
                ToDate = DpTo.SelectedDate
            };
            var folderRoot = TxtExportFolder.Text.Trim();
            var outDir = string.IsNullOrEmpty(folderRoot)
                ? ""
                : ExportPathHelper.ResolveChatOutputDir(folderRoot, chat, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            var (sorted, _, _) = await Task.Run(() =>
                ChatExportService.LoadAllMessagesInternalAsync(_client, chat.Peer, opts, null, CancellationToken.None));
            var mediaCount = sorted.Count(m => m is TL.Message { media: not null });
            var state = string.IsNullOrEmpty(outDir) ? null : IncrementalStateService.Load(outDir);
            var lastId = opts.Incremental && state != null ? state.LastMessageId : 0;
            var newCount = lastId > 0 ? sorted.Count(m => m.ID > lastId) : sorted.Count;
            var diff = IncrementalPreviewService.FormatDiffSummary(newCount, lastId, lastId > 0 || Directory.Exists(outDir));
            AppendLog($"Предпросмотр: {sorted.Count} сообщ., ~{mediaCount} медиа. Инкремент: {diff}");
        }
        catch (Exception ex) { AppendLog("Предпросмотр: " + ex.Message); }
        finally { UpdateExportButtons(); }
    }

    // ─── Очистить даты ───────────────────────────────────────────
    private void BtnClearDates_Click(object sender, RoutedEventArgs e)
    {
        DpFrom.SelectedDate = null;
        DpTo.SelectedDate = null;
    }

    // ─── Очередь экспорта ────────────────────────────────────────
    private static bool IsInActiveQueue(ExportQueueItem q) =>
        q.Status is QueueItemStatus.Pending or QueueItemStatus.Running;

    private IEnumerable<DialogListItem> CollectMarkedChatsForQueue() =>
        _chatItems.Where(c => c.IsMarked);

    private void SetupQueueListView()
    {
        var view = CollectionViewSource.GetDefaultView(_exportQueue);
        if (view is null) return;
        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(ExportQueueItem.DisplayOrder), ListSortDirection.Ascending));
        view.SortDescriptions.Add(new SortDescription(nameof(ExportQueueItem.Position), ListSortDirection.Ascending));
    }

    private void RefreshQueueListView() =>
        CollectionViewSource.GetDefaultView(_exportQueue)?.Refresh();

    private void ScrollQueueItemIntoView(ExportQueueItem item)
    {
        RefreshQueueListView();
        ListQueue.UpdateLayout();
        ListQueue.ScrollIntoView(item);
    }

    private void BtnAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        var marked = CollectMarkedChatsForQueue().ToList();
        if (marked.Count == 0)
        {
            AppendLog("Отметьте чаты чекбоксами (кнопка «Все» — для всех видимых в фильтре).");
            return;
        }

        var added = 0;
        var requeued = 0;
        var skipped = 0;
        var skippedTitles = new List<string>();
        ExportQueueItem? lastTouched = null;

        foreach (var chat in marked)
        {
            var existing = _exportQueue.FirstOrDefault(q => q.IsSameDialog(chat));
            if (existing != null)
            {
                existing.AttachChat(chat);
                if (existing.Status is QueueItemStatus.Pending or QueueItemStatus.Running)
                {
                    skipped++;
                    skippedTitles.Add(chat.Title);
                    chat.IsMarked = false;
                    continue;
                }
                existing.Status = QueueItemStatus.Pending;
                existing.StatusHint = "";
                requeued++;
                lastTouched = existing;
                chat.IsMarked = false;
                continue;
            }

            var item = new ExportQueueItem(chat);
            _exportQueue.Add(item);
            lastTouched = item;
            chat.IsMarked = false;
            added++;
        }

        ReindexQueue();
        RefreshQueueUi();
        RefreshMarkedCount();

        if (lastTouched != null)
            ScrollQueueItemIntoView(lastTouched);

        if (added == 0 && requeued == 0)
        {
            if (skippedTitles.Count > 0)
                AppendLog($"Уже в очереди ({skipped}): {string.Join(", ", skippedTitles.Take(5))}" +
                          (skippedTitles.Count > 5 ? "…" : "") + ". Отметьте другие чаты.");
            else
                AppendLog("Нечего добавить в очередь.");
            return;
        }

        var parts = new List<string>();
        if (added > 0) parts.Add($"новых: {added}");
        if (requeued > 0) parts.Add($"снова в очередь: {requeued}");
        if (skipped > 0) parts.Add($"пропущено (уже есть): {skipped}");
        AppendLog($"В очередь — {string.Join(", ", parts)}. Ожидает: {_exportQueue.Count(q => q.Status == QueueItemStatus.Pending)}");

        if (_queueRunner.IsRunning && (added > 0 || requeued > 0))
            AppendLog("Скачивание уже идёт — новые чаты подхватятся автоматически.");
    }

    private void BtnRemoveFromQueue_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not ExportQueueItem item) return;
        if (item.Status == QueueItemStatus.Running)
        {
            AppendLog("Нельзя убрать чат, который сейчас скачивается.");
            return;
        }
        _exportQueue.Remove(item);
        ReindexQueue();
        RefreshQueueUi();
    }

    private void BtnClearQueue_Click(object sender, RoutedEventArgs e) => ClearQueueCompletely();

    /// <summary>Полная очистка очереди в памяти, UI и на диске (в т.ч. «невидимые» элементы).</summary>
    private void ClearQueueCompletely()
    {
        var key = GetQueueStoreKey();
        var n = _exportQueue.Count;
        var hasPersisted = QueuePersistenceService.Exists(key);

        if (n == 0 && !hasPersisted)
        {
            AppendLog("Очередь уже пуста.");
            return;
        }

        if (_queueRunner.IsRunning)
        {
            var msg = n > 0
                ? $"Остановить скачивание и удалить все {n} чатов из очереди?"
                : "Остановить скачивание и сбросить сохранённую очередь?";
            if (MessageBox.Show(msg, "Очистить очередь", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
                return;

            _queueRunner.Stop();
            _exportCts?.Cancel();
        }

        _exportQueue.Clear();
        QueuePersistenceService.Delete(key);
        ListQueue.ItemsSource = null;
        ListQueue.ItemsSource = _exportQueue;
        SetupQueueListView();
        PanelQueueProgress.Visibility = Visibility.Collapsed;
        _activeQueueEntry = null;

        ReindexQueue(persist: false);
        RefreshQueueListView();
        RefreshQueueUi();

        AppendLog(n > 0
            ? $"Очередь полностью очищена ({n} чатов). Можно добавлять чаты заново."
            : "Сохранённая очередь сброшена. Можно добавлять чаты заново.");
    }

    private void ReindexQueue(bool persist = true)
    {
        for (var i = 0; i < _exportQueue.Count; i++)
            _exportQueue[i].Position = i + 1;
        if (persist)
            PersistQueue();
        RefreshQueueListView();
    }

    private void RefreshQueueUi()
    {
        var pending = _exportQueue.Count(q => q.Status == QueueItemStatus.Pending);
        var running = _exportQueue.Count(q => q.Status == QueueItemStatus.Running);
        var n = _exportQueue.Count;

        UpdateExportButtons();
        BtnClearQueue.IsEnabled = n > 0 || QueuePersistenceService.Exists(GetQueueStoreKey());
        BtnPauseQueue.IsEnabled = _queueRunner.IsRunning;
        if (!_queueRunner.IsRunning) BtnPauseQueue.Content = "⏸ Пауза";

        if (_queueRunner.IsRunning)
        {
            LblQueueSummary.Text = running > 0
                ? $"скачивается…  |  ожидает: {pending}"
                : $"ожидает: {pending}";
        }
        else
            LblQueueSummary.Text = n == 0 ? "пусто" : $"{pending} ожидает, {n} всего";

        var root = TxtExportFolder.Text.Trim();
        LblQueueFolderHint.Text = string.IsNullOrEmpty(root)
            ? "Укажите папку экспорта справа"
            : _queueRunner.IsRunning
                ? "Можно отмечать и добавлять чаты — подхватятся по мере скачивания"
                : $"Каждый чат → {root}\\<название>";
        UpdateSelectedChatLabel();
        RefreshMarkedCount();
    }

    private static string _PluralChats(int n) =>
        (n % 10, n % 100) switch
        {
            (1, not 11) => "чат",
            ( >= 2 and <= 4, not (>= 12 and <= 14)) => "чата",
            _ => "чатов"
        };

    private void BtnRunQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null) return;
        if (_queueRunner.IsRunning) return;

        if (!_exportQueue.Any(q => q.Status == QueueItemStatus.Pending))
        {
            AppendLog("В очереди нет ожидающих чатов. Отметьте чаты и нажмите «+ В очередь».");
            return;
        }

        var folderRoot = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(folderRoot)) { AppendLog("Укажите папку экспорта."); return; }
        Directory.CreateDirectory(folderRoot);

        var disk = DiskSpaceChecker.CheckPath(folderRoot);
        AppendLog(disk.Message);
        if (!disk.Ok && MessageBox.Show(disk.Message + "\n\nВсё равно начать?", "Мало места на диске",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _countEnrichmentCts?.Cancel();
        _queueFolderNames.Clear();
        _exportCts = new CancellationTokenSource();
        _queueSessionDone = 0;
        _queueSessionFailed = 0;
        SetExporting(true);
        ResetExportProgressUi();
        ExportProgressBar.IsIndeterminate = false;

        RevalidateQueueCompletion();
        var pending = _exportQueue.Count(q => q.Status == QueueItemStatus.Pending);
        AppendLog($"--- Старт очереди: {pending} чатов, по одному. Можно добавлять новые во время скачивания. ---");

        _queueRunner.Start();
        RefreshQueueUi();
    }

    private Task<ExportQueueItem?> DequeueNextPendingAsync(CancellationToken ct) =>
        Dispatcher.InvokeAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            return _exportQueue.FirstOrDefault(q => q.Status == QueueItemStatus.Pending);
        }).Task;

    private async Task ExportQueueEntryAsync(ExportQueueItem entry, CancellationToken ct)
    {
        if (_client is null) return;

        if (!entry.HasChat)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                entry.Status = QueueItemStatus.Failed;
                entry.StatusHint = "нет в списке";
                AppendLog($"Чат «{entry.Title}» не найден в списке — переподключитесь.");
                RefreshQueueUi();
                PersistQueue();
            });
            return;
        }

        var job = await Dispatcher.InvokeAsync(() =>
        {
            var folderRoot = TxtExportFolder.Text.Trim();
            if (string.IsNullOrEmpty(folderRoot))
                throw new InvalidOperationException("Не указана папка экспорта.");

            var chat = entry.Chat;
            _activeQueueEntry = entry;
            PanelQueueProgress.Visibility = Visibility.Visible;
            LblQueueCurrentChat.Text = $"{chat.Title}: подготовка…";
            var outDir = ExportPathHelper.ResolveChatOutputDir(folderRoot, chat, _queueFolderNames);
            var pendingLeft = _exportQueue.Count(q => q.Status == QueueItemStatus.Pending);
            var ordinal = _queueSessionDone + _queueSessionFailed + 1;

            entry.Status = QueueItemStatus.Running;
            entry.StatusHint = $"#{ordinal}";
            LblExportPhase.Text = $"Скачивание: {chat.Title}  (ещё ожидает: {pendingLeft})";
            AppendLog($"[{ordinal}] {chat.Title} → {outDir}");
            if (Directory.Exists(Path.Combine(outDir, "media")))
                AppendLog("  Докачка: существующие файлы будут пропущены.");
            RefreshQueueUi();

            return (chat, outDir, BuildExportOptions(), GetRetryCount());
        });

        var (chat, outDir, options, retries) = job;
        var progress = new Progress<ExportProgressReport>(r => Dispatcher.Invoke(() => ApplyExportProgress(r)));

        await _exportGate.WaitAsync(ct);
        try
        {
            await ExportRetryHelper.RunWithRetryAsync(async token =>
            {
                await Task.Run(() => ChatExportService.ExportChatToHtmlAsync(
                    _client, chat.Peer, chat.Title, outDir, options, progress, token), token);
            }, retries, ct, msg => Dispatcher.Invoke(() => AppendLog($"  {msg}")));

            await Dispatcher.InvokeAsync(() =>
            {
                var state = IncrementalStateService.Load(outDir);
                if (ExportCompletionTracker.IsFullyComplete(outDir, state, options))
                {
                    entry.Status = QueueItemStatus.Done;
                    entry.StatusHint = "";
                    _queueSessionDone++;
                    AppendLog($"  ✓ Готово: {chat.Title}");
                }
                else
                {
                    entry.Status = QueueItemStatus.Pending;
                    entry.StatusHint = "докачка";
                    AppendLog($"  ⚠ {chat.Title}: экспорт не завершён полностью — останется в очереди для докачки.");
                }
                RefreshQueueUi();
                PersistQueue();
            });
        }
        catch (OperationCanceledException)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                entry.Status = QueueItemStatus.Cancelled;
                entry.StatusHint = "";
                AppendLog("Скачивание остановлено.");
                RefreshQueueUi();
                PersistQueue();
            });
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                entry.Status = QueueItemStatus.Failed;
                entry.StatusHint = ex.Message.Length > 40 ? ex.Message[..40] + "…" : ex.Message;
                _queueSessionFailed++;
                AppendLog($"  ✗ Ошибка {chat.Title}: {ex.Message}");
                RefreshQueueUi();
                PersistQueue();
            });
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (ReferenceEquals(_activeQueueEntry, entry))
                    _activeQueueEntry = null;
            });
            _exportGate.Release();
        }
    }

    private void OnQueueIdleStopping()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(OnQueueIdleStopping); return; }
        AppendLog("Новых чатов в очереди нет — скачивание завершено.");
    }

    private void OnQueueWorkerFinished(Exception? fault)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => OnQueueWorkerFinished(fault)); return; }

        ExportProgressBar.IsIndeterminate = false;
        SetExporting(false);
        PanelQueueProgress.Visibility = Visibility.Collapsed;
        _activeQueueEntry = null;

        if (fault != null)
            AppendLog("Ошибка воркера очереди: " + fault.Message);

        LblExportPhase.Text = $"Очередь: {_queueSessionDone} готово" +
            (_queueSessionFailed > 0 ? $", {_queueSessionFailed} с ошибкой" : "");

        LoadHistory();
        if (_queueSessionDone > 0)
        {
            try { SystemSounds.Exclamation.Play(); } catch { /* */ }
        }

        AppendLog($"--- Итог сессии: {_queueSessionDone} успешно, {_queueSessionFailed} ошибок ---");
        NotifyExportComplete("TelegaScan",
            $"Очередь завершена: {_queueSessionDone} готово" + (_queueSessionFailed > 0 ? $", {_queueSessionFailed} ошибок" : ""));
        RefreshQueueUi();
    }

    // ─── История экспортов ───────────────────────────────────────
    private void LoadHistory()
    {
        var items = ExportHistoryService.Load()
            .Select(e => new HistoryDisplayItem(e))
            .ToList();
        ListHistory.ItemsSource = items;
    }

    private void ListHistory_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListHistory.SelectedItem is HistoryDisplayItem item && Directory.Exists(item.OutputPath))
            Process.Start("explorer.exe", item.OutputPath);
    }

    private void BtnOpenHistoryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ListHistory.SelectedItem is HistoryDisplayItem item && Directory.Exists(item.OutputPath))
            Process.Start("explorer.exe", item.OutputPath);
    }

    private async void BtnIntegrityCheck_Click(object sender, RoutedEventArgs e)
    {
        if (ListHistory.SelectedItem is not HistoryDisplayItem item) { AppendLog("Выберите запись в истории."); return; }
        if (!Directory.Exists(item.OutputPath)) { AppendLog("Папка не найдена."); return; }

        AppendLog($"Проверка целостности: {item.OutputPath}…");
        var state = IncrementalStateService.Load(item.OutputPath);
        if (state.FileHashes.Count == 0) { AppendLog("Нет данных для проверки (запустите экспорт заново)."); return; }

        var issues = await Task.Run(() => IncrementalStateService.CheckIntegrity(item.OutputPath, state));
        if (issues.Count == 0)
            AppendLog($"Целостность ОК: {state.FileHashes.Count} файлов проверено.");
        else
        {
            AppendLog($"Найдено проблем: {issues.Count}");
            foreach (var iss in issues.Take(20))
                AppendLog($"  {iss.RelPath}: {iss.Issue}");
        }
    }

    private void ResetExportProgressUi()
    {
        _mediaPhaseStartedUtc = null;
        LblExportPhase.Text = "Запуск…";
        LblExportPercent.Text = "";
        LblExportBytes.Text = "";
        LblExportSub.Text = "";
        LblExportEta.Visibility = Visibility.Collapsed;
        LblExportEta.Text = "";
        LblExportCurrentFile.Visibility = Visibility.Collapsed;
        ExportProgressBar.Value = 0;
        ResetSegmentBars(GridExportSegments, SegExportTextFill, SegExportMediaFill, SegExportAuxFill,
            LblSegExportText, LblSegExportMedia, LblSegExportAux);
        HideExportStatCards();
    }

    private static void ResetSegmentBars(Grid grid, Border textFill, Border mediaFill, Border auxFill,
        TextBlock lblText, TextBlock lblMedia, TextBlock lblAux)
    {
        textFill.Width = 0;
        mediaFill.Width = 0;
        auxFill.Width = 0;
        lblText.Text = "Текст 0%";
        lblMedia.Text = "Медиа 0%";
        lblAux.Text = "Файлы 0%";
    }

    private static void UpdateThreeSegmentProgress(
        Grid grid, Border textFill, Border mediaFill, Border auxFill,
        TextBlock lblText, TextBlock lblMedia, TextBlock lblAux,
        ExportProgressReport r)
    {
        grid.UpdateLayout();
        var w = grid.ActualWidth;
        if (w < 12) return;
        var seg = (w - 6) / 3.0;
        textFill.Width = seg * Math.Clamp(r.TextSegmentProgress, 0, 1);
        mediaFill.Width = seg * Math.Clamp(r.MediaSegmentProgress, 0, 1);
        auxFill.Width = seg * Math.Clamp(r.AuxSegmentProgress, 0, 1);
        lblText.Text = $"Текст {Math.Round(r.TextSegmentProgress * 100)}%";
        lblMedia.Text = $"Медиа {Math.Round(r.MediaSegmentProgress * 100)}%";
        lblAux.Text = $"Файлы {Math.Round(r.AuxSegmentProgress * 100)}%";
    }

    private void HideExportStatCards()
    {
        foreach (var b in new[] { CardStatPhotos, CardStatVideos, CardStatGifs, CardStatDocs, CardStatVoices, CardStatStickers, CardStatSkipped, CardStatMediaTotal })
            b.Visibility = Visibility.Collapsed;
    }

    private void ApplyExportProgress(ExportProgressReport r)
    {
        if (!string.IsNullOrEmpty(r.LogLine))
            AppendLog(r.LogLine);

        UpdateThreeSegmentProgress(GridExportSegments, SegExportTextFill, SegExportMediaFill, SegExportAuxFill,
            LblSegExportText, LblSegExportMedia, LblSegExportAux, r);

        if (_queueRunner.IsRunning && _activeQueueEntry != null)
        {
            PanelQueueProgress.Visibility = Visibility.Visible;
            var pct = r.ProgressIndeterminate ? -1 : (int)Math.Round(r.ProgressFraction * 100);
            var done = _exportQueue.Count(q => q.Status == QueueItemStatus.Done);
            var total = _exportQueue.Count(q => q.Status != QueueItemStatus.Cancelled);
            var ordinal = done + 1;
            var segHint = $"текст {Math.Round(r.TextSegmentProgress * 100)}% · медиа {Math.Round(r.MediaSegmentProgress * 100)}% · файлы {Math.Round(r.AuxSegmentProgress * 100)}%";
            LblQueueCurrentChat.Text = pct >= 0
                ? $"{_activeQueueEntry.Title}: {pct}% ({segHint})  ·  чат {ordinal}/{total}"
                : $"{_activeQueueEntry.Title}: {r.PhaseTitle}  ·  чат {ordinal}/{total}";
            UpdateThreeSegmentProgress(GridQueueSegments, SegQueueTextFill, SegQueueMediaFill, SegQueueAuxFill,
                LblSegQueueText, LblSegQueueMedia, LblSegQueueAux, r);
            _activeQueueEntry.StatusHint = pct >= 0 ? $"{pct}%" : "…";
        }

        var phaseTitle = _activeQueueEntry != null && _queueRunner.IsRunning
            ? $"{_activeQueueEntry.Title}: {r.PhaseTitle}"
            : r.PhaseTitle;
        LblExportPhase.Text = phaseTitle;

        ExportProgressBar.IsIndeterminate = r.ProgressIndeterminate;
        if (!r.ProgressIndeterminate)
            ExportProgressBar.Value = Math.Clamp(r.ProgressFraction * 100.0, 0, 100);

        LblExportPercent.Text = r.ProgressIndeterminate ? "…" : $"{Math.Round(r.ProgressFraction * 100)}%";

        if (r.BytesDownloaded > 0)
        {
            LblExportBytes.Text = FormatBinarySize(r.BytesDownloaded);
        }
        else if (r.Phase != ExportWorkPhase.Media)
        {
            LblExportBytes.Text = "";
        }

        LblExportSub.Text = r.Phase switch
        {
            ExportWorkPhase.History => $"Сообщений загружено: {r.MessagesLoadedCount:N0}",
            ExportWorkPhase.Participants when r.ParticipantTotal > 0 =>
                $"Этап: {r.ParticipantStep:N0} / {r.ParticipantTotal:N0}",
            ExportWorkPhase.Participants => "Загрузка списка участников…",
            ExportWorkPhase.Html => "Подготовка данных для HTML…",
            ExportWorkPhase.Media when r.MediaTotal > 0 =>
                $"Медиа: {r.MediaProcessed} из {r.MediaTotal}  (осталось {r.MediaTotal - r.MediaProcessed})",
            ExportWorkPhase.Media => "Подсчёт медиа…",
            ExportWorkPhase.Done => "Архив сохранён на диск",
            _ => ""
        };

        if (r.Phase == ExportWorkPhase.Media && r.MediaTotal > 0 && r.MediaProcessed > 0)
        {
            if (_mediaPhaseStartedUtc is null)
                _mediaPhaseStartedUtc = DateTime.UtcNow;

            var elapsed = (DateTime.UtcNow - _mediaPhaseStartedUtc.Value).TotalSeconds;
            if (elapsed > 2 && r.MediaProcessed < r.MediaTotal)
            {
                var rate = r.MediaProcessed / elapsed;
                if (rate > 0.02)
                {
                    var remaining = (r.MediaTotal - r.MediaProcessed) / rate;
                    if (remaining is > 3 and < 864000)
                    {
                        LblExportEta.Visibility = Visibility.Visible;
                        LblExportEta.Text = "≈ осталось: " + FormatRemaining(remaining);
                    }
                }
            }
        }
        else
        {
            LblExportEta.Visibility = Visibility.Collapsed;
            if (r.Phase != ExportWorkPhase.Media)
                _mediaPhaseStartedUtc = null;
        }

        if (!string.IsNullOrEmpty(r.CurrentFileHint))
        {
            LblExportCurrentFile.Visibility = Visibility.Visible;
            LblExportCurrentFile.Text = "Сейчас: " + r.CurrentFileHint;
        }
        else
        {
            LblExportCurrentFile.Visibility = Visibility.Collapsed;
        }

        var showStats = r.Phase is ExportWorkPhase.Media or ExportWorkPhase.Done;
        if (showStats && ((r.Photos + r.Videos + r.Gifs + r.Docs + r.Voices + r.Stickers + r.Skipped) > 0 || r.MediaTotal > 0))
        {
            SetStatCard(CardStatPhotos, LblStatPhotos, r.Photos);
            SetStatCard(CardStatVideos, LblStatVideos, r.Videos);
            SetStatCard(CardStatGifs, LblStatGifs, r.Gifs);
            SetStatCard(CardStatDocs, LblStatDocs, r.Docs);
            SetStatCard(CardStatVoices, LblStatVoices, r.Voices);
            SetStatCard(CardStatStickers, LblStatStickers, r.Stickers);
            SetStatCard(CardStatSkipped, LblStatSkipped, r.Skipped);
            if (r.MediaTotal > 0 && r.Phase == ExportWorkPhase.Media)
            {
                CardStatMediaTotal.Visibility = Visibility.Visible;
                LblStatMediaTotal.Text = $"{r.MediaProcessed} / {r.MediaTotal}";
            }
            else if (r.Phase == ExportWorkPhase.Done && r.MediaTotal > 0)
            {
                CardStatMediaTotal.Visibility = Visibility.Visible;
                LblStatMediaTotal.Text = $"{r.MediaProcessed} / {r.MediaTotal}";
            }
            else
                CardStatMediaTotal.Visibility = Visibility.Collapsed;
        }
        else if (r.Phase == ExportWorkPhase.History || r.Phase == ExportWorkPhase.Participants || r.Phase == ExportWorkPhase.Html)
        {
            HideExportStatCards();
        }
    }

    private static void SetStatCard(Border card, TextBlock label, int value)
    {
        if (value > 0)
        {
            card.Visibility = Visibility.Visible;
            label.Text = value.ToString("N0", CultureInfo.CurrentCulture);
        }
        else
            card.Visibility = Visibility.Collapsed;
    }

    private static string FormatBinarySize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} Б";
        double x = bytes / 1024.0;
        if (x < 1024) return $"{x:0.#} КБ";
        x /= 1024;
        if (x < 1024) return $"{x:0.#} МБ";
        return $"{x / 1024:0.#} ГБ";
    }

    private static string FormatRemaining(double seconds)
    {
        if (seconds >= 3600)
            return $"{(int)(seconds / 3600)} ч {(int)((seconds % 3600) / 60)} мин";
        if (seconds >= 120)
            return $"{(int)(seconds / 60)} мин";
        if (seconds >= 60)
            return "1–2 мин";
        return $"{Math.Max(5, (int)seconds)} с";
    }

    private const int MaxLogLines = 500;
    private int _logLineCount;

    private void AppendLog(string line)
    {
        _logLineCount++;
        // Обрезаем старые строки чтобы TextBox не разрастался бесконечно
        if (_logLineCount > MaxLogLines + 100)
        {
            var text = TxtLog.Text;
            var cut = 0;
            for (var i = 0; i < 100; i++)
            {
                var nl = text.IndexOf('\n', cut);
                if (nl < 0) break;
                cut = nl + 1;
            }
            TxtLog.Text = text[cut..];
            _logLineCount -= 100;
        }
        TxtLog.AppendText(line + Environment.NewLine);
        TxtLog.ScrollToEnd();
        _fileLog.Write(line);
    }

    protected override async void OnClosed(EventArgs e)
    {
        _schedulerTimer.Stop();
        _countEnrichmentCts?.Cancel();
        _queueRunner.Stop();
        _exportCts?.Cancel();
        _exportGate.Dispose();
        SaveSettingsFromUi();
        PersistQueue();
        _fileLog.Dispose();
        _toast.Dispose();
        if (_client != null) await _client.DisposeAsync();
        base.OnClosed(e);
    }
}
