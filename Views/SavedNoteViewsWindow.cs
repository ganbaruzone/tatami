using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Tatami.Models;
using Tatami.Services;

namespace Tatami.Views;

internal sealed class SavedNoteViewsWindow : Window
{
    private static readonly string[] AllTags = ["すべて"];
    private static readonly string[] TableHeader = ["タイトル                         日付              タグ・パス"];
    private readonly string _root;
    private readonly IReadOnlyList<Note> _notes;
    private readonly SavedNoteViewService _service;
    private readonly ComboBox _saved = new() { MinWidth = 170 };
    private readonly TextBox _name = new() { PlaceholderText = "保存名" };
    private readonly ComboBox _tag = new() { MinWidth = 130 };
    private readonly ComboBox _publish = new() { MinWidth = 110, ItemsSource = new[] { "すべて", "公開", "非公開" }, SelectedIndex = 0 };
    private readonly TextBox _from = new() { PlaceholderText = "開始日 YYYY-MM-DD" };
    private readonly TextBox _through = new() { PlaceholderText = "終了日 YYYY-MM-DD" };
    private readonly TextBox _folder = new() { PlaceholderText = "フォルダ（例: guides）" };
    private readonly ComboBox _sort = new() { MinWidth = 100, ItemsSource = new[] { "日付", "タイトル", "パス" }, SelectedIndex = 0 };
    private readonly CheckBox _descending = new() { Content = "降順", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _display = new() { MinWidth = 100, ItemsSource = new[] { "一覧", "表", "カード" }, SelectedIndex = 0 };
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new();
    private readonly List<Note> _currentResults = [];
    private string? _selectedPath;

    public SavedNoteViewsWindow(string root, IReadOnlyList<Note> notes)
    {
        _root = root;
        _notes = notes;
        _service = new SavedNoteViewService(root);
        Title = "保存したノート一覧";
        Width = 820;
        Height = 680;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        _tag.ItemsSource = AllTags.Concat(notes.SelectMany(note => note.Metadata.Tags).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)).ToArray();
        _tag.SelectedIndex = 0;
        _saved.ItemsSource = _service.Load().Select(view => view.Name).ToArray();
        _saved.SelectionChanged += (_, _) => LoadSelectedView();
        _display.SelectionChanged += (_, _) => ApplyView();
        _results.DoubleTapped += (_, _) => AcceptSelection();
        _results.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; AcceptSelection(); } };

        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(16, 12) };
        AddCell(form, Labelled("保存済み", _saved), 0, 0);
        AddCell(form, Labelled("名前", _name), 1, 0);
        AddCell(form, Labelled("タグ", _tag), 2, 0);
        AddCell(form, Labelled("公開状態", _publish), 0, 1);
        AddCell(form, Labelled("開始日", _from), 1, 1);
        AddCell(form, Labelled("終了日", _through), 2, 1);
        AddCell(form, Labelled("フォルダ", _folder), 0, 2);
        AddCell(form, Labelled("並び替え", _sort), 1, 2);
        AddCell(form, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { _descending, Labelled("表示", _display) } }, 2, 2);
        var save = new Button { Content = "条件を保存", MinWidth = 110 };
        var apply = new Button { Content = "適用", MinWidth = 90, Background = new SolidColorBrush(Color.Parse("#285D4D")) };
        save.Click += (_, _) => SaveView();
        apply.Click += (_, _) => ApplyView();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, apply } };
        AddCell(form, buttons, 0, 3);

        _results.Margin = new Thickness(16, 0, 16, 8);
        _status.Margin = new Thickness(18, 0, 18, 8);
        _status.Foreground = new SolidColorBrush(Color.Parse("#8D95A5"));
        var close = new Button { Content = "閉じる", MinWidth = 90 };
        var open = new Button { Content = "ノートを開く", MinWidth = 110, Background = new SolidColorBrush(Color.Parse("#285D4D")) };
        close.Click += (_, _) => Close();
        open.Click += (_, _) => AcceptSelection();
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(16, 0, 16, 16), Children = { close, open } };
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Children = { form, _results, _status, actions }
        };
        Grid.SetRow(_results, 1);
        Grid.SetRow(_status, 2);
        Grid.SetRow(actions, 3);
        Opened += (_, _) => ApplyView();
    }

    public async System.Threading.Tasks.Task<string?> ShowForAsync(Window owner) => await ShowDialog<string?>(owner);

    private static Control Labelled(string label, Control control) => new StackPanel
    {
        Spacing = 3,
        Children = { new TextBlock { Text = label, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#8D95A5")) }, control }
    };

    private static void AddCell(Grid grid, Control control, int column, int row)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        grid.Children.Add(control);
    }

    private void LoadSelectedView()
    {
        if (_saved.SelectedItem is not string selectedName) return;
        var view = _service.Load().FirstOrDefault(item => item.Name == selectedName);
        if (view == null) return;
        _name.Text = view.Name;
        _tag.SelectedItem = view.Tag ?? "すべて";
        _publish.SelectedIndex = view.PublishState switch { "public" => 1, "private" => 2, _ => 0 };
        _from.Text = view.FromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        _through.Text = view.ThroughDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        _folder.Text = view.Folder ?? "";
        _sort.SelectedIndex = view.SortBy switch { "title" => 1, "path" => 2, _ => 0 };
        _descending.IsChecked = view.Descending;
        ApplyView();
    }

    private SavedNoteView ReadForm()
    {
        var from = ParseDate(_from.Text, "開始日");
        var through = ParseDate(_through.Text, "終了日");
        if (from is DateOnly fromDate && through is DateOnly throughDate && fromDate > throughDate)
            throw new InvalidOperationException("開始日は終了日以前の日付にしてください。");
        var name = (_name.Text ?? "").Trim();
        if (name.Length == 0) name = "無題の一覧";
        return new SavedNoteView(name,
            _tag.SelectedIndex > 0 ? _tag.SelectedItem as string : null,
            _publish.SelectedIndex switch { 1 => "public", 2 => "private", _ => "any" },
            from, through, string.IsNullOrWhiteSpace(_folder.Text) ? null : _folder.Text.Trim(),
            _sort.SelectedIndex switch { 1 => "title", 2 => "path", _ => "date" },
            _descending.IsChecked == true);
    }

    private static DateOnly? ParseDate(string? text, string label)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        throw new InvalidOperationException($"{label}は YYYY-MM-DD 形式で入力してください。");
    }

    private void SaveView()
    {
        try
        {
            var view = ReadForm();
            _service.Save(view);
            _name.Text = view.Name;
            _saved.ItemsSource = _service.Load().Select(item => item.Name).ToArray();
            _saved.SelectedItem = view.Name;
            _status.Text = $"「{view.Name}」を保存しました。";
            ApplyView();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            _status.Text = ex.Message;
        }
    }

    private void ApplyView()
    {
        try
        {
            var view = ReadForm();
            _currentResults.Clear();
            _currentResults.AddRange(_service.Apply(_notes, view, _root));
            var format = _display.SelectedIndex;
            var rows = _currentResults.Select(note => Format(note, format)).ToArray();
            _results.ItemsSource = format == 1
                ? TableHeader.Concat(rows).ToArray()
                : rows;
            _status.Text = $"{_currentResults.Count} 件 — ダブルクリックまたはEnterで開く";
        }
        catch (InvalidOperationException ex)
        {
            _status.Text = ex.Message;
        }
    }

    private string Format(Note note, int format)
    {
        var title = note.Metadata.Title ?? Path.GetFileNameWithoutExtension(note.SourcePath);
        var date = note.Metadata.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "日付なし";
        var tags = string.Join(", ", note.Metadata.Tags);
        var path = Path.GetRelativePath(_root, note.SourcePath);
        return format switch
        {
            1 => $"{title,-32} {date,-16} {tags}  {path}",
            2 => $"{title}\n{date}  ·  {(note.Metadata.Publish ? "公開" : "非公開")}  ·  {tags}\n{path}",
            _ => $"{title}  ·  {date}  ·  {path}"
        };
    }

    private void AcceptSelection()
    {
        var index = _results.SelectedIndex;
        if (_display.SelectedIndex == 1) index--;
        if (index < 0 || index >= _currentResults.Count) return;
        _selectedPath = _currentResults[index].SourcePath;
        Close(_selectedPath);
    }
}
