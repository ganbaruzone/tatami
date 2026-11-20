using System.IO;
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

    private async void Button_OnClick(object? sender, RoutedEventArgs e)
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
}