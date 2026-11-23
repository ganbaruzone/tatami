using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tatami.Models;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private readonly WorkspaceRecoveryStore _recoveryStore = new();
    private readonly DispatcherTimer _recoveryTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _recoveryDeferred;
    private bool _recoveryInProgress;
    private bool _recoveryErrorLogged;

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
            OutputPathLabel.Text = DisplayDirectoryName(_outputPath);
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

}
