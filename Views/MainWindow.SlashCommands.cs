using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit.Rendering;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow
{
    private readonly SlashCommandPalette _slashCommandPalette;
    private int _slashCommandStart = -1;

    private void OnEditorTextEntered(object? sender, TextInputEventArgs e)
    {
        if (e.Text?.Contains('/', StringComparison.Ordinal) == true)
        {
            TryOpenSlashCommandPalette();
            return;
        }
        if (_slashCommandPalette.IsOpen) RefreshSlashCommandPalette();
    }

    private void TryOpenSlashCommandPalette()
    {
        var caretOffset = Editor.TextArea.Caret.Offset;
        if (caretOffset == 0 || Editor.Document.GetCharAt(caretOffset - 1) != '/') return;

        var line = Editor.Document.GetLineByOffset(caretOffset);
        var slashOffset = caretOffset - 1;
        var prefix = Editor.Document.GetText(line.Offset, slashOffset - line.Offset);
        if (!string.IsNullOrWhiteSpace(prefix)) return;

        _slashCommandStart = slashOffset;
        _slashCommandPalette.Show(Editor, GetSlashCommandPlacement(), "");
    }

    private void RefreshSlashCommandPalette()
    {
        var caretOffset = Editor.TextArea.Caret.Offset;
        if (_slashCommandStart < 0 || caretOffset <= _slashCommandStart || caretOffset > Editor.Document.TextLength
            || Editor.Document.GetCharAt(_slashCommandStart) != '/')
        {
            CloseSlashCommandPalette();
            return;
        }

        var query = Editor.Document.GetText(_slashCommandStart + 1, caretOffset - _slashCommandStart - 1);
        if (query.Contains('\n') || query.Contains('\r'))
        {
            CloseSlashCommandPalette();
            return;
        }
        _slashCommandPalette.Update(query);
        _slashCommandPalette.UpdatePosition(GetSlashCommandPlacement());
    }

    private Rect GetSlashCommandPlacement()
    {
        var textView = Editor.TextArea.TextView;
        var visual = textView.GetVisualPosition(Editor.TextArea.Caret.Position, VisualYPosition.LineBottom);
        var viewport = visual - textView.ScrollOffset;
        var editorPoint = textView.TranslatePoint(viewport, Editor) ?? new Point(12, 12);
        return new Rect(editorPoint.X, editorPoint.Y, 1, Math.Max(1, textView.DefaultLineHeight));
    }

    private void CloseSlashCommandPalette()
    {
        _slashCommandStart = -1;
        _slashCommandPalette.Close();
    }

    private async void InsertSlashCommand(SlashCommand command)
    {
        var start = _slashCommandStart;
        var end = Editor.TextArea.Caret.Offset;
        if (start < 0 || end <= start) return;
        CloseSlashCommandPalette();
        Editor.Select(start, end - start);
        if (command.Id == "image")
        {
            Editor.TextArea.PerformTextInput("");
            Editor.TextArea.Focus();
            await RunAsync(ImportAttachmentAsync);
            return;
        }

        Editor.TextArea.PerformTextInput(command.Insertion);
        if (!string.IsNullOrEmpty(command.Placeholder))
        {
            var placeholderOffset = command.Insertion.IndexOf(command.Placeholder, StringComparison.Ordinal);
            if (placeholderOffset >= 0) Editor.Select(start + placeholderOffset, command.Placeholder.Length);
        }
        Editor.TextArea.Focus();
    }

    private bool HandleSlashCommandKey(KeyEventArgs e)
    {
        if (!_slashCommandPalette.IsOpen) return false;
        switch (e.Key)
        {
            case Key.Escape:
                CloseSlashCommandPalette();
                e.Handled = true;
                return true;
            case Key.Up:
                _slashCommandPalette.MoveSelection(-1);
                e.Handled = true;
                return true;
            case Key.Down:
                _slashCommandPalette.MoveSelection(1);
                e.Handled = true;
                return true;
            case Key.Enter:
            case Key.Tab:
                _slashCommandPalette.SelectCurrent();
                e.Handled = true;
                return true;
            default:
                return false;
        }
    }
}
