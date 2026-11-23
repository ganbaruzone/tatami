using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
    private string? _notesPath;
    private string? _outputPath;
    private readonly WorkspaceSettings _settings = new();
    private string _builder = "builtin";
    private ExternalBuildOptions _external = new();
    private CancellationTokenSource? _buildCancellation;
    private SiteConfig CurrentConfig => new(_notesPath ?? "", _outputPath ?? "") { Builder = _builder, External = _external };
    private bool _busy;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
        _slashCommandPalette = new SlashCommandPalette(InsertSlashCommand);
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
            if (_slashCommandPalette.IsOpen) RefreshSlashCommandPalette();
        };
        Editor.TextArea.TextEntered += OnEditorTextEntered;
        MetadataPublish.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == nameof(MetadataPublish.IsChecked)) MetadataSettingChanged(MetadataPublish, EventArgs.Empty);
        };
        MetadataTitle.TextChanged += MetadataSettingChanged;
        MetadataSlug.TextChanged += MetadataSettingChanged;
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            CursorStatus.Text = $"行 {Editor.TextArea.Caret.Line}, 列 {Editor.TextArea.Caret.Column}";
            if (_slashCommandPalette.IsOpen) RefreshSlashCommandPalette();
        };
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
        if (HandleSlashCommandKey(e)) return;
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
        if (e.Key == Key.K && e.KeyModifiers == modifier)
        {
            e.Handled = true;
            await RunAsync(InsertNoteLinkAsync);
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

    private async void SavedNoteViews(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (_notesPath == null)
        {
            Log("先にメモのディレクトリを開いてください。", true);
            return;
        }

        var entries = await Task.Run(() => WorkspaceEntry.Read(_notesPath));
        var paths = new List<string>();
        void AddMarkdownFiles(IEnumerable<WorkspaceEntry> nodes)
        {
            foreach (var node in nodes)
            {
                if (node.IsDirectory) AddMarkdownFiles(node.Children);
                else if (string.Equals(Path.GetExtension(node.FullPath), ".md", StringComparison.OrdinalIgnoreCase)) paths.Add(node.FullPath);
            }
        }
        AddMarkdownFiles(entries);
        var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        foreach (var tab in _tabs)
            if (!paths.Contains(tab.Path, pathComparer)) paths.Add(tab.Path);
        var openDocuments = _tabs.ToDictionary(tab => tab.Path, tab => tab.Buffer.Text, pathComparer);
        var repository = new NoteRepository();
        var notes = await Task.Run(() => paths.Select(path => repository.Parse(path,
            openDocuments.TryGetValue(path, out var text) ? text : File.ReadAllText(path))).ToArray());
        var views = new SavedNoteViewsWindow(_notesPath, notes);
        var selectedPath = await views.ShowForAsync(this);
        if (selectedPath == null) return;
        await OpenTabAsync(selectedPath);
        Editor.TextArea.Focus();
    });

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
        // Resolve IME preedit before saving, changing tabs, or moving focus into another workflow.
        Editor.TextArea.CommitPreedit();
        _busy = true;
        Toolbar.IsEnabled = FileTree.IsEnabled = RefreshButton.IsEnabled = OutputFolderButton.IsEnabled = BuilderSettingsButton.IsEnabled = NoteActions.IsEnabled = LinkTools.IsEnabled = false;
        Editor.IsReadOnly = true;
        try { await action(); }
        catch (OperationCanceledException) { Log("ビルドを中止しました。前回の出力を保持しています。"); }
        catch (Exception ex) { Log(ex.Message, true); }
        finally
        {
            _busy = false;
            Toolbar.IsEnabled = FileTree.IsEnabled = RefreshButton.IsEnabled = OutputFolderButton.IsEnabled = BuilderSettingsButton.IsEnabled = NoteActions.IsEnabled = LinkTools.IsEnabled = true;
            Editor.IsReadOnly = false;
            UpdateBufferStatus();
            UpdateExternalDiffButton();
            DiagnosticsButton.IsEnabled = _diagnostics.Count > 0;
        }
    }

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


}
