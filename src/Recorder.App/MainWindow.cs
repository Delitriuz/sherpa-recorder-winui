using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.Storage.Pickers;
using Recorder.Core;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace Recorder.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<SavedSentence> sentences = new();
    private readonly WorkerClient worker = new();
    private readonly bool smoke;
    private NativeTray? tray;
    private DeviceWatcher? deviceWatcher;
    private Settings settings = new();
    private Message? lastMessage;
    private string currentSession = "";
    private long lastSequence;
    private bool recording, closing, forceExit, allowClose, disposed;
    private bool notificationsAvailable;
    private bool startPending;
    private bool followLatest = true;
    private ScrollViewer? transcriptScroll;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern uint GetDpiForWindow(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern nint GetWindowDpiAwarenessContext(nint hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);
    private TaskCompletionSource? stopped;
    private TaskCompletionSource? workerQuit;

    public MainWindow(bool smoke = false)
    {
        this.smoke = smoke;
        InitializeComponent();
        Title = "课堂记录 · WinUI";
        SystemBackdrop = new MicaBackdrop();
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
        var workArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new SizeInt32(Math.Min((int)(780 * scale), workArea.Width), Math.Min((int)(640 * scale), workArea.Height)));
        AppWindow.Changed += (_, e) =>
        {
            if (!e.DidSizeChange || AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized }) return;
            double dpiScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            int width = Math.Min((int)(600 * dpiScale), area.Width), height = Math.Min((int)(560 * dpiScale), area.Height);
            if (AppWindow.Size.Width < width || AppWindow.Size.Height < height)
                AppWindow.Resize(new SizeInt32(Math.Max(width, AppWindow.Size.Width), Math.Max(height, AppWindow.Size.Height)));
        };
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsMaximizable = true;
        try { settings = ProjectPaths.Load(); }
        catch (Exception ex) { ShowError($"配置读取失败：{ex.Message}"); }
        course.Text = settings.CourseName; stability.Value = settings.StabilitySeconds;
        folder.Text = ProjectPaths.Resolve(settings.RecordingDirectory); closeToTray.IsOn = settings.CloseToTray;
        theme.Items.Add("跟随系统"); theme.Items.Add("浅色"); theme.Items.Add("深色");
        theme.SelectedIndex = settings.Theme == "Light" ? 1 : settings.Theme == "Dark" ? 2 : 0;
        root.RequestedTheme = settings.Theme == "Light" ? ElementTheme.Light : settings.Theme == "Dark" ? ElementTheme.Dark : ElementTheme.Default;
        start.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        recover.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        Grid.SetColumn(recover, 0);
        partial.IsTextSelectionEnabled = true;
        file.IsTextSelectionEnabled = true;
        finalList.ItemsSource = sentences;
        navigation.SelectionChanged += (_, e) =>
        {
            string? page = (e.SelectedItem as NavigationViewItem)?.Tag as string;
            recordPage.Visibility = page == "record" ? Visibility.Visible : Visibility.Collapsed;
            historyPage.Visibility = page == "history" ? Visibility.Visible : Visibility.Collapsed;
            settingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
            if (page == "history") RefreshHistory();
        };
        navigation.SelectedItem = navigation.MenuItems[0];
        hideButton.Click += (_, _) => { if (tray is null) ShowError("托盘尚未就绪，请稍后再试。"); else AppWindow.Hide(); };
        latestButton.Click += (_, _) => { followLatest = true; if (sentences.Count > 0) finalList.ScrollIntoView(sentences[^1]); };
        finalList.Loaded += (_, _) =>
        {
            transcriptScroll = FindScroll(finalList);
            if (transcriptScroll is not null) transcriptScroll.ViewChanged += (_, _) =>
            {
                followLatest = transcriptScroll.ScrollableHeight - transcriptScroll.VerticalOffset < 48;
            };
        };
        devices.SelectionChanged += (_, _) => UpdateActions();
        refreshHistory.Click += (_, _) => RefreshHistory();
        openFolderButton.Click += (_, _) => OpenFolder();
        history.ItemClick += (_, e) => { if (e.ClickedItem is HistoryFile entry) Open(entry.Path); };
        chooseFolder.Click += async (_, _) =>
        {
            try { var result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync(); if (result is not null) { settings.RecordingDirectory = result.Path; folder.Text = result.Path; SaveSettings(); } }
            catch (Exception ex) { ShowError($"目录选择失败：{ex.Message}"); }
        };
        saveSettingsButton.Click += (_, _) => { if (SaveSettings()) { info.Severity = InfoBarSeverity.Success; info.Title = "设置已保存"; info.Message = "下次记录将使用这些设置。"; info.IsOpen = true; } };
        start.Click += async (_, _) => await Start();
        stop.Click += async (_, _) => await Stop();
        openFile.Click += (_, _) => Open(lastMessage?.FilePath);
        recover.Click += async (_, _) => await Recover();
        theme.SelectionChanged += (_, _) => { settings.Theme = theme.SelectedIndex == 1 ? "Light" : theme.SelectedIndex == 2 ? "Dark" : "Default"; root.RequestedTheme = settings.Theme == "Light" ? ElementTheme.Light : settings.Theme == "Dark" ? ElementTheme.Dark : ElementTheme.Default; SaveSettings(); };
        closeToTray.Toggled += (_, _) => { settings.CloseToTray = closeToTray.IsOn; SaveSettings(); };
        AppWindow.Closing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            await RequestClose();
        };
        Closed += (_, _) => { disposed = true; tray?.Dispose(); deviceWatcher?.Dispose(); worker.Dispose(); if (notificationsAvailable) AppNotificationManager.Default.Unregister(); };
        root.Loaded += async (_, _) =>
        {
            if (smoke) { await RunSmoke(); return; }
            worker.Received += message => DispatcherQueue.TryEnqueue(() => { if (!disposed) Update(message); });
            worker.ConnectionChanged += message => DispatcherQueue.TryEnqueue(() => { if (!disposed) { UpdateActions(); if (!worker.Connected) { stopped?.TrySetException(new IOException(message)); ShowError(message); } } });
            worker.Connect();
            await RefreshDevices();
            try { deviceWatcher = new DeviceWatcher(); deviceWatcher.Changed += () => DispatcherQueue.TryEnqueue(async () => { if (!disposed && !recording) await RefreshDevices(); }); }
            catch (Exception ex) { ShowError($"设备变化监听不可用：{ex.Message}"); }
            try { tray = new NativeTray(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowWindow, () => DispatcherQueue.TryEnqueue(async () => await Stop()), () => DispatcherQueue.TryEnqueue(async () => { forceExit = true; await RequestClose(); })); }
            catch (Exception ex) { ShowError($"托盘不可用：{ex.Message}"); }
            try { AppNotificationManager.Default.Register(); notificationsAvailable = true; } catch { notificationsAvailable = false; }
            if (Environment.GetCommandLineArgs().Contains("--desktop-test")) await RunDesktopTest();
        };
    }

    private async Task RefreshDevices()
    {
        try
        {
            var list = await Task.Run(NativeAudioCapture.Devices);
            if (disposed || recording) return;
            devices.ItemsSource = list;
            devices.SelectedItem = list.FirstOrDefault(device => device.Id == settings.DeviceId);
            if (devices.SelectedItem is null) { devices.PlaceholderText = "原麦克风不可用，请重新选择"; start.IsEnabled = false; ShowError("已保存的麦克风不在活动设备列表中，请重新选择。"); }
        }
        catch (Exception ex) { ShowError($"麦克风枚举失败：{ex.Message}"); }
    }
    private bool SaveSettings()
    {
        if (smoke) return true;
        try { settings.CourseName = course.Text.Trim(); if (devices.SelectedItem is AudioDevice selected) settings.DeviceId = selected.Id; if (!double.IsNaN(stability.Value)) settings.StabilitySeconds = Math.Clamp(stability.Value, 0.5, 5); ProjectPaths.Save(settings); return true; }
        catch (Exception ex) { ShowError($"设置保存失败：{ex.Message}"); return false; }
    }
    private async Task Start()
    {
        if (startPending || recording || !worker.Connected) return;
        if (lastMessage?.PendingText?.Length > 0) { ShowError("尚有未确认保存的文字，请先另存并核对原记录后再开始。"); return; }
        if (devices.SelectedItem is not AudioDevice selected) { ShowError("请先选择可用麦克风。"); return; }
        if (ProjectPaths.LegacyRecordingActive()) { ShowError("旧版正在录音，请结束本次录音后再使用 WinUI 版。原进程未被操作。"); return; }
        try
        {
            settings.CourseName = string.IsNullOrWhiteSpace(course.Text) ? "English class" : course.Text.Trim(); settings.DeviceId = selected.Id;
            settings.StabilitySeconds = double.IsNaN(stability.Value) ? 1.8 : Math.Clamp(stability.Value, 0.5, 5);
            ProjectPaths.Save(settings); startPending = true; UpdateActions(); info.IsOpen = false;
            await worker.Send(new("Start", settings));
        }
        catch (Exception ex) { startPending = false; ShowError(ex.Message); UpdateActions(); }
    }
    private async Task Stop()
    {
        if (!recording) return;
        try { if (stopped is null || stopped.Task.IsCompleted) stopped = new(TaskCreationOptions.RunContinuationsAsynchronously); await worker.Send(new("Stop")); await stopped.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        catch (Exception ex) { ShowError($"停止请求失败，录音没有被强杀：{ex.Message}"); }
    }
    private async Task RequestClose()
    {
        if (closing) return;
        if (!forceExit && settings.CloseToTray && tray is not null) { AppWindow.Hide(); return; }
        closing = true;
        try
        {
            if (recording) await Stop();
            if (recording) { closing = false; return; }
            if (lastMessage?.PendingText?.Length > 0)
            {
                var dialog = new ContentDialog { XamlRoot = root.XamlRoot, Title = "还有未确认保存的文字", Content = "请先另存并核对原文件。直接退出会丢失工作进程中保留的文字。", PrimaryButtonText = "另存文字", CloseButtonText = "继续保留" };
                if (await dialog.ShowAsync() == ContentDialogResult.Primary) await Recover();
                closing = false; return;
            }
            if (worker.Connected)
            {
                workerQuit = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await worker.Send(new("Quit"));
                await workerQuit.Task.WaitAsync(TimeSpan.FromSeconds(60));
            }
            allowClose = true; Close();
        }
        catch (Exception ex) { closing = false; ShowError($"未能正常退出：{ex.Message}"); }
    }
    private async Task Recover()
    {
        try { var result = await new FolderPicker(AppWindow.Id).PickSingleFolderAsync(); if (result is not null) await worker.Send(new("Recover", Path: result.Path)); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void Update(Message message)
    {
        if (message.Type == "Quit") workerQuit?.TrySetResult();
        if (message.Sequence <= lastSequence && message.SessionId == currentSession) return;
        if (message.SessionId != currentSession)
        {
            if (currentSession.Length > 0 && message.SessionId.Length == 0) ShowError("记录服务已重新启动。之前保存的文本可在历史中查看，请确认后重新开始。");
            sentences.Clear(); currentSession = message.SessionId; stopped = null; followLatest = true;
        }
        lastSequence = message.Sequence; lastMessage = message;
        startPending = false;
        recording = message.State is "Loading" or "Recording" or "Stopping";
        start.IsEnabled = worker.Connected && !recording;
        stop.IsEnabled = recording && message.State != "Stopping";
        course.IsEnabled = devices.IsEnabled = stability.IsEnabled = !recording;
        loading.IsActive = message.State is "Preparing" or "Loading"; loading.Visibility = loading.IsActive ? Visibility.Visible : Visibility.Collapsed;
        status.Text = message.State switch { "Preparing" => "正在加载模型…", "Loading" => "正在启动录音…", "Recording" => "正在记录", "Stopping" => "正在保存尾句…", "Stopped" => "已停止并保存", "Error" => message.ModelReady ? "记录失败" : "模型加载失败", _ => "准备就绪" };
        var duration = TimeSpan.FromSeconds(message.AudioSeconds); elapsed.Text = $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        if (message.Type == "Level") volume.Value = message.Level;
        if (message.Sentences is not null)
        {
            long lastId = sentences.Count == 0 ? 0 : sentences[^1].Id;
            foreach (SavedSentence sentence in message.Sentences.Where(sentence => sentence.Id > lastId)) sentences.Add(sentence);
        }
        savedCount.Text = $"{sentences.Count} 句";
        emptyRecord.Visibility = sentences.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (message.Sentences is not null && followLatest && sentences.Count > 0) finalList.ScrollIntoView(sentences[^1]);
        if (message.Type == "Snapshot" && message.Text.Length == 0) { partial.Text = recording ? "正在聆听…" : "准备好后，开始记录这堂课。"; partialHeading.Text = "实时识别 · 成句后自动保存"; }
        if (message.Type == "Partial" || message.Type == "Snapshot" && message.Text.Length > 0 && message.State != "Error") { partial.Text = message.Text; partialHeading.Text = "实时识别 · 正在整理"; DispatcherQueue.TryEnqueue(() => partialScroll.ChangeView(null, partialScroll.ScrollableHeight, null, true)); }
        if (message.Type == "Final" && sentences.Count > 0) { partial.Text = sentences[^1].Text; partialHeading.Text = "实时识别 · 此句已保存"; }
        if (message.State == "Preparing") { partial.Text = "正在准备识别，加载完成后即可开始。"; partialHeading.Text = "加载模型 · 尚未录音"; }
        if (message.Type == "ModelReady") { partial.Text = "准备好后，开始记录这堂课。"; partialHeading.Text = "实时识别 · 成句后自动保存"; }
        if (message.State == "Loading") { partial.Text = "正在启动麦克风…"; partialHeading.Text = "实时识别 · 等待讲话"; }
        file.Text = message.FilePath.Length > 0 ? Path.GetFileName(message.FilePath) : "每次课堂会自动保存一份文本记录";
        ToolTipService.SetToolTip(file, message.FilePath.Length > 0 ? message.FilePath : file.Text);
        openFile.IsEnabled = message.FilePath.Length > 0 && File.Exists(message.FilePath);
        recover.Visibility = message.PendingText?.Length > 0 && message.State == "Error" ? Visibility.Visible : Visibility.Collapsed;
        if (message.State == "Error")
        {
            ShowError(message.Text);
            if (message.PendingText?.Length > 0) { partial.Text = message.PendingText; partialHeading.Text = "未确认保存的文字 · 请另存并核对原文件"; }
            if (message.Type == "Error") NotifyError(message.Text);
        }
        if (message.Type == "Recovered") { info.Severity = InfoBarSeverity.Success; info.Title = "文字已另存"; info.Message = message.Text; info.IsOpen = true; partialHeading.Text = "已另存 · 请核对原记录"; status.Text = "文字已另存"; }
        if (!recording) { volume.Value = 0; stopped?.TrySetResult(); }
        inputGrid.Visibility = message.PendingText?.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        savedPanel.Visibility = message.PendingText?.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        UpdateActions();
    }
    private void RefreshHistory()
    {
        string directory = ProjectPaths.Resolve(settings.RecordingDirectory);
        try
        {
            history.ItemsSource = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.txt").Where(path => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(path), @"^(\d{8}-\d{6}-.+-[0-9a-f]{8}|recovered-\d{8}-\d{6}-[0-9a-f]{32})\.txt$")).OrderByDescending(File.GetLastWriteTime).Select(path => new HistoryFile(path, HistoryTitle(path), $"{File.GetLastWriteTime(path):yyyy年M月d日 HH:mm} · {Math.Max(1, new FileInfo(path).Length / 1024)} KB")).ToArray() : [];
            emptyHistory.Visibility = history.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { ShowError($"历史记录读取失败：{ex.Message}"); }
    }
    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch (Exception ex) { ShowError($"无法打开：{ex.Message}"); }
    }
    private void OpenFolder() { try { string directory = ProjectPaths.Resolve(settings.RecordingDirectory); Directory.CreateDirectory(directory); Open(directory); } catch (Exception ex) { ShowError(ex.Message); } }
    public void ShowWindow() { DispatcherQueue.TryEnqueue(() => { AppWindow.Show(); Activate(); }); }
    private void ShowError(string text) { info.Severity = InfoBarSeverity.Error; info.Title = "需要处理"; info.Message = text; info.IsOpen = true; }
    private void NotifyError(string text)
    {
        if (!notificationsAvailable) return;
        try { AppNotificationManager.Default.Show(new AppNotification($"<toast><visual><binding template='ToastGeneric'><text>课堂记录发生错误</text><text>{SecurityElement.Escape(text)}</text></binding></visual></toast>")); } catch { }
    }
    private void UpdateActions()
    {
        start.IsEnabled = (worker.Connected || smoke) && (smoke || lastMessage?.ModelReady == true) && lastMessage?.State != "Preparing" && !recording && !startPending && devices.SelectedItem is AudioDevice && lastMessage?.PendingText?.Length is not > 0;
        start.Content = lastMessage?.State == "Preparing" ? "正在加载模型…" : "开始记录";
        start.Visibility = recording || lastMessage?.PendingText?.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
        stop.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        stop.IsEnabled = (worker.Connected || smoke) && recording && lastMessage?.State != "Stopping";
    }
    private static ScrollViewer? FindScroll(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private sealed record HistoryFile(string Path, string Name, string Detail);
    private static string HistoryTitle(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        var match = System.Text.RegularExpressions.Regex.Match(name, @"^\d{8}-\d{6}-(.+)-[0-9a-f]{8}$");
        return match.Success ? match.Groups[1].Value : name.StartsWith("recovered-") ? "另存的课堂记录" : name;
    }

    private async Task RunSmoke()
    {
        try
        {
            await RefreshDevices();
            using var testTray = new NativeTray(WinRT.Interop.WindowNative.GetWindowHandle(this), ShowWindow, () => { }, () => { });
            using var testWatcher = new DeviceWatcher();
            AppWindow.Hide(); AppWindow.Show();
            if (devices.Items.Count == 0) throw new InvalidOperationException("原生设备枚举没有返回结果。");
            Directory.CreateDirectory(ProjectPaths.Resolve("tests/results/ui-v3"));
            root.RequestedTheme = ElementTheme.Light;
            Update(new("Snapshot", "", 1, "Preparing"));
            if (start.IsEnabled || stop.Visibility != Visibility.Collapsed || !loading.IsActive) throw new InvalidOperationException("启动预加载状态错误地允许录音。");
            await CapturePreview("model-loading.png");
            Update(new("ModelReady", "", 2, "Idle", ModelReady: true, ModelLoads: 1));
            if (!start.IsEnabled || loading.IsActive) throw new InvalidOperationException("模型就绪后开始按钮没有恢复。");
            Update(new("Partial", "smoke", 1, "Recording", "The lecturer is explaining the engineering design."));
            Update(new("Final", "smoke", 2, "Recording", Sentences: [new(1, "00:00:08", "The lecturer is explaining the engineering design.")]));
            Update(new("Final", "smoke", 3, "Recording", Sentences: [new(1, "00:00:08", "The lecturer is explaining the engineering design."), new(2, "00:00:15", "The lecturer is explaining the engineering design.")]));
            Update(new("Partial", "smoke", 4, "Recording", "This sentence is still being corrected.", AudioSeconds: 19));
            if (sentences.Count != 2 || partial.Text != "This sentence is still being corrected.") throw new InvalidOperationException("消息去重或实时更新失败。");
            if (start.Visibility != Visibility.Collapsed || stop.Visibility != Visibility.Visible || !stop.IsEnabled) throw new InvalidOperationException("录音状态未切换到停止操作。");
            Directory.CreateDirectory(ProjectPaths.Resolve("tests/results/ui-v3"));
            root.RequestedTheme = ElementTheme.Light;
            await CapturePreview("record-light.png");
            root.RequestedTheme = ElementTheme.Dark;
            await CapturePreview("record-dark.png");
            Update(new("Snapshot", "smoke", 5, "Stopped", AudioSeconds: 19));
            if (stop.Visibility != Visibility.Collapsed || start.Visibility != Visibility.Visible) throw new InvalidOperationException("停止状态按钮未恢复。");
            await CapturePreview("stopped-dark.png");
            double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96d;
            AppWindow.Resize(new SizeInt32((int)(600 * scale), (int)(560 * scale)));
            Update(new("Error", "smoke", 6, "Error", "无法写入记录文件。请检查保存位置，或将待确认文字另存。", PendingText: "The final sentence is retained until you choose a new location.", ModelReady: true));
            await CapturePreview("error-compact.png");
            double recoveryBottom = recover.TransformToVisual(navigation).TransformPoint(new Windows.Foundation.Point(0, recover.ActualHeight)).Y;
            if (recover.ActualHeight <= 0 || recoveryBottom > navigation.ActualHeight || start.IsEnabled) throw new InvalidOperationException("紧凑窗口恢复操作不可见或错误地允许重新录音。");
            info.IsOpen = false;
            navigation.SelectedItem = navigation.MenuItems[2];
            await CapturePreview("settings-compact.png");
            navigation.SelectedItem = navigation.MenuItems[1];
            await CapturePreview("history-compact.png");
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            bool perMonitor = AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new nint(-4));
            if (!perMonitor) throw new InvalidOperationException("窗口没有使用 PerMonitorV2 DPI awareness。");
            File.WriteAllText(ProjectPaths.Resolve("tests/results/ui-v3/ui-smoke.txt"), $"PASS native XAML, tray, notifications, live revisions, 2 repeated finals, stopped state, compact error recovery visible, settings/history navigation. DPI PerMonitorV2={perMonitor}; WindowDpi={GetDpiForWindow(hwnd)}; RasterizationScale={root.XamlRoot.RasterizationScale}; default=780x640 DIP; minimum=600x560 DIP. No worker or microphone started; configuration not changed.");
        }
        catch (Exception ex) { Directory.CreateDirectory(ProjectPaths.Resolve("tests/results/ui-v3")); File.WriteAllText(ProjectPaths.Resolve("tests/results/ui-v3/ui-smoke.txt"), ex.ToString()); }
        finally { recording = false; allowClose = true; Close(); }
    }
    private async Task CapturePreview(string name)
    {
        await Task.Delay(500);
        var bitmap = new RenderTargetBitmap(); await bitmap.RenderAsync(root);
        byte[] pixels = (await bitmap.GetPixelsAsync()).ToArray();
        StorageFolder results = await StorageFolder.GetFolderFromPathAsync(ProjectPaths.Resolve("tests/results/ui-v3"));
        StorageFile output = await results.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
        using var stream = await output.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels); await encoder.FlushAsync();
    }
    private async Task RunDesktopTest()
    {
        Directory.CreateDirectory(ProjectPaths.Resolve("tests/results/ui-v3"));
        string report = ProjectPaths.Resolve("tests/results/ui-v3/desktop-test.txt");
        try
        {
            for (int i = 0; i < 600 && (!worker.Connected || lastMessage?.ModelReady != true); i++) await Task.Delay(100);
            if (!worker.Connected || lastMessage?.State != "Idle" || lastMessage.ModelReady != true || !start.IsEnabled || tray is null || deviceWatcher is null)
                throw new InvalidOperationException("正常启动的工作进程、空闲状态、托盘或设备通知未就绪。");
            using Process duplicate = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false })!;
            await duplicate.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (duplicate.ExitCode != 0) throw new InvalidOperationException("UI 第二实例未正常重定向退出。");
            AppWindow.Hide(); ShowWindow(); await Task.Delay(300);
            File.WriteAllText(report, $"PASS normal WinUI startup automatically loads model before Start; ModelReady={lastMessage.ModelReady}; ModelLoads={lastMessage.ModelLoads}; start enabled after loading; native tray, UI single-instance redirect and hide/restore; microphone not started. Normal window close requested.");
        }
        catch (Exception ex) { File.WriteAllText(report, ex.ToString()); }
        finally { forceExit = true; await RequestClose(); }
    }
}
