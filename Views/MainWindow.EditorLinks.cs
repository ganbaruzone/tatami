using System;
using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Interactivity;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow
{
    private async void OnEditorPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (_buffer == null || _notesPath == null || e.KeyModifiers != modifier
            || !e.GetCurrentPoint(Editor).Properties.IsLeftButtonPressed) return;

        var position = Editor.GetPositionFromPoint(e.GetPosition(Editor));
        if (position == null) return;
        var offset = Editor.Document.GetOffset(position.Value.Line, position.Value.Column);
        if (!_noteLinks.TryGetLinkAtOffset(_buffer.Path, Editor.Text, offset, out var url)) return;

        e.Handled = true;
        await RunAsync(async () =>
        {
            if (NoteLinkService.TryGetWebUri(url, out var uri))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    Log($"既定のブラウザーで開きました: {uri}");
                }
                catch (Exception error)
                {
                    Log($"Webリンクを開けませんでした: {error.Message}", true);
                }
                return;
            }

            if (_noteLinks.TryResolveNotePath(_notesPath, _buffer.Path, url, out var targetPath, out _))
            {
                await OpenTabAsync(targetPath);
                Log($"リンク先のノートを開きました: {targetPath}");
            }
            else Log($"リンク先のノートが見つかりません: {url}", true);
        });
    }
}
