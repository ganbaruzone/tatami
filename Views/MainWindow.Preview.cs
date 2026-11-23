using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Tatami.Services;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _livePreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private NativeWebView? _webView;
    private bool _refreshingPages;
    private bool _livePreviewActive;
    private readonly PreviewServer _previewServer = new();

    private async Task<bool> ChooseOutputFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        { Title = "HTML出力先（メモとは別に保存されます）", AllowMultiple = false });
        if (folders.Count == 0) return false;
        var path = Path.TrimEndingDirectorySeparator(folders[0].TryGetLocalPath()
            ?? throw new IOException("ローカルフォルダを選択してください。"));
        if (_notesPath != null && PathSafety.IsWithin(_notesPath, path))
            throw new IOException("メモと同じディレクトリや、その親をHTML出力先には指定できません。");
        PathSafety.RejectLinks(path);
        _outputPath = path;
        OutputPathLabel.Text = DisplayDirectoryName(path);
        ToolTip.SetTip(OutputPathLabel, path);
        // Clear stale pages from a previously selected output directory.
        _refreshingPages = true;
        try { PreviewPages.ItemsSource = null; }
        finally { _refreshingPages = false; }
        _webView?.Navigate(new Uri("about:blank"));
        await _previewServer.DisposeAsync();
        PreviewStatus.Text = "出力先を変更しました。HTMLプレビューを開くかビルドしてください。";
        await _settings.SaveAsync(CurrentConfig);
        Log($"HTML出力先を設定しました: {path}");
        return true;
    }

    private async void SelectOutputFolder(object? sender, RoutedEventArgs e) =>
        await RunAsync(async () => { await ChooseOutputFolderAsync(); });

    private async void TogglePreview(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (PreviewPane.IsVisible) { PreviewPane.IsVisible = false; EditingPane.IsVisible = true; }
        else await ShowPreviewAsync();
    });

    private async Task ShowPreviewAsync()
    {
        if (_outputPath == null) { Log("先にHTML出力先を選択してビルドしてください。"); return; }
        var pages = await PreviewCatalog.ReadAsync(_outputPath);
        await _previewServer.StartAsync(_outputPath);
        _livePreviewActive = _buffer != null;
        _refreshingPages = true;
        try
        {
            PreviewPages.ItemsSource = pages;
            string? slug = null;
            if (_buffer != null)
            {
                var note = new NoteRepository().Parse(_buffer.Path, _buffer.Text);
                if (note.Metadata.Publish)
                    slug = note.Metadata.Slug ?? PathSafety.FileSlug(Path.GetFileNameWithoutExtension(_buffer.Path));
            }
            PreviewPages.SelectedItem = pages.FirstOrDefault(page => page.Name == slug) ?? pages.FirstOrDefault();
        }
        finally { _refreshingPages = false; }
        EditingPane.IsVisible = false;
        PreviewPane.IsVisible = true;
        await UpdateLivePreviewAsync();
        NavigateSelectedPage();
    }

    private void RequestLivePreviewRefresh()
    {
        _livePreviewActive = _buffer != null;
        if (_buffer == null || _outputPath == null) return;
        _livePreviewTimer.Stop();
        _livePreviewTimer.Start();
    }

    private async Task UpdateLivePreviewAsync()
    {
        _livePreviewTimer.Stop();
        if (_buffer == null || _outputPath == null || _previewServer.BaseUri == null) return;
        try
        {
            var text = Editor.Text;
            if (_metadataPending)
                text = NoteMetadataEditor.Apply(text, MetadataTitle.Text, MetadataPublish.IsChecked == true, MetadataSlug.Text);
            var note = new NoteRepository().Parse(_buffer.Path, text);
            var notes = new NoteRepository().ReadAll(_notesPath!, _outputPath).ToList();
            var existingIndex = notes.FindIndex(item => string.Equals(item.SourcePath, note.SourcePath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
            if (existingIndex >= 0) notes[existingIndex] = note;
            else notes.Add(note);
            var routes = _noteLinks.BuildPublishedRoutes(notes);
            var liveAttachments = _attachments.FindReferenced(_notesPath!, note.SourcePath, PrivateContentFilter.Remove(note.Markdown));
            var assetRoutes = liveAttachments.ToDictionary(
                asset => Path.GetFullPath(asset.Path),
                asset => "__tatami_live_assets/" + AttachmentService.LivePreviewToken(asset.Path),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var renderer = new TemplateRenderer(Path.Combine(AppContext.BaseDirectory, "Templates", "default.html"), routes,
                assetRoutes, rootRelativeNoteLinks: true);
            _previewServer.SetLivePreview(renderer.Render(note), liveAttachments.ToDictionary(
                asset => AttachmentService.LivePreviewToken(asset.Path), asset => asset.Path, StringComparer.Ordinal), _notesPath);
            if (_livePreviewActive)
            {
                PreviewStatus.Text = "編集中の記事のライブプレビュー";
                NavigateLivePreview();
            }
        }
        catch (Exception ex)
        {
            PreviewStatus.Text = $"ライブプレビューを更新できません: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    private void NavigateLivePreview()
    {
        var uri = _previewServer.GetLivePreviewUri();
        EnsureWebView();
        PreviewStatus.Text = "編集中の記事のライブプレビュー";
        if (_webView!.Source == uri) _webView.Refresh();
        else _webView.Navigate(uri);
    }

    private void NavigateSelectedPage()
    {
        if (_livePreviewActive && _buffer != null)
        {
            if (_previewServer.BaseUri != null && _previewServer.GetLivePreviewUri() is { } liveUri)
            {
                EnsureWebView();
                if (_webView!.Source == liveUri) _webView.Refresh(); else _webView.Navigate(liveUri);
            }
            return;
        }
        if (PreviewPages.SelectedItem is not PreviewPage page)
        {
            _webView?.Navigate(new Uri("about:blank"));
            PreviewStatus.Text = "公開済みの記事がありません。publish: true を指定してビルドしてください。";
            return;
        }
        var uri = _previewServer.GetUri(page.FullPath);
        if (!_previewServer.CanNavigate(uri))
            throw new IOException("プレビュー対象のHTMLが見つからないか、許可された出力先の範囲外です。");
        EnsureWebView();
        PreviewStatus.Text = $"読み込み中: {page.Name}";
        if (_webView!.Source == uri) _webView.Refresh();
        else _webView.Navigate(uri);
    }

    private void EnsureWebView()
    {
        if (_webView != null) return;
        _webView = new NativeWebView();
        _webView.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            PreviewStatus.Text = "プレビューから別ウィンドウは開きません。";
        };
        _webView.NavigationStarted += (_, args) =>
        {
            if (args.Request == null || !_previewServer.CanNavigate(args.Request))
            {
                args.Cancel = true;
                PreviewStatus.Text = "プレビューでは出力先のHTMLまたは添付ファイルだけを開けます。";
            }
        };
        _webView.NavigationCompleted += (_, args) =>
        {
            if (args.Request == null || args.Request.ToString() == "about:blank") return;
            if (_livePreviewActive) PreviewStatus.Text = "編集中の記事のライブプレビュー";
            else PreviewStatus.Text = args.IsSuccess
                ? "ビルド済みHTML — 編集の反映には再ビルドが必要です。"
                : "HTMLを読み込めませんでした。WebViewの実行環境とファイルを確認してください。";
            if (!args.IsSuccess) Log(PreviewStatus.Text, true);
        };
        PreviewHost.Content = _webView;
    }

    private void PreviewPageSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_refreshingPages) return;
        _livePreviewActive = false;
        try { NavigateSelectedPage(); }
        catch (Exception ex) { Log(ex.Message, true); }
    }

    private async void ReloadPreview(object? sender, RoutedEventArgs e) => await RunAsync(ShowPreviewAsync);

}
