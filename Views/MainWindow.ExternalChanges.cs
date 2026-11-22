using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _externalChangeTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<OpenNoteTab, (string Hash, string? Text, bool Exists)> _externalChanges = [];

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

}
