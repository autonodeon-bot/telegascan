using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
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
    private readonly ObservableCollection<string> _queueItems = new();
    private readonly List<DialogListItem> _exportQueue = new();
    private readonly string _sessionPath;

    private Client? _client;
    private CancellationTokenSource? _exportCts;
    private bool _busy;
    private DateTime? _mediaPhaseStartedUtc;

    public MainWindow()
    {
        InitializeComponent();
        _sessionPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TelegaScan",
            "WTelegram.session");

        TxtApiId.Text = _settings.ApiId;
        TxtApiHash.Text = _settings.ApiHash;
        TxtPhone.Text = _settings.PhoneNumber;
        TxtExportFolder.Text = string.IsNullOrEmpty(_settings.LastExportFolder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "TelegaScanExports")
            : _settings.LastExportFolder;
        ListChats.ItemsSource = _chatItems;
        CollectionViewSource.GetDefaultView(_chatItems).Filter = ChatFilter;
        ListQueue.ItemsSource = _queueItems;

        SliderHistoryDelay.ValueChanged += (_, _) => LblHistoryDelay.Text = $"{(int)SliderHistoryDelay.Value} мс";
        SliderMediaDelay.ValueChanged += (_, _) => LblMediaDelay.Text = $"{(int)SliderMediaDelay.Value} мс";
        SliderParallel.ValueChanged += (_, _) => LblParallel.Text = $"{(int)SliderParallel.Value}";

        // Горячие клавиши
        KeyDown += MainWindow_KeyDown;

        // Загружаем историю
        LoadHistory();

        Helpers.Log = (level, msg) => System.Diagnostics.Debug.WriteLine($"[WTelegram {level}] {msg}");
    }

    private bool ChatFilter(object obj) =>
        obj is DialogListItem d && (TxtFilter.Text.Trim().Length == 0 || d.Title.Contains(TxtFilter.Text.Trim(), StringComparison.CurrentCultureIgnoreCase));

    private void TxtFilter_OnTextChanged(object sender, TextChangedEventArgs e) =>
        CollectionViewSource.GetDefaultView(_chatItems).Refresh();

    private void SaveSettingsFromUi()
    {
        _settings.ApiId = TxtApiId.Text.Trim();
        _settings.ApiHash = TxtApiHash.Text.Trim();
        _settings.PhoneNumber = TxtPhone.Text.Trim();
        _settings.LastExportFolder = TxtExportFolder.Text.Trim();
        _settings.Save();
    }

    private void Credentials_OnLostFocus(object sender, RoutedEventArgs e) => SaveSettingsFromUi();
    // ───── Подключение через client.Login() — без колбэков с фоновых потоков ─────

    private async void BtnConnect_OnClick(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        await DoConnect();
    }

    /// <summary>
    /// Основной метод подключения. Использует client.Login() — возвращает строку,
    /// говорящую, что ещё нужно (номер, код, пароль…). Мы отдаём управление обратно
    /// в UI и ждём ввода — никаких Dispatcher.Invoke с фоновых потоков.
    /// </summary>
    private async Task DoConnect()
    {
        if (_busy) return;
        _busy = true;
        BtnConnect.IsEnabled = false;

        if (!int.TryParse(TxtApiId.Text.Trim(), out var apiId))
        {
            TxtStatus.Text = "api_id должен быть числом.";
            BtnConnect.IsEnabled = true;
            _busy = false;
            return;
        }

        var apiHash = TxtApiHash.Text.Trim();
        var phone = TxtPhone.Text.Trim();
        if (string.IsNullOrWhiteSpace(apiHash) || string.IsNullOrWhiteSpace(phone))
        {
            TxtStatus.Text = "Заполните api_hash и телефон.";
            BtnConnect.IsEnabled = true;
            _busy = false;
            return;
        }

        try
        {
            var old = _client;
            _client = null;
            if (old != null) await old.DisposeAsync();

            var dir = Path.GetDirectoryName(_sessionPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            TxtStatus.Text = "Подключение к серверам Telegram…";

            string MinimalConfig(string what) => what switch
            {
                "api_id" => apiId.ToString(),
                "api_hash" => apiHash,
                "session_pathname" => _sessionPath,
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

            if (dialogs.Count > 0)
                TxtStatus.Text = $"{userName} — {dialogs.Count} диалогов. Выберите чат.";
            BtnDisconnect.Visibility = Visibility.Visible;
            BtnExport.IsEnabled = ListChats.SelectedItem != null;
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
            _busy = false;
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
        TxtSelectedChat.Text = ListChats.SelectedItem is DialogListItem d ? "Выбран: " + d.Title : "Чат не выбран";
        BtnExport.IsEnabled = _client != null && ListChats.SelectedItem != null;
    }

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

    private async void BtnExport_OnClick(object sender, RoutedEventArgs e)
    {
        if (_client is null || ListChats.SelectedItem is not DialogListItem chat) return;
        var folderRoot = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(folderRoot)) { AppendLog("Укажите папку экспорта."); return; }

        Directory.CreateDirectory(folderRoot);
        var outDir = Path.Combine(folderRoot, SanitizeName(chat.Title));

        var hasExisting = Directory.Exists(Path.Combine(outDir, "media"));
        if (hasExisting)
            AppendLog("Папка уже существует — докачка: существующие файлы будут пропущены.");

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

        var isChannel = RbChannel.IsChecked == true;
        var options = new ExportOptions
        {
            MessagesPerPage = perPage,
            DelayBetweenHistoryRequests = TimeSpan.FromMilliseconds((int)SliderHistoryDelay.Value),
            DelayAfterEachMediaFile = TimeSpan.FromMilliseconds((int)SliderMediaDelay.Value),
            HistoryBatchSize = 100,
            Quality = quality,
            Mode = isChannel ? ExportMode.Channel : ExportMode.Chat,
            ParallelDownloads = (int)SliderParallel.Value,
            FromDate = DpFrom.SelectedDate,
            ToDate = DpTo.SelectedDate,
            DownloadPhotos    = ChkPhotos.IsChecked == true,
            DownloadVideos    = ChkVideos.IsChecked == true,
            DownloadDocuments = ChkDocuments.IsChecked == true,
            DownloadVoice     = ChkVoice.IsChecked == true,
            DownloadStickers  = ChkStickers.IsChecked == true,
            Incremental       = ChkIncremental.IsChecked == true,
            ExportJson        = ChkExportJson.IsChecked == true,
            ExportSqlite      = ChkExportSqlite.IsChecked == true,
            GenerateStatistics = ChkStatistics.IsChecked == true,
            SaveLongTexts     = ChkLongTexts.IsChecked == true,
            GroupAlbums       = ChkAlbums.IsChecked == true,
            GroupThreads      = ChkThreads.IsChecked == true,
            TrackForwarded    = ChkForwarded.IsChecked == true,
            DeduplicateMedia  = ChkDedup.IsChecked == true,
            CreateZip         = ChkZip.IsChecked == true
        };

        _exportCts = new CancellationTokenSource();
        BtnExport.IsEnabled = false;
        BtnCancelExport.IsEnabled = true;
        ResetExportProgressUi();
        ExportProgressBar.IsIndeterminate = true;
        AppendLog("--- Старт экспорта ---");

        var progress = new Progress<ExportProgressReport>(r => Dispatcher.Invoke(() => ApplyExportProgress(r)));
        var success = false;
        try
        {
            await Task.Run(() => ChatExportService.ExportChatToHtmlAsync(_client, chat.Peer, chat.Title, outDir, options, progress, _exportCts.Token));
            success = true;
        }
        catch (OperationCanceledException) { AppendLog("Экспорт остановлен."); }
        catch (Exception ex) { AppendLog("Ошибка: " + ex.Message); }
        finally
        {
            ExportProgressBar.IsIndeterminate = false;
            BtnCancelExport.IsEnabled = false;
            BtnExport.IsEnabled = _client != null && ListChats.SelectedItem != null;
            BtnPreview.IsEnabled = BtnExport.IsEnabled;
        }

        if (success)
        {
            LoadHistory();
            try { SystemSounds.Exclamation.Play(); } catch { /* */ }
            AppendLog($"Файлы: {outDir}");
        }
    }

    private void BtnCancelExport_OnClick(object sender, RoutedEventArgs e) => _exportCts?.Cancel();

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
            var (sorted, _, _) = await Task.Run(() =>
                ChatExportService.LoadAllMessagesInternalAsync(_client, chat.Peer, opts, null, CancellationToken.None));
            var mediaCount = sorted.Count(m => m is TL.Message { media: not null });
            AppendLog($"Предпросмотр: {sorted.Count} сообщений, ~{mediaCount} с медиа");
        }
        catch (Exception ex) { AppendLog("Предпросмотр: " + ex.Message); }
        finally { BtnPreview.IsEnabled = _client != null && ListChats.SelectedItem != null; }
    }

    // ─── Очистить даты ───────────────────────────────────────────
    private void BtnClearDates_Click(object sender, RoutedEventArgs e)
    {
        DpFrom.SelectedDate = null;
        DpTo.SelectedDate = null;
    }

    // ─── Очередь экспорта ────────────────────────────────────────
    private void BtnAddToQueue_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in ListChats.SelectedItems.OfType<DialogListItem>())
        {
            if (_exportQueue.All(q => q.Title != item.Title))
            {
                _exportQueue.Add(item);
                _queueItems.Add(item.Title);
            }
        }
    }

    private void BtnClearQueue_Click(object sender, RoutedEventArgs e)
    {
        _exportQueue.Clear();
        _queueItems.Clear();
    }

    private async void BtnRunQueue_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null || _exportQueue.Count == 0) return;
        if (_busy) { AppendLog("Уже выполняется экспорт."); return; }

        var folderRoot = TxtExportFolder.Text.Trim();
        if (string.IsNullOrEmpty(folderRoot)) { AppendLog("Укажите папку."); return; }

        var qualityTag = ((ComboBoxItem)CmbMediaQuality.SelectedItem).Tag?.ToString() ?? "original";
        var quality = qualityTag switch { "compressed" => MediaQuality.Compressed, "thumbs" => MediaQuality.Thumbs, "none" => MediaQuality.None, _ => MediaQuality.Original };
        var baseOptions = new ExportOptions
        {
            DelayBetweenHistoryRequests = TimeSpan.FromMilliseconds((int)SliderHistoryDelay.Value),
            DelayAfterEachMediaFile = TimeSpan.FromMilliseconds((int)SliderMediaDelay.Value),
            ParallelDownloads = (int)SliderParallel.Value,
            Quality = quality,
            Mode = RbChannel.IsChecked == true ? ExportMode.Channel : ExportMode.Chat,
            Incremental = ChkIncremental.IsChecked == true,
            ExportJson = ChkExportJson.IsChecked == true,
            ExportSqlite = ChkExportSqlite.IsChecked == true,
            GenerateStatistics = ChkStatistics.IsChecked == true,
        };

        _busy = true;
        BtnRunQueue.IsEnabled = false;
        _exportCts = new CancellationTokenSource();
        var queue = _exportQueue.ToList();
        AppendLog($"--- Очередь: {queue.Count} чатов ---");

        var progress = new Progress<ExportProgressReport>(r => Dispatcher.Invoke(() => ApplyExportProgress(r)));
        foreach (var chat in queue)
        {
            if (_exportCts.IsCancellationRequested) break;
            AppendLog($"Экспорт: {chat.Title}");
            var outDir = Path.Combine(folderRoot, SanitizeName(chat.Title));
            try
            {
                await Task.Run(() => ChatExportService.ExportChatToHtmlAsync(
                    _client, chat.Peer, chat.Title, outDir, baseOptions, progress, _exportCts.Token));
                AppendLog($"  Готово: {chat.Title}");
            }
            catch (OperationCanceledException) { AppendLog("Очередь остановлена."); break; }
            catch (Exception ex) { AppendLog($"  Ошибка {chat.Title}: {ex.Message}"); }
        }

        _busy = false;
        BtnRunQueue.IsEnabled = true;
        LoadHistory();
        try { SystemSounds.Exclamation.Play(); } catch { /* */ }
        AppendLog("--- Очередь завершена ---");
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
        HideExportStatCards();
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

        LblExportPhase.Text = r.PhaseTitle;

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
                $"Медиафайлов обработано: {r.MediaProcessed} из {r.MediaTotal}",
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
    }

    private static string SanitizeName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return string.IsNullOrWhiteSpace(n) ? "chat" : n.Trim(); }

    protected override async void OnClosed(EventArgs e) { SaveSettingsFromUi(); if (_client != null) await _client.DisposeAsync(); base.OnClosed(e); }
}
