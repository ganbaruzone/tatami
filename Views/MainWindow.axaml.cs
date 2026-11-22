using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using System.Xml;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AvaloniaEdit.Search;
using AvaloniaEdit.Highlighting;
using AvaloniaEdit.Highlighting.Xshd;
using Tatami.Models;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private string? _notesPath;
    private string? _outputPath;
    private readonly WorkspaceSettings _settings = new();
    private NativeWebView? _webView;
    private bool _refreshingPages;
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
            if (_buffer != null) _buffer.Text = Editor.Text;
            if (_buffer != null && !_suppressMetadataChanges && !_metadataPending)
                LoadMetadataSettings(Editor.Text);
            UpdateBufferStatus();
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
        Closed += async (_, _) => await _previewServer.DisposeAsync();
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
        }
    }

    private async Task SaveCurrentAsync()
    {
        if (_buffer == null) return;
        ApplyMetadataSettings();
        if (!_buffer.IsDirty) return;
        await _buffer.SaveAsync();
        UpdateBufferStatus();
        Log($"保存しました: {_buffer.Path}");
    }

    private async Task SaveAllOpenTabsAsync()
    {
        ApplyMetadataSettings();
        var dirty = _tabs.Where(tab => tab.IsDirty).ToArray();
        foreach (var tab in dirty) await tab.Buffer.SaveAsync();
        UpdateBufferStatus();
        if (dirty.Length > 0) Log($"開いているノートを {dirty.Length} 件保存しました。");
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
            UpdateBufferStatus();
            Log($"{dirty.Length} 個のノートを保存しました。");
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
            UpdateBufferStatus();
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
        if (_activeTab != null) _activeTab.CaretOffset = Editor.TextArea.Caret.Offset;
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
        if (_notesPath != null) await ReloadNotesAsync(tab.Path);
    }

    private async Task CloseTabAsync(OpenNoteTab tab)
    {
        if (!await ConfirmTabUnsavedAsync(tab)) return;
        var index = _tabs.IndexOf(tab);
        var wasActive = tab == _activeTab;
        _tabs.Remove(tab);
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
        UpdateBufferStatus();
        if (_notesPath != null) await ReloadNotesAsync(_activeTab?.Path);
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
                OutputLog.Text += "\n" + message;
                if (OutputLog.Text.Length > 100000) OutputLog.Text = OutputLog.Text[^100000..];
                OutputScroll.ScrollToEnd();
            });
            result = await new BuildCoordinator().BuildAsync(CurrentConfig, progress, _buildCancellation.Token);
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
        if (Directory.Exists(config.InputPath)) await OpenNotesFolderAsync(config.InputPath);
        else if (!string.IsNullOrEmpty(config.InputPath)) Log("保存されたメモのディレクトリが見つかりません。選び直してください。", true);
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
        NavigateSelectedPage();
    }

    private void NavigateSelectedPage()
    {
        if (PreviewPages.SelectedItem is not PreviewPage page)
        {
            _webView?.Navigate(new Uri("about:blank"));
            PreviewStatus.Text = "公開済みの記事がありません。publish: true を指定してビルドしてください。";
            return;
        }
        var uri = _previewServer.GetUri(page.FullPath);
        if (!_previewServer.CanNavigate(uri))
            throw new IOException("プレビュー対象のHTMLが見つからないか、許可された出力先の範囲外です。");
        if (_webView == null)
        {
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
                PreviewStatus.Text = args.IsSuccess
                    ? "ビルド済みHTML — 編集の反映には再ビルドが必要です。"
                    : "HTMLを読み込めませんでした。WebViewの実行環境とファイルを確認してください。";
                if (!args.IsSuccess) Log(PreviewStatus.Text, true);
            };
            PreviewHost.Content = _webView;
        }
        PreviewStatus.Text = $"読み込み中: {page.Name}";
        if (_webView.Source == uri) _webView.Refresh();
        else _webView.Navigate(uri);
    }

    private void PreviewPageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPages) return;
        try { NavigateSelectedPage(); }
        catch (Exception ex) { Log(ex.Message, true); }
    }

    private async void ReloadPreview(object? sender, RoutedEventArgs e) => await RunAsync(ShowPreviewAsync);

    private void CancelBuild(object? sender, RoutedEventArgs e) => _buildCancellation?.Cancel();

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
    { OutputLog.Text = ""; ProblemSummary.Text = "出力をクリアしました"; }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        if (_busy) { e.Cancel = true; return; }
        if (!_tabs.Any(tab => tab.IsDirty) && !_metadataPending) return;
        e.Cancel = true;
        var close = false;
        await RunAsync(async () => close = await ConfirmUnsavedAsync());
        if (close) { _allowClose = true; Close(); }
    }
}
