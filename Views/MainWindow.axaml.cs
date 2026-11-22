using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using AvaloniaEdit.Search;
using Tatami.Models;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private sealed record BuildDiagnostic(string Path, int Line, string Message);
    private static readonly Regex ParenthesizedDiagnostic = MyRegex();
    private static readonly Regex ColonDiagnostic = new(@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.md):(?<line>\d+)(?::\d+)?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private string? _notesPath;
    private string? _outputPath;
    private readonly WorkspaceSettings _settings = new();
    private readonly WorkspaceRecoveryStore _recoveryStore = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _livePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _externalChangeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<OpenNoteTab, (string Hash, string? Text, bool Exists)> _externalChanges = [];
    private readonly List<BuildDiagnostic> _diagnostics = [];
    private NativeWebView? _webView;
    private bool _refreshingPages;
    private bool _livePreviewActive;
    private string _builder = "builtin";
    private ExternalBuildOptions _external = new();
    private CancellationTokenSource? _buildCancellation;
    private readonly PreviewServer _previewServer = new();
    private SiteConfig CurrentConfig => new(_notesPath ?? "", _outputPath ?? "") { Builder = _builder, External = _external };
    private readonly List<OpenNoteTab> _tabs = [];
    private readonly Dictionary<OpenNoteTab, TextBlock> _tabLabels = [];
    private readonly Dictionary<OpenNoteTab, Border> _tabBorders = [];
    private OpenNoteTab? _activeTab;
    private EditorBuffer? _buffer;
    private WorkspaceEntry? _selected;
    private bool _busy;
    private bool _changingSelection;
    private bool _suppressMetadataChanges;
    private bool _metadataPending;
    private bool _recoveryDeferred;
    private bool _recoveryInProgress;
    private bool _recoveryErrorLogged;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        SearchPanel.Install(Editor);
        using (var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Tatami/Assets/Markdown.xshd")))
        using (var reader = XmlReader.Create(stream))
            Editor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
        Editor.TextChanged += (_, _) =>
        {
            if (_buffer != null)
            {
                if (!string.Equals(_buffer.Text, Editor.Text, StringComparison.Ordinal)) _recoveryDeferred = false;
                _buffer.Text = Editor.Text;
            }
            if (_buffer != null && !_suppressMetadataChanges && !_metadataPending)
                LoadMetadataSettings(Editor.Text);
            UpdateBufferStatus();
            if (PreviewPane.IsVisible) RequestLivePreviewRefresh();
        };
        MetadataPublish.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(MetadataPublish.IsChecked)) MetadataSettingChanged(MetadataPublish, EventArgs.Empty);
        };
        MetadataTitle.TextChanged += MetadataSettingChanged;
        MetadataSlug.TextChanged += MetadataSettingChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
            CursorStatus.Text = $"行 {Editor.TextArea.Caret.Line}, 列 {Editor.TextArea.Caret.Column}";
        AddHandler(KeyDownEvent, HandleShortcut, RoutingStrategies.Tunnel);
        Closing += OnClosing;
        _recoveryTimer.Tick += async (_, _) => await BackupTickAsync();
        _recoveryTimer.Start();
        _livePreviewTimer.Tick += async (_, _) => await UpdateLivePreviewAsync();
        _externalChangeTimer.Tick += async (_, _) => await CheckExternalChangesAsync();
        _externalChangeTimer.Start();
        Closed += async (_, _) => { _recoveryTimer.Stop(); _livePreviewTimer.Stop(); _externalChangeTimer.Stop(); await _previewServer.DisposeAsync(); };
        Opened += async (_, _) => await RunAsync(RestoreWorkspaceAsync);
    }

    private async void HandleShortcut(object? sender, KeyEventArgs e)
    {
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.Key == Key.N && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            await RunAsync(CreateNoteAsync);
            return;
        }
        if (e.Key == Key.S && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            await RunAsync(SaveCurrentAsync);
            return;
        }
        if (e.Key == Key.P && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            await RunAsync(() => FindNotesAsync(searchContents: false));
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers == (modifier | KeyModifiers.Shift))
        {
            e.Handled = true;
            await RunAsync(() => FindNotesAsync(searchContents: true));
        }
    }

    private async Task FindNotesAsync(bool searchContents)
    {
        if (_notesPath == null)
        {
            Log("先にメモのディレクトリを開いてください。", true);
            return;
        }
        ApplyMetadataSettings();
        var entries = await Task.Run(() => WorkspaceEntry.Read(_notesPath));
        var paths = new List<string>();
        void AddFiles(IEnumerable<WorkspaceEntry> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) AddFiles(node.Children);
                else paths.Add(node.FullPath);
            }
        }
        AddFiles(entries);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var tab in _tabs)
            if (!paths.Contains(tab.Path, pathComparer)) paths.Add(tab.Path);
        var openDocuments = _tabs.ToDictionary(tab => tab.Path, tab => tab.Buffer.Text, pathComparer);
        var finder = new NoteFinderWindow(_notesPath, searchContents, paths, openDocuments);
        var result = await finder.ShowDialog<NoteFinderResult?>(this);
        if (result == null) return;
        await OpenTabAsync(result.Path);
        if (result.Line is int lineNumber && lineNumber >= 1 && lineNumber <= Editor.Document.LineCount)
        {
            Editor.TextArea.Caret.Offset = Editor.Document.GetLineByNumber(lineNumber).Offset;
            Editor.ScrollToLine(lineNumber);
        }
        Editor.TextArea.Focus();
    }

    private async Task BackupTickAsync()
    {
        if (_busy || _notesPath == null) return;
        try
        {
            await UpdateRecoverySnapshotAsync();
            _recoveryErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (!_recoveryErrorLogged) Log($"自動退避に失敗しました: {ex.Message}", true);
            _recoveryErrorLogged = true;
        }
    }

    private async Task UpdateRecoverySnapshotAsync()
    {
        if (_notesPath == null || _recoveryInProgress) return;
        _recoveryInProgress = true;
        try
        {
            if (!_tabs.Any(tab => tab.IsDirty) && !_metadataPending)
            {
                if (!_recoveryDeferred) await _recoveryStore.DeleteAsync(_notesPath);
                return;
            }
            _recoveryDeferred = false;
            _activeTab?.CaretOffset = Editor.TextArea.Caret.Offset;
            var snapshot = new WorkspaceRecoverySnapshot(
                1,
                Path.GetFullPath(_notesPath),
                DateTimeOffset.UtcNow,
                Math.Max(0, _tabs.IndexOf(_activeTab!)),
                [.. _tabs.Select(tab => new RecoveredNoteTab(
                    tab.Path, tab.Buffer.Text, tab.Buffer.SavedHash, tab.IsDirty, tab.CaretOffset,
                    tab == _activeTab && _metadataPending,
                    tab == _activeTab && _metadataPending ? MetadataTitle.Text : null,
                    tab == _activeTab && _metadataPending ? MetadataSlug.Text : null,
                    tab == _activeTab && _metadataPending && MetadataPublish.IsChecked == true))]);
            await _recoveryStore.SaveAsync(snapshot);
        }
        finally { _recoveryInProgress = false; }
    }

    private async void QuickOpen(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => FindNotesAsync(searchContents: false));

    private async void SearchAllNotes(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => FindNotesAsync(searchContents: true));

    private void UpdateBufferStatus()
    {
        var dirty = _buffer?.IsDirty == true || _metadataPending;
        SaveButton.IsEnabled = !_busy && _buffer != null && dirty;
        MetadataPanel.IsEnabled = !_busy && _buffer != null;
        ApplyMetadataButton.IsEnabled = !_busy && _buffer != null && _metadataPending;
        RenameNoteButton.IsEnabled = TrashNoteButton.IsEnabled = !_busy && _buffer != null;
        Title = _buffer == null ? "Tatami" : $"{(dirty ? "● " : "")}{Path.GetFileName(_buffer.Path)} — Tatami";
        if (_buffer != null) EncodingStatus.Text = $"{_buffer.EncodingName}   Markdown";
        UpdateTabLabels();
    }

    private async Task CheckExternalChangesAsync()
    {
        if (_busy || _tabs.Count == 0) return;
        foreach (var tab in _tabs.ToArray())
        {
            try
            {
                var (Exists, Hash, Text) = await tab.Buffer.ReadDiskSnapshotAsync();
                if (Exists && Hash == tab.Buffer.SavedHash)
                {
                    _externalChanges.Remove(tab);
                    continue;
                }
                _externalChanges[tab] = (Hash, Text, Exists);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A temporary read/lock failure is retried on the next poll.
            }
        }
        UpdateExternalDiffButton();
    }

    private void UpdateExternalDiffButton()
    {
        var changed = _activeTab != null && _externalChanges.ContainsKey(_activeTab);
        ExternalDiffButton.IsVisible = changed;
        ExternalDiffButton.IsEnabled = !_busy && changed;
        if (changed) ToolTip.SetTip(ExternalDiffButton, $"{_externalChanges.Count} 個の開いているファイルに外部変更があります");
    }

    private async void ShowExternalDiff(object? sender, RoutedEventArgs e) =>
        await RunAsync(ShowExternalDiffAsync);

    private async Task ShowExternalDiffAsync()
    {
        if (_activeTab == null || !_externalChanges.TryGetValue(_activeTab, out var disk)) return;
        var tab = _activeTab;
        var diff = BuildLineDiff(tab.Buffer.Text, disk.Text ?? "(ファイルが削除されました)");
        var dialog = new Window
        {
            Title = "外部変更との差分",
            Width = 850,
            Height = 620,
            MinWidth = 600,
            MinHeight = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark
        };
        var keep = new Button { Content = "編集を続ける" };
        var reload = new Button { Content = "ディスク版に置換", IsEnabled = disk.Exists && disk.Text != null };
        keep.Click += (_, _) => dialog.Close(false);
        reload.Click += (_, _) => dialog.Close(true);
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        content.Children.Add(new TextBlock
        {
            Text = $"{Path.GetFileName(tab.Path)} は外部で変更されています。\n差分: - は編集中の内容、+ はディスク上の内容です。",
            TextWrapping = TextWrapping.Wrap
        });
        if (tab.IsDirty)
            content.Children.Add(new TextBlock { Text = "ディスク版に置換すると、現在の未保存編集を破棄します。", Foreground = new SolidColorBrush(Color.Parse("#F2C879")), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBox
        {
            Text = diff,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new FontFamily("Menlo,Consolas,monospace"),
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch
        });
        content.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { keep, reload } });
        dialog.Content = content;
        var shouldReload = await dialog.ShowDialog<bool>(this);
        if (!shouldReload || !disk.Exists || disk.Text == null) return;
        await tab.Buffer.ReloadFromDiskAsync();
        tab.ReloadDocument(tab.Buffer.Text);
        _externalChanges.Remove(tab);
        if (tab == _activeTab)
        {
            _buffer = tab.Buffer;
            _metadataPending = false;
            LoadMetadataSettings(tab.Buffer.Text);
            UpdateBufferStatus();
        }
        UpdateExternalDiffButton();
        await UpdateRecoverySnapshotAsync();
        Log($"ディスク上の内容を読み込みました: {tab.Path}");
    }

    private static string BuildLineDiff(string editing, string disk)
    {
        var left = editing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var right = disk.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if ((long)left.Length * right.Length > 300_000)
            return $"差分が大きいため行単位比較を省略しました（編集中 {left.Length} 行 / ディスク {right.Length} 行）。\n\n" +
                string.Join("\n", left.Select(line => "- " + line)) + "\n" + string.Join("\n", right.Select(line => "+ " + line));
        var lcs = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                lcs[i, j] = left[i] == right[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var output = new List<string>();
        var a = 0;
        var b = 0;
        while (a < left.Length || b < right.Length)
        {
            if (a < left.Length && b < right.Length && left[a] == right[b])
            { output.Add("  " + left[a++]); b++; }
            else if (a < left.Length && (b == right.Length || lcs[a + 1, b] >= lcs[a, b + 1])) output.Add("- " + left[a++]);
            else output.Add("+ " + right[b++]);
        }
        return string.Join("\n", output);
    }

    private void RefreshTabStrip()
    {
        TabStrip.Children.Clear();
        _tabLabels.Clear();
        _tabBorders.Clear();
        foreach (var tab in _tabs)
        {
            var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var select = new Button { Content = label, Padding = new Thickness(12, 6), Background = Brushes.Transparent };
            if (_notesPath != null) ToolTip.SetTip(select, Path.GetRelativePath(_notesPath, tab.Path));
            select.Click += async (_, _) => await RunAsync(() => ActivateTabAsync(tab));
            var close = new Button { Content = "×", Padding = new Thickness(7, 4), MinWidth = 28, Background = Brushes.Transparent };
            ToolTip.SetTip(close, "タブを閉じる");
            close.Click += async (_, _) => await RunAsync(() => CloseTabAsync(tab));
            var item = new Border
            {
                BorderBrush = new SolidColorBrush(Color.Parse(tab == _activeTab ? "#82C6AF" : "#30333B")),
                BorderThickness = new Thickness(0, 2, 0, 0),
                Background = new SolidColorBrush(Color.Parse(tab == _activeTab ? "#181A1F" : "#202229")),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { select, close } }
            };
            TabStrip.Children.Add(item);
            _tabLabels[tab] = label;
            _tabBorders[tab] = item;
        }
        UpdateTabLabels();
    }

    private void UpdateTabLabels()
    {
        foreach (var tab in _tabs)
        {
            if (_tabLabels.TryGetValue(tab, out var label))
                label.Text = $"{Path.GetFileName(tab.Path)}{(tab.IsDirty || (tab == _activeTab && _metadataPending) ? "  ●" : "")}";
            if (_tabBorders.TryGetValue(tab, out var border))
            {
                border.BorderBrush = new SolidColorBrush(Color.Parse(tab == _activeTab ? "#82C6AF" : "#30333B"));
                border.Background = new SolidColorBrush(Color.Parse(tab == _activeTab ? "#181A1F" : "#202229"));
            }
        }
    }

    private void MetadataSettingChanged(object? sender, EventArgs e)
    {
        if (_suppressMetadataChanges || _buffer == null) return;
        _recoveryDeferred = false;
        _metadataPending = true;
        UpdateBufferStatus();
    }

    private void LoadMetadataSettings(string text)
    {
        _suppressMetadataChanges = true;
        try
        {
            var note = new NoteRepository().Parse(_buffer?.Path ?? "note.md", text);
            MetadataPublish.IsChecked = note.Metadata.Publish;
            MetadataTitle.Text = note.Metadata.Title ?? "";
            MetadataSlug.Text = note.Metadata.Slug ?? "";
        }
        catch
        {
            MetadataPublish.IsChecked = false;
            MetadataTitle.Text = "";
            MetadataSlug.Text = "";
        }
        finally { _suppressMetadataChanges = false; }
    }

    private void ApplyMetadataSettings()
    {
        if (_buffer == null || !_metadataPending) return;
        var updated = NoteMetadataEditor.Apply(Editor.Text, MetadataTitle.Text, MetadataPublish.IsChecked == true, MetadataSlug.Text);
        _metadataPending = false;
        if (!string.Equals(updated, Editor.Text, StringComparison.Ordinal))
        {
            _suppressMetadataChanges = true;
            try { Editor.Document.Replace(0, Editor.Document.TextLength, updated); }
            finally { _suppressMetadataChanges = false; }
            _buffer.Text = Editor.Text;
        }
        LoadMetadataSettings(Editor.Text);
        UpdateBufferStatus();
    }

    private async void ApplyMetadata(object? sender, RoutedEventArgs e) =>
        await RunAsync(() => { ApplyMetadataSettings(); return Task.CompletedTask; });

    private void Log(string message, bool error = false)
    {
        if (error) RecordDiagnostics(message);
        OutputLog.Text += $"\n[{DateTime.Now:HH:mm:ss}] {(error ? "エラー: " : "")}{message}";
        ProblemSummary.Text = error ? "エラーがあります" : "処理完了";
        ProblemSummary.Foreground = new SolidColorBrush(Color.Parse(error ? "#F19999" : "#82C6AF"));
        StatusText.Text = error ? "エラー — 下の出力を確認してください" : message.Split('\n')[0];
        OutputScroll.ScrollToEnd();
    }

    // One operation at a time protects the current buffer during dialogs and async I/O.
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy) return;
        _busy = true;
        Toolbar.IsEnabled = FileTree.IsEnabled = RefreshButton.IsEnabled = OutputFolderButton.IsEnabled = BuilderSettingsButton.IsEnabled = NoteActions.IsEnabled = false;
        Editor.IsReadOnly = true;
        try { await action(); }
        catch (OperationCanceledException) { Log("ビルドを中止しました。前回の出力を保持しています。"); }
        catch (Exception ex) { Log(ex.Message, true); }
        finally
        {
            _busy = false;
            Toolbar.IsEnabled = FileTree.IsEnabled = RefreshButton.IsEnabled = OutputFolderButton.IsEnabled = BuilderSettingsButton.IsEnabled = NoteActions.IsEnabled = true;
            Editor.IsReadOnly = false;
            UpdateBufferStatus();
            UpdateExternalDiffButton();
            DiagnosticsButton.IsEnabled = _diagnostics.Count > 0;
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (_buffer == null) return;
        ApplyMetadataSettings();
        if (!_buffer.IsDirty) return;
        await _buffer.SaveAsync();
        _recoveryDeferred = false;
        UpdateBufferStatus();
        Log($"保存しました: {_buffer.Path}");
        await UpdateRecoverySnapshotAsync();
    }

    private async Task SaveAllOpenTabsAsync()
    {
        ApplyMetadataSettings();
        var dirty = _tabs.Where(tab => tab.IsDirty).ToArray();
        foreach (var tab in dirty) await tab.Buffer.SaveAsync();
        _recoveryDeferred = false;
        UpdateBufferStatus();
        if (dirty.Length > 0) Log($"開いているノートを {dirty.Length} 件保存しました。");
        await UpdateRecoverySnapshotAsync();
    }

    private async Task<bool> ConfirmUnsavedAsync()
    {
        ApplyMetadataSettings();
        var dirty = _tabs.Where(tab => tab.IsDirty).ToArray();
        if (dirty.Length == 0) return true;
        var choice = await AskUnsavedChoiceAsync(dirty.Length == 1
            ? $"{Path.GetFileName(dirty[0].Path)} に未保存の変更があります。"
            : $"{dirty.Length} 個のノートに未保存の変更があります。");
        if (choice == "save")
        {
            foreach (var tab in dirty) await tab.Buffer.SaveAsync();
            _recoveryDeferred = false;
            UpdateBufferStatus();
            Log($"{dirty.Length} 個のノートを保存しました。");
            await UpdateRecoverySnapshotAsync();
            return true;
        }
        return choice == "discard";
    }

    private async Task<bool> ConfirmTabUnsavedAsync(OpenNoteTab tab)
    {
        if (tab == _activeTab) ApplyMetadataSettings();
        if (!tab.IsDirty) return true;
        var choice = await AskUnsavedChoiceAsync($"{Path.GetFileName(tab.Path)} に未保存の変更があります。");
        if (choice == "save")
        {
            await tab.Buffer.SaveAsync();
            _recoveryDeferred = false;
            UpdateBufferStatus();
            await UpdateRecoverySnapshotAsync();
            return true;
        }
        return choice == "discard";
    }

    private async Task<string?> AskUnsavedChoiceAsync(string message)
    {
        var dialog = new Window
        {
            Title = "未保存の変更",
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark
        };
        var save = new Button { Content = "保存して続行" };
        var discard = new Button { Content = "変更を破棄" };
        var cancel = new Button { Content = "キャンセル" };
        save.Click += (_, _) => dialog.Close("save");
        discard.Click += (_, _) => dialog.Close("discard");
        cancel.Click += (_, _) => dialog.Close("cancel");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 20,
            Children =
            {
                new TextBlock { Text = $"{message} 保存して続行しますか？", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { cancel, discard, save } }
            }
        };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> SelectNotesFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "Markdownの保存先フォルダ", AllowMultiple = false });
        if (folders.Count == 0) return false;
        var path = Path.TrimEndingDirectorySeparator(folders[0].TryGetLocalPath() ?? throw new IOException("ローカルフォルダを選択してください。"));
        if (!await ConfirmUnsavedAsync()) return false;
        if (_notesPath != null) await _recoveryStore.DeleteAsync(_notesPath);
        _recoveryDeferred = false;
        await OpenNotesFolderAsync(path);
        await _settings.SaveAsync(CurrentConfig);
        return true;
    }

    private async Task OpenNotesFolderAsync(string path)
    {
        _ = new NoteFileService(path);
        var entries = await Task.Run(() => WorkspaceEntry.Read(path));
        _notesPath = path;
        _tabs.Clear();
        _activeTab = null;
        _buffer = null;
        _metadataPending = false;
        _recoveryDeferred = false;
        RefreshTabStrip();
        _selected = null;
        _changingSelection = true;
        try { FileTree.ItemsSource = entries; FileTree.SelectedItem = null; }
        finally { _changingSelection = false; }
        Editor.Text = "";
        Editor.IsVisible = false;
        Welcome.IsVisible = true;
        WorkspaceTitle.Text = Path.GetFileName(path);
        ToolTip.SetTip(WorkspaceTitle, path);
        FolderLabel.Text = Path.GetFileName(path);
        Breadcrumb.Text = "Tatami  /  Markdown editor";
        UpdateBufferStatus();
        NotesPathLabel.Text = path;
        ToolTip.SetTip(NotesPathLabel, path);
        EditingPane.IsVisible = true;
        PreviewPane.IsVisible = false;
        Log($"メモのディレクトリを開きました: {path}");
    }

    private async void GetFiles(object? sender, RoutedEventArgs e) =>
        await RunAsync(async () => { await SelectNotesFolderAsync(); });

    private async void SaveFile(object? sender, RoutedEventArgs e) => await RunAsync(SaveCurrentAsync);

    private async void RefreshFiles(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_notesPath == null) return;
        var entries = await Task.Run(() => WorkspaceEntry.Read(_notesPath));
        _changingSelection = true;
        try { FileTree.ItemsSource = entries; }
        finally { _changingSelection = false; }
        Log("ファイル一覧を更新しました。編集中のバッファは保持しています。");
    });

    private async void FileSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || FileTree.SelectedItem is not WorkspaceEntry { IsDirectory: false } entry) return;
        if (_buffer?.Path == entry.FullPath) return;
        await RunAsync(async () =>
        {
            var opened = false;
            try
            {
                await OpenTabAsync(entry.FullPath);
                Log($"開きました: {entry.Name}");
                UpdateBufferStatus();
                opened = true;
            }
            finally
            {
                if (!opened)
                {
                    _changingSelection = true;
                    try { FileTree.SelectedItem = _selected; }
                    finally { _changingSelection = false; }
                }
            }
        });
        Editor.TextArea.Focus();
    }

    private async Task OpenTabAsync(string path)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var tab = _tabs.FirstOrDefault(item => item.Path.Equals(path, comparison));
        if (tab == null)
        {
            // Read the target first: a read error must not affect any open tab.
            tab = new OpenNoteTab(await EditorBuffer.OpenAsync(path));
            _tabs.Add(tab);
        }
        await ActivateTabAsync(tab);
    }

    private async Task ActivateTabAsync(OpenNoteTab tab)
    {
        if (tab == _activeTab) return;
        ApplyMetadataSettings();
        _activeTab?.CaretOffset = Editor.TextArea.Caret.Offset;
        _activeTab = tab;
        _buffer = tab.Buffer;
        _metadataPending = false;
        Editor.Document = tab.Document;
        _buffer.Text = tab.Document.Text;
        Editor.TextArea.Caret.Offset = Math.Clamp(tab.CaretOffset, 0, tab.Document.TextLength);
        LoadMetadataSettings(tab.Document.Text);
        EditingPane.IsVisible = true;
        PreviewPane.IsVisible = false;
        Editor.IsVisible = true;
        Welcome.IsVisible = false;
        Breadcrumb.Text = Path.GetRelativePath(_notesPath!, tab.Path);
        RefreshTabStrip();
        UpdateBufferStatus();
        UpdateExternalDiffButton();
        if (_notesPath != null) await ReloadNotesAsync(tab.Path);
    }

    private async Task CloseTabAsync(OpenNoteTab tab)
    {
        if (!await ConfirmTabUnsavedAsync(tab)) return;
        var index = _tabs.IndexOf(tab);
        var wasActive = tab == _activeTab;
        _tabs.Remove(tab);
        _externalChanges.Remove(tab);
        if (wasActive)
        {
            _activeTab = null;
            _buffer = null;
            _metadataPending = false;
            if (_tabs.Count > 0) await ActivateTabAsync(_tabs[Math.Min(index, _tabs.Count - 1)]);
            else
            {
                Editor.IsVisible = false;
                Welcome.IsVisible = true;
                Breadcrumb.Text = "Tatami / Markdown editor";
            }
        }
        RefreshTabStrip();
        UpdateExternalDiffButton();
        UpdateBufferStatus();
        if (_notesPath != null) await ReloadNotesAsync(_activeTab?.Path);
        await UpdateRecoverySnapshotAsync();
    }

    private async Task ReloadNotesAsync(string? selectedPath)
    {
        var entries = await Task.Run(() => WorkspaceEntry.Read(_notesPath!));
        WorkspaceEntry? Find(IReadOnlyList<WorkspaceEntry> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.FullPath == selectedPath) return node;
                var child = Find(node.Children);
                if (child != null) { node.IsExpanded = true; return child; }
            }
            return null;
        }
        var selected = Find(entries);
        _changingSelection = true;
        try { FileTree.ItemsSource = entries; FileTree.SelectedItem = selected; _selected = selected; }
        finally { _changingSelection = false; }
    }

    private async Task CreateNoteAsync()
    {
        if (_notesPath == null && !await SelectNotesFolderAsync()) return;
        var directory = FileTree.SelectedItem is WorkspaceEntry entry
            ? entry.IsDirectory ? entry.FullPath : Path.GetDirectoryName(entry.FullPath)!
            : _notesPath!;
        var name = await NoteDialogs.Name("新規Markdownノート", "無題.md", directory).ShowDialog<string?>(this);
        if (name == null) return;
        var path = await Task.Run(() => new NoteFileService(_notesPath!).Create(directory, name));
        await OpenTabAsync(path);
        Editor.TextArea.Focus();
        Log($"新規ノートを作成しました（非公開）: {path}");
    }

    private async void NewNote(object? sender, RoutedEventArgs e) => await RunAsync(CreateNoteAsync);

    private async void RenameNote(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_buffer == null || _notesPath == null) return;
        var name = await NoteDialogs.Name("ノートの名前変更", Path.GetFileName(_buffer.Path), Path.GetDirectoryName(_buffer.Path)!).ShowDialog<string?>(this);
        if (name == null) return;
        await Task.Run(() => _buffer.Rename(_notesPath, name));
        // Do not reload the document: unsaved changes, caret, and undo history belong to this buffer.
        Breadcrumb.Text = Path.GetRelativePath(_notesPath, _buffer.Path);
        await ReloadNotesAsync(_buffer.Path);
        UpdateBufferStatus();
        await UpdateRecoverySnapshotAsync();
        Log($"名前を変更しました: {_buffer.Path}（公開URLの変更には再ビルドが必要です）");
    });

    private async void TrashNote(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_activeTab == null || _notesPath == null) return;
        var tab = _activeTab;
        var name = Path.GetFileName(tab.Path);
        if (!await NoteDialogs.Trash(name).ShowDialog<bool>(this) || !await ConfirmTabUnsavedAsync(tab)) return;
        await Task.Run(() => new NoteFileService(_notesPath).MoveToTrash(tab.Path));
        _tabs.Remove(tab);
        _activeTab = null;
        _buffer = null;
        _metadataPending = false;
        if (_tabs.Count > 0) await ActivateTabAsync(_tabs[^1]);
        else
        {
            Editor.IsVisible = false;
            Welcome.IsVisible = true;
            Breadcrumb.Text = "Tatami / Markdown editor";
        }
        RefreshTabStrip();
        UpdateBufferStatus();
        await ReloadNotesAsync(_activeTab?.Path);
        await UpdateRecoverySnapshotAsync();
        Log($"Tatamiのゴミ箱へ移動しました: {name}。公開済みHTMLへの反映には再ビルドしてください。");
    });

    private async void RestoreNote(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_notesPath == null) { Log("先にメモのディレクトリを選んでください。"); return; }
        var service = new NoteFileService(_notesPath);
        var entries = await Task.Run(service.ListTrash);
        var id = await NoteDialogs.Restore(entries).ShowDialog<string?>(this);
        if (id == null) return;
        var path = await Task.Run(() => service.Restore(id));
        await OpenTabAsync(path);
        Editor.TextArea.Focus();
        Log($"復元しました: {path}");
    });

    private async void BuildSite(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_notesPath == null && !await SelectNotesFolderAsync()) return;
        if (_outputPath == null && !await ChooseOutputFolderAsync()) return;
        // Persist every edited open buffer so the build uses the current text from all tabs.
        await SaveAllOpenTabsAsync();
        StatusText.Text = "サイトをビルドしています…";
        ProblemSummary.Text = "ビルド中";
        _buildCancellation = new CancellationTokenSource();
        CancelBuildButton.IsVisible = true;
        Tatami.Models.BuildResult result;
        try
        {
            var progress = new Progress<string>(message =>
            {
                RecordDiagnostics(message);
                OutputLog.Text += "\n" + message;
                if (OutputLog.Text.Length > 100000) OutputLog.Text = OutputLog.Text[^100000..];
                OutputScroll.ScrollToEnd();
            });
            result = await BuildCoordinator.BuildAsync(CurrentConfig, progress, _buildCancellation.Token);
        }
        finally
        {
            _buildCancellation.Dispose();
            _buildCancellation = null;
            CancelBuildButton.IsVisible = false;
        }
        Log($"ビルド完了: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件\n{result.OutputPath}");
        await ShowPreviewAsync();
    });

    private async Task RestoreWorkspaceAsync()
    {
        var config = await _settings.LoadAsync();
        if (config == null) return;
        _builder = config.Builder;
        _external = config.External;
        BuilderLabel.Text = _builder == "external" ? "外部ビルダー" : "内蔵ビルダー";
        if (!string.IsNullOrWhiteSpace(config.OutputPath))
        {
            _outputPath = Path.GetFullPath(config.OutputPath);
            OutputPathLabel.Text = _outputPath;
            ToolTip.SetTip(OutputPathLabel, _outputPath);
        }
        if (Directory.Exists(config.InputPath))
        {
            await OpenNotesFolderAsync(Path.GetFullPath(config.InputPath));
            await OfferRecoveryAsync(_notesPath!);
        }
        else if (!string.IsNullOrEmpty(config.InputPath)) Log("保存されたメモのディレクトリが見つかりません。選び直してください。", true);
    }

    private async Task OfferRecoveryAsync(string workspace)
    {
        WorkspaceRecoverySnapshot? snapshot;
        try { snapshot = await _recoveryStore.LoadAsync(workspace); }
        catch (Exception ex) { Log($"自動退避データを読み込めません: {ex.Message}", true); return; }
        if (snapshot == null) return;
        var choice = await AskRecoveryChoiceAsync(snapshot);
        if (choice == "restore")
        {
            var restoredTabs = new List<(int SourceIndex, OpenNoteTab Tab, RecoveredNoteTab Saved)>();
            for (var i = 0; i < snapshot.Tabs.Length; i++)
            {
                var saved = snapshot.Tabs[i];
                try
                {
                    var buffer = await EditorBuffer.OpenAsync(saved.Path);
                    buffer.RestoreBackup(saved.Text, saved.BaselineHash, saved.WasDirty);
                    var tab = new OpenNoteTab(buffer) { CaretOffset = saved.CaretOffset };
                    _tabs.Add(tab);
                    restoredTabs.Add((i, tab, saved));
                }
                catch (Exception ex) { Log($"退避したノートを復元できませんでした: {saved.Path}: {ex.Message}", true); }
            }
            if (_tabs.Count == 0) { await _recoveryStore.DeleteAsync(workspace); return; }
            _recoveryDeferred = false;
            var active = restoredTabs.FirstOrDefault(item => item.SourceIndex == snapshot.ActiveTabIndex);
            if (active.Tab == null) active = restoredTabs[0];
            await ActivateTabAsync(active.Tab);
            var activeDraft = active.Saved;
            if (activeDraft.HasPendingMetadata)
            {
                _suppressMetadataChanges = true;
                try
                {
                    MetadataTitle.Text = activeDraft.PendingTitle ?? "";
                    MetadataSlug.Text = activeDraft.PendingSlug ?? "";
                    MetadataPublish.IsChecked = activeDraft.PendingPublish;
                }
                finally { _suppressMetadataChanges = false; }
                _metadataPending = true;
                UpdateBufferStatus();
            }
            Editor.TextArea.Focus();
            Log($"自動退避から {_tabs.Count} 個のタブを復元しました。");
        }
        else if (choice == "discard")
        {
            await _recoveryStore.DeleteAsync(workspace);
            _recoveryDeferred = false;
            Log("自動退避データを破棄しました。");
        }
        else _recoveryDeferred = true;
    }

    private async Task<string?> AskRecoveryChoiceAsync(WorkspaceRecoverySnapshot snapshot)
    {
        var dialog = new Window
        {
            Title = "編集内容を復元",
            Width = 500,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark
        };
        var restore = new Button { Content = "復元する", Background = new SolidColorBrush(Color.Parse("#285D4D")) };
        var discard = new Button { Content = "破棄する" };
        var later = new Button { Content = "後で決める" };
        restore.Click += (_, _) => dialog.Close("restore");
        discard.Click += (_, _) => dialog.Close("discard");
        later.Click += (_, _) => dialog.Close("later");
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = "前回終了時の自動退避があります。", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock
                {
                    Text = $"{snapshot.Tabs.Length} 個のタブ — {snapshot.SavedAt.ToLocalTime():yyyy-MM-dd HH:mm} に保存",
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock { Text = "復元すると、保存後にディスク上のファイルが変更されていたノートは競合保護のため上書き保存できません。", TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { later, discard, restore } }
            }
        };
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> ChooseOutputFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "HTML出力先（メモとは別に保存されます）", AllowMultiple = false });
        if (folders.Count == 0) return false;
        var path = Path.TrimEndingDirectorySeparator(folders[0].TryGetLocalPath()
            ?? throw new IOException("ローカルフォルダを選択してください。"));
        if (_notesPath != null && PathSafety.IsWithin(_notesPath, path))
            throw new IOException("メモと同じディレクトリや、その親をHTML出力先には指定できません。");
        PathSafety.RejectLinks(path);
        _outputPath = path;
        OutputPathLabel.Text = path;
        ToolTip.SetTip(OutputPathLabel, path);
        // Clear stale pages from a previously selected output directory.
        _refreshingPages = true;
        try { PreviewPages.ItemsSource = null; }
        finally { _refreshingPages = false; }
        _webView?.Navigate(new Uri("about:blank"));
        await _previewServer.DisposeAsync();
        PreviewStatus.Text = "出力先を変更しました。HTMLプレビューを開くかビルドしてください。";
        await _settings.SaveAsync(CurrentConfig);
        Log($"HTML出力先を設定しました: {path}");
        return true;
    }

    private async void SelectOutputFolder(object? sender, RoutedEventArgs e) =>
        await RunAsync(async () => { await ChooseOutputFolderAsync(); });

    private async void TogglePreview(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (PreviewPane.IsVisible) { PreviewPane.IsVisible = false; EditingPane.IsVisible = true; }
        else await ShowPreviewAsync();
    });

    private async Task ShowPreviewAsync()
    {
        if (_outputPath == null) { Log("先にHTML出力先を選択してビルドしてください。"); return; }
        var pages = await PreviewCatalog.ReadAsync(_outputPath);
        await _previewServer.StartAsync(_outputPath);
        _livePreviewActive = _buffer != null;
        _refreshingPages = true;
        try
        {
            PreviewPages.ItemsSource = pages;
            string? slug = null;
            if (_buffer != null)
            {
                var note = new NoteRepository().Parse(_buffer.Path, _buffer.Text);
                if (note.Metadata.Publish)
                    slug = note.Metadata.Slug ?? PathSafety.FileSlug(Path.GetFileNameWithoutExtension(_buffer.Path));
            }
            PreviewPages.SelectedItem = pages.FirstOrDefault(page => page.Name == slug) ?? pages.FirstOrDefault();
        }
        finally { _refreshingPages = false; }
        EditingPane.IsVisible = false;
        PreviewPane.IsVisible = true;
        await UpdateLivePreviewAsync();
        NavigateSelectedPage();
    }

    private void RequestLivePreviewRefresh()
    {
        _livePreviewActive = _buffer != null;
        if (_buffer == null || _outputPath == null) return;
        _livePreviewTimer.Stop();
        _livePreviewTimer.Start();
    }

    private async Task UpdateLivePreviewAsync()
    {
        _livePreviewTimer.Stop();
        if (_buffer == null || _outputPath == null || _previewServer.BaseUri == null) return;
        try
        {
            var text = Editor.Text;
            if (_metadataPending)
                text = NoteMetadataEditor.Apply(text, MetadataTitle.Text, MetadataPublish.IsChecked == true, MetadataSlug.Text);
            var note = new NoteRepository().Parse(_buffer.Path, text);
            var renderer = new TemplateRenderer(Path.Combine(AppContext.BaseDirectory, "Templates", "default.html"));
            _previewServer.SetLivePreview(renderer.Render(note));
            if (_livePreviewActive)
            {
                PreviewStatus.Text = "編集中の記事のライブプレビュー";
                NavigateLivePreview();
            }
        }
        catch (Exception ex)
        {
            PreviewStatus.Text = $"ライブプレビューを更新できません: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    private void NavigateLivePreview()
    {
        var uri = _previewServer.GetLivePreviewUri();
        EnsureWebView();
        PreviewStatus.Text = "編集中の記事のライブプレビュー";
        if (_webView!.Source == uri) _webView.Refresh();
        else _webView.Navigate(uri);
    }

    private void NavigateSelectedPage()
    {
        if (_livePreviewActive && _buffer != null)
        {
            if (_previewServer.BaseUri != null && _previewServer.GetLivePreviewUri() is { } liveUri)
            {
                EnsureWebView();
                if (_webView!.Source == liveUri) _webView.Refresh(); else _webView.Navigate(liveUri);
            }
            return;
        }
        if (PreviewPages.SelectedItem is not PreviewPage page)
        {
            _webView?.Navigate(new Uri("about:blank"));
            PreviewStatus.Text = "公開済みの記事がありません。publish: true を指定してビルドしてください。";
            return;
        }
        var uri = _previewServer.GetUri(page.FullPath);
        if (!_previewServer.CanNavigate(uri))
            throw new IOException("プレビュー対象のHTMLが見つからないか、許可された出力先の範囲外です。");
        EnsureWebView();
        PreviewStatus.Text = $"読み込み中: {page.Name}";
        if (_webView!.Source == uri) _webView.Refresh();
        else _webView.Navigate(uri);
    }

    private void EnsureWebView()
    {
        if (_webView != null) return;
        _webView = new NativeWebView();
        _webView.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            PreviewStatus.Text = "プレビューから別ウィンドウは開きません。";
        };
        _webView.NavigationStarted += (_, args) =>
        {
            if (args.Request == null || !_previewServer.CanNavigate(args.Request))
            {
                args.Cancel = true;
                PreviewStatus.Text = "プレビューでは出力先のHTMLだけを開けます。";
            }
        };
        _webView.NavigationCompleted += (_, args) =>
        {
            if (args.Request == null || args.Request.ToString() == "about:blank") return;
            if (_livePreviewActive) PreviewStatus.Text = "編集中の記事のライブプレビュー";
            else PreviewStatus.Text = args.IsSuccess
                ? "ビルド済みHTML — 編集の反映には再ビルドが必要です。"
                : "HTMLを読み込めませんでした。WebViewの実行環境とファイルを確認してください。";
            if (!args.IsSuccess) Log(PreviewStatus.Text, true);
        };
        PreviewHost.Content = _webView;
    }

    private void PreviewPageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPages) return;
        _livePreviewActive = false;
        try { NavigateSelectedPage(); }
        catch (Exception ex) { Log(ex.Message, true); }
    }

    private async void ReloadPreview(object? sender, RoutedEventArgs e) => await RunAsync(ShowPreviewAsync);

    private void CancelBuild(object? sender, RoutedEventArgs e) => _buildCancellation?.Cancel();

    private void RecordDiagnostics(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            var match = ParenthesizedDiagnostic.Match(line);
            if (!match.Success) match = ColonDiagnostic.Match(line);
            if (!match.Success || !int.TryParse(match.Groups["line"].Value, out var number) || number < 1) continue;
            var path = match.Groups["path"].Value.Trim().Trim('"', '\'');
            var item = new BuildDiagnostic(path, number, line.Trim());
            if (!_diagnostics.Any(existing => existing.Path == item.Path && existing.Line == item.Line && existing.Message == item.Message))
                _diagnostics.Add(item);
        }
        DiagnosticsButton.IsVisible = _diagnostics.Count > 0;
        DiagnosticsButton.IsEnabled = !_busy && _diagnostics.Count > 0;
    }

    private async void ShowDiagnostics(object? sender, RoutedEventArgs e) =>
        await RunAsync(ShowDiagnosticsAsync);

    private async Task ShowDiagnosticsAsync()
    {
        if (_diagnostics.Count == 0) return;
        var dialog = new Window
        {
            Title = "エラー箇所へ移動",
            Width = 760,
            Height = 480,
            MinWidth = 520,
            MinHeight = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark
        };
        var rows = new StackPanel { Margin = new Thickness(16), Spacing = 6 };
        foreach (var diagnostic in _diagnostics.TakeLast(100).Reverse())
        {
            var button = new Button
            {
                Content = $"{diagnostic.Path}:{diagnostic.Line}  {diagnostic.Message}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            button.Click += (_, _) => dialog.Close(diagnostic);
            rows.Children.Add(button);
        }
        dialog.Content = new ScrollViewer { Content = rows };
        var selected = await dialog.ShowDialog<BuildDiagnostic?>(this);
        if (selected == null) return;
        var path = ResolveDiagnosticPath(selected.Path);
        if (path == null || !File.Exists(path))
        {
            Log($"該当ファイルを見つけられません: {selected.Path}", true);
            return;
        }
        await OpenTabAsync(path);
        if (selected.Line <= Editor.Document.LineCount)
        {
            Editor.TextArea.Caret.Offset = Editor.Document.GetLineByNumber(selected.Line).Offset;
            Editor.ScrollToLine(selected.Line);
        }
        Editor.TextArea.Focus();
    }

    private string? ResolveDiagnosticPath(string path)
    {
        if (Path.IsPathFullyQualified(path)) return Path.GetFullPath(path);
        var roots = new[] { _notesPath, _builder == "external" ? _external.ProjectPath : null, Environment.CurrentDirectory };
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var candidate = Path.GetFullPath(Path.Combine(root!, path));
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private async void ConfigureBuilder(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        // Hide the native browser while the configuration window is displayed.
        var previewVisible = PreviewPane.IsVisible;
        PreviewPane.IsVisible = false;
        try
        {
            var dialog = new BuildSettingsWindow(_builder, _external);
            var result = await dialog.ShowDialog<SiteConfig?>(this);
            if (result == null) return;
            _builder = result.Builder;
            _external = result.External;
            BuilderLabel.Text = _builder == "external" ? "外部ビルダー" : "内蔵ビルダー";
            await _settings.SaveAsync(CurrentConfig);
            Log("ビルダー設定を保存しました。");
        }
        finally { PreviewPane.IsVisible = previewVisible; }
    });

    private void ClearOutput(object? sender, RoutedEventArgs e)
    {
        OutputLog.Text = "";
        _diagnostics.Clear();
        DiagnosticsButton.IsVisible = false;
        ProblemSummary.Text = "出力をクリアしました";
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_busy) { e.Cancel = true; return; }
        e.Cancel = true;
        var close = !_tabs.Any(tab => tab.IsDirty) && !_metadataPending;
        if (!close)
        {
            try { await UpdateRecoverySnapshotAsync(); }
            catch (Exception ex) { Log($"終了前の自動退避に失敗しました: {ex.Message}", true); }
            await RunAsync(async () => close = await ConfirmUnsavedAsync());
        }
        if (!close) return;
        if (_notesPath != null && !_recoveryDeferred)
        {
            try { await _recoveryStore.DeleteAsync(_notesPath); }
            catch (Exception ex) { Log($"自動退避データを整理できません: {ex.Message}", true); }
        }
        _recoveryTimer.Stop();
        _allowClose = true;
        Close();
    }

    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.md)\((?<line>\d+)(?:,\d+)?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
