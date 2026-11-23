using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tatami.Services;

namespace Tatami.Views;

internal sealed record NoteLinkPick(NoteLinkTarget? Target, bool CreateNew = false);

internal sealed class NoteLinkPickerWindow : Window
{
    private readonly NoteLinkTarget[] _targets;
    private readonly TextBox _query = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new();
    private IReadOnlyList<NoteLinkTarget> _current = [];

    public NoteLinkPickerWindow(IReadOnlyList<NoteLinkTarget> targets)
    {
        _targets = [.. targets];
        Title = "ノートへのリンクを挿入";
        Width = 640;
        Height = 560;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;

        _query.PlaceholderText = "ノート名または見出しを検索…";
        _query.FontSize = 16;
        _query.Margin = new Thickness(16, 16, 16, 8);
        _results.Margin = new Thickness(16, 0, 16, 8);
        _results.DoubleTapped += (_, _) => AcceptSelection();
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AcceptSelection(); } };
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { AcceptSelection(); e.Handled = true; }
            else if (e.Key == Key.Down && _results.ItemCount > 0)
            { _results.SelectedIndex = 0; _results.Focus(); e.Handled = true; }
        };
        _query.TextChanged += (_, _) => UpdateResults();
        _status.Margin = new Thickness(18, 0, 18, 12);
        _status.Foreground = new SolidColorBrush(Color.Parse("#8D95A5"));
        var cancel = new Button { Content = "キャンセル", MinWidth = 90, IsCancel = true };
        cancel.Click += (_, _) => Close();
        var create = new Button { Content = "新しいノートを作成…", MinWidth = 150 };
        create.Click += (_, _) => Close(new NoteLinkPick(null, CreateNew: true));
        var choose = new Button { Content = "リンクを挿入", MinWidth = 110, Background = new SolidColorBrush(Color.Parse("#285D4D")), IsDefault = true };
        choose.Click += (_, _) => AcceptSelection();
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children = { cancel, create, choose }
        };
        Grid.SetRow(_results, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(actions, 3);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Children = { _query, _results, _status, actions }
        };
        Opened += (_, _) => { UpdateResults(); _query.Focus(); };
    }

    private void UpdateResults()
    {
        var query = _query.Text?.Trim() ?? "";
        _current = _targets
            .Where(target => query.Length == 0
                || target.Display.Contains(query, StringComparison.OrdinalIgnoreCase)
                || target.Path.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(500)
            .ToArray();
        _results.ItemsSource = _current.Select(target => target.Display).ToArray();
        _results.SelectedIndex = _current.Count > 0 ? 0 : -1;
        _status.Text = _current.Count == 0 ? "候補がありません。新しいノートを作成できます。" : $"{_current.Count} 件 — Enterで選択";
    }

    private void AcceptSelection()
    {
        if (_current.Count == 0) return;
        var index = _results.SelectedIndex < 0 ? 0 : _results.SelectedIndex;
        if (index < _current.Count) Close(new NoteLinkPick(_current[index]));
    }
}

internal sealed class BrokenLinksWindow : Window
{
    private readonly IReadOnlyList<BrokenNoteLink> _links;
    private readonly ListBox _results;

    public BrokenLinksWindow(IReadOnlyList<BrokenNoteLink> links, string workspace)
    {
        _links = links;
        Title = "リンク切れの確認";
        Width = 760;
        Height = 500;
        MinWidth = 520;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        _results = new ListBox
        {
            Margin = new Thickness(16, 8),
            ItemsSource = links.Select(link => $"{System.IO.Path.GetRelativePath(workspace, link.SourcePath)}:{link.Line}  {link.Url} — {link.Reason}").ToArray(),
            SelectedIndex = links.Count > 0 ? 0 : -1
        };
        _results.DoubleTapped += (_, _) => AcceptSelection();
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AcceptSelection(); } };
        var status = new TextBlock
        {
            Text = links.Count == 0 ? "リンク切れはありません。" : $"{links.Count} 件のリンク切れ — 項目を選ぶと参照元を開きます。",
            Margin = new Thickness(18, 14, 18, 0),
            Foreground = new SolidColorBrush(Color.Parse(links.Count == 0 ? "#82C6AF" : "#F19999"))
        };
        var open = new Button { Content = "参照元を開く", MinWidth = 110, IsEnabled = links.Count > 0, IsDefault = true };
        open.Click += (_, _) => AcceptSelection();
        var close = new Button { Content = "閉じる", MinWidth = 90, IsCancel = true };
        close.Click += (_, _) => Close();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { close, open } };
        Grid.SetRow(_results, 1);
        Grid.SetRow(actions, 2);
        Content = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Children = { status, _results, actions } };
    }

    private void AcceptSelection()
    {
        if (_links.Count == 0 || _results.SelectedIndex < 0 || _results.SelectedIndex >= _links.Count) return;
        Close(_links[_results.SelectedIndex]);
    }
}
