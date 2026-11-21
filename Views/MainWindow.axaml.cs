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
            Folder_Contents.ItemsSource = di.GetFiles();
        }
    }
    private async void File_Selected(
        object? sender, SelectionChangedEventArgs e
    )
    {

        if (Folder_Contents.SelectedItem is FileInfo file)
        {
            content.Text = await File.ReadAllTextAsync(file.FullName);
        }
    }
}