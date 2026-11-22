using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tatami.Services;

namespace Tatami.Views;

internal static class NoteDialogs
{
    private static Window Create(string title) => new()
    {
        Title = title, Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark,
        Background = new SolidColorBrush(Color.Parse("#202229"))
    };

    public static Window Name(string title, string initialName, string location)
    {
        var window = Create(title);
        var input = new TextBox { Text = initialName };
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
        var accept = new Button { Content = "確定", IsDefault = true };
        accept.Click += (_, _) =>
        {
            try { window.Close(NoteFileService.NormalizeName(input.Text ?? "")); }
            catch (System.IO.InvalidDataException ex) { error.Text = ex.Message; }
        };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, FontSize = 18 },
                new TextBlock { Text = location, TextWrapping = TextWrapping.Wrap, FontSize = 11 },
                input, error,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
            }
        };
        window.Opened += (_, _) => { input.Focus(); input.SelectionStart = 0; input.SelectionEnd = initialName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? initialName.Length - 3 : initialName.Length; };
        return window;
    }

    public static Window Trash(string name)
    {
        var window = Create("Tatamiのゴミ箱へ移動");
        var accept = new Button { Content = "ゴミ箱へ移動" };
        accept.Click += (_, _) => window.Close(true);
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        cancel.Click += (_, _) => window.Close(false);
        window.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = $"{name} をTatamiのゴミ箱へ移動しますか？", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "「復元」から元に戻せます。公開済みHTMLへの反映には、移動後に再ビルドしてください。", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, accept } }
            }
        };
        return window;
    }

    public static Window Restore(IReadOnlyList<TrashedNote> entries)
    {
        var window = Create("Tatamiのゴミ箱から復元");
        var list = new ListBox { ItemsSource = entries, Height = 240, SelectedIndex = entries.Count > 0 ? 0 : -1 };
        var restore = new Button { Content = "選択したノートを復元", IsEnabled = entries.Count > 0 };
        restore.Click += (_, _) => { if (list.SelectedItem is TrashedNote note) window.Close(note.Id); };
        var cancel = new Button { Content = "キャンセル", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = new StackPanel
        {
            Margin = new Thickness(24), Spacing = 16,
            Children =
            {
                new TextBlock { Text = entries.Count == 0 ? "ゴミ箱は空です。" : "復元するノートを選んでください。同名ファイルは上書きしません。", TextWrapping = TextWrapping.Wrap },
                list,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, restore } }
            }
        };
        return window;
    }
}
