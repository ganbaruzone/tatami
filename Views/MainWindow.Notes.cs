using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tatami.Models;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private readonly List<OpenNoteTab> _tabs = [];
    private readonly Dictionary<OpenNoteTab, TextBlock> _tabLabels = [];
    private readonly Dictionary<OpenNoteTab, Border> _tabBorders = [];
    private OpenNoteTab? _activeTab;
    private EditorBuffer? _buffer;
    private WorkspaceEntry? _selected;
    private bool _changingSelection;
    private bool _suppressMetadataChanges;
    private bool _metadataPending;

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

}
