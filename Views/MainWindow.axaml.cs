using System;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Tatami.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    private async void Read_File(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select a File",
            AllowMultiple = false,
            FileTypeFilter =
            [
            new FilePickerFileType("Text Files") { Patterns = ["*.txt"] },
            new FilePickerFileType("All Files") { Patterns = ["*"] },
            ]
        });

        if (files.Count > 0)
        {
            var file = files[0];
            await using var stream = await file.OpenReadAsync();
            StreamReader sr = new(stream);
            content.Text = sr.ReadToEnd();
        }
    }

    private async void GetFiles(object? sender, RoutedEventArgs e)
    {
        var folder = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "フォルダを選択してください",
            AllowMultiple = false,
        });

        if (folder.Count > 0)
        {
            DirectoryInfo di = new(folder[0].Path.LocalPath);

            Folder_Contents.Text = string.Join(
                Environment.NewLine,
                di.GetFiles().Select(x => x.Name));
        }
    }
}