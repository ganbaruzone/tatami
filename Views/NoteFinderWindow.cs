using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tatami.Views;

internal sealed record NoteFinderResult(string Path, int? Line, string Display);

internal sealed class NoteFinderWindow : Window
{
    private const int MaxResults = 300;
    private readonly string _root;
    private readonly bool _searchContents;
    private readonly string[] _paths;
    private readonly IReadOnlyDictionary<string, string> _openDocuments;
    private readonly TextBox _query = new();
    private readonly ListBox _results = new();
    private readonly TextBlock _status = new();
    private CancellationTokenSource? _searchCancellation;
    private IReadOnlyList<NoteFinderResult> _currentResults = [];

    public NoteFinderWindow(string root, bool searchContents, IReadOnlyList<string> paths,
        IReadOnlyDictionary<string, string> openDocuments)
    {
        _root = root;
        _searchContents = searchContents;
        _paths = [.. paths];
        _openDocuments = openDocuments;
        Title = searchContents ? "ノート全体を検索" : "ファイル名で開く";
        Width = 680;
        Height = 560;
        MinWidth = 480;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        _query.PlaceholderText = searchContents ? "ノート本文を検索…" : "ファイル名またはパスを入力…";
        _query.FontSize = 16;
        _query.Margin = new Thickness(16, 16, 16, 8);
        _results.Margin = new Thickness(16, 0, 16, 8);
        _results.DoubleTapped += (_, _) => AcceptSelection();
        _results.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; AcceptSelection(); }
        };
        _query.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            else if (e.Key == Key.Enter) { AcceptSelection(); e.Handled = true; }
            else if (e.Key == Key.Down && _results.ItemCount > 0)
            { _results.SelectedIndex = 0; _results.Focus(); e.Handled = true; }
        };
        _query.TextChanged += async (_, _) => await UpdateResultsAsync();
        _status.Margin = new Thickness(18, 0, 18, 12);
        _status.Foreground = new SolidColorBrush(Color.Parse("#8D95A5"));
        var cancel = new Button { Content = "閉じる", MinWidth = 90 };
        cancel.Click += (_, _) => Close();
        var open = new Button { Content = "開く", MinWidth = 90, Background = new SolidColorBrush(Color.Parse("#285D4D")) };
        open.Click += (_, _) => AcceptSelection();
        var resultHost = new Grid { Children = { _results } };
        Grid.SetRow(resultHost, 1);
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(16, 0, 16, 16),
            Children = { cancel, open }
        };
        Grid.SetRow(actions, 3);
        Grid.SetRow(_status, 2);
        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"),
            Children = { _query, resultHost, _status, actions }
        };
        Opened += async (_, _) => { _query.Focus(); await UpdateResultsAsync(); };
        Closed += (_, _) => _searchCancellation?.Cancel();
    }

    private async Task UpdateResultsAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var query = _query.Text ?? "";
        try
        {
            await Task.Delay(100, cancellation.Token);
            var found = await Task.Run(() => Search(query, cancellation.Token), cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _currentResults = found;
            _results.ItemsSource = found.Select(result => result.Display).ToArray();
            _results.SelectedIndex = found.Count > 0 ? 0 : -1;
            _status.Text = found.Count == 0
                ? (string.IsNullOrWhiteSpace(query) && _searchContents ? "検索語を入力してください。" : "一致するノートがありません。")
                : found.Count == MaxResults ? $"{found.Count} 件以上（表示上限）" : $"{found.Count} 件 — Enterで開く";
        }
        catch (OperationCanceledException) { }
    }

    private IReadOnlyList<NoteFinderResult> Search(string query, CancellationToken cancellationToken)
    {
        var results = new List<NoteFinderResult>();
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!_searchContents)
        {
            return [.. _paths.Select(path => new { Path = path, Relative = Path.GetRelativePath(_root, path) })
                .Where(item => MatchesFilename(item.Relative, query))
                .OrderBy(item => FilenameScore(item.Relative, query))
                .ThenBy(item => item.Relative, StringComparer.OrdinalIgnoreCase)
                .Take(MaxResults)
                .Select(item => new NoteFinderResult(item.Path, null, item.Relative))];
        }

        if (string.IsNullOrWhiteSpace(query)) return results;
        foreach (var path in _paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                IEnumerable<string> lines;
                if (_openDocuments.TryGetValue(path, out var openText)) lines = ReadLines(openText);
                else lines = File.ReadLines(path);
                var lineNumber = 0;
                foreach (var line in lines)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    lineNumber++;
                    if (!line.Contains(query, comparison)) continue;
                    var relative = Path.GetRelativePath(_root, path);
                    var snippet = line.Trim().Replace('\t', ' ');
                    if (snippet.Length > 180) snippet = snippet[..177] + "…";
                    results.Add(new NoteFinderResult(path, lineNumber, $"{relative}:{lineNumber}: {snippet}"));
                    if (results.Count >= MaxResults) return results;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) { }
        }
        return results;
    }

    private static IEnumerable<string> ReadLines(string text)
    {
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    private static bool MatchesFilename(string path, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var comparison = StringComparison.OrdinalIgnoreCase;
        if (path.Contains(query, comparison)) return true;
        var queryIndex = 0;
        foreach (var character in path)
            if (char.ToUpperInvariant(character) == char.ToUpperInvariant(query[queryIndex]) && ++queryIndex == query.Length)
                return true;
        return false;
    }

    private static int FilenameScore(string path, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return 2;
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (name.Equals(query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (path.Contains(query, StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }

    private void AcceptSelection()
    {
        if (_currentResults.Count == 0) return;
        var index = _results.SelectedIndex < 0 ? 0 : _results.SelectedIndex;
        if (index >= _currentResults.Count) return;
        Close(_currentResults[index]);
    }
}
