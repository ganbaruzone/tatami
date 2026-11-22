using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private sealed record BuildDiagnostic(string Path, int Line, string Message);
    private static readonly Regex ParenthesizedDiagnostic = MyRegex();
    private static readonly Regex ColonDiagnostic = new(@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.md):(?<line>\d+)(?::\d+)?(?:\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly List<BuildDiagnostic> _diagnostics = [];

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

#pragma warning disable IDE0060 // Parameters are required by the XAML event binding.
    private async void ShowDiagnostics(object? sender, RoutedEventArgs e) =>
        await RunAsync(ShowDiagnosticsAsync);
#pragma warning restore IDE0060

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

#pragma warning disable IDE0060 // Parameters are required by the XAML event binding.
    private void ClearOutput(object? sender, RoutedEventArgs e)
    {
        OutputLog.Text = "";
        _diagnostics.Clear();
        DiagnosticsButton.IsVisible = false;
        ProblemSummary.Text = "出力をクリアしました";
    }
#pragma warning restore IDE0060

    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:)?[^\r\n]*?\.md)\((?<line>\d+)(?:,\d+)?\)", RegexOptions.IgnoreCase | RegexOptions.Compiled, "ja-JP")]
    private static partial Regex MyRegex();
}
