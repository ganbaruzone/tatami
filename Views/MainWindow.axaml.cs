using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Tatami.Services;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    private string? _notesPath;

    public MainWindow()
    {
        InitializeComponent();
    }
    private async void GetFiles(object? sender, RoutedEventArgs e)
    {
        try { await SelectNotesFolderAsync(); }
        catch (Exception ex) { BuildStatus.Text = $"フォルダを開けません: {ex.Message}"; }
    }

    private async Task<bool> SelectNotesFolderAsync()
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Markdownの保存先フォルダを選択してください",
            AllowMultiple = false
        });
        if (folders.Count == 0) return false;
        var path = folders[0].TryGetLocalPath()
            ?? throw new IOException("ローカルフォルダを選択してください。");
        var files = await Task.Run(() => new DirectoryInfo(path).GetFiles());
        _notesPath = path;
        Folder_Contents.ItemsSource = files;
        return true;
    }

    private async void BuildSite(object? sender, RoutedEventArgs e)
    {
        BuildSiteButton.IsEnabled = false;
        SelectFolderButton.IsEnabled = false;
        try
        {
            if (_notesPath == null && !await SelectNotesFolderAsync()) return;
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "サイトの出力先フォルダを選択してください",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var output = folders[0].TryGetLocalPath()
                ?? throw new IOException("ローカルフォルダを選択してください。");
            BuildStatus.Text = "サイトをビルドしています…";
            var result = await new SiteBuilder().BuildAsync(_notesPath!, output);
            BuildStatus.Text = $"ビルド完了: 公開 {result.PublishedCount} 件、非公開 {result.PrivateCount} 件\n{result.OutputPath}";
        }
        catch (Exception ex) { BuildStatus.Text = $"ビルド失敗: {ex.Message}"; }
        finally
        {
            BuildSiteButton.IsEnabled = true;
            SelectFolderButton.IsEnabled = true;
        }
    }
    private async void File_Selected(
        object? sender, SelectionChangedEventArgs e
    )
    {

        if (Folder_Contents.SelectedItem is FileInfo file)
        {
            try { content.Text = await File.ReadAllTextAsync(file.FullName); }
            catch (Exception ex) { BuildStatus.Text = $"読み込み失敗: {ex.Message}"; }
        }
    }
}