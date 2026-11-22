using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Tatami.Models;

namespace Tatami.Views;

public sealed class BuildSettingsWindow : Window
{
    public BuildSettingsWindow(string builder, ExternalBuildOptions options)
    {
        Title = "ビルダー設定";
        Width = 650; Height = 620; MinWidth = 500; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        var mode = new ComboBox { ItemsSource = new[] { "Tatami 内蔵", "外部コマンド" }, SelectedIndex = builder == "external" ? 1 : 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var project = new TextBox { Text = options.ProjectPath, PlaceholderText = "外部ビルダーのプロジェクトフォルダ" };
        var executable = new TextBox { Text = options.Executable, PlaceholderText = "node または実行ファイルの絶対パス" };
        var arguments = new TextBox { Text = string.Join('\n', options.Arguments), AcceptsReturn = true, MinHeight = 90, PlaceholderText = "引数を1行ずつ入力（引用符は不要）" };
        var timeout = new TextBox { Text = options.TimeoutSeconds.ToString() };
        var browse = new Button { Content = "プロジェクトを選択…" };
        browse.Click += async (_, _) =>
        {
            var selected = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { AllowMultiple = false });
            if (selected.Count > 0) project.Text = selected[0].TryGetLocalPath();
        };
        var preset = new Button { Content = "Node.jsのビルド設定を入力" };
        preset.Click += (_, _) =>
        {
            mode.SelectedIndex = 1;
            executable.Text = File.Exists("/opt/homebrew/bin/node") ? "/opt/homebrew/bin/node" : File.Exists("/usr/local/bin/node") ? "/usr/local/bin/node" : "node";
            arguments.Text = "--run\nbuild";
        };
        var error = new TextBlock { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "設定を保存" };
        save.Click += (_, _) =>
        {
            if (!int.TryParse(timeout.Text, out var seconds) || seconds is < 1 or > 86400)
            { error.Text = "タイムアウトは1〜86400秒で指定してください。"; return; }
            if (mode.SelectedIndex == 1 && (!Directory.Exists(project.Text) || string.IsNullOrWhiteSpace(executable.Text)))
            { error.Text = "存在するプロジェクトフォルダと実行ファイルを指定してください。"; return; }
            Close(new SiteConfig("", "")
            {
                Builder = mode.SelectedIndex == 1 ? "external" : "builtin",
                External = new ExternalBuildOptions
                {
                    ProjectPath = project.Text ?? "",
                    Executable = executable.Text ?? "",
                    Arguments = (arguments.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries),
                    TimeoutSeconds = seconds
                }
            });
        };
        var cancel = new Button { Content = "キャンセル" };
        cancel.Click += (_, _) => Close();
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "使用するビルダー", FontSize = 20 }, mode, preset,
                    new TextBlock { Text = "プロジェクトフォルダ" }, project, browse,
                    new TextBlock { Text = "実行ファイル" }, executable,
                    new TextBlock { Text = "引数（1行につき1引数）" }, arguments,
                    new TextBlock { Text = "タイムアウト（秒）" }, timeout,
                    new TextBlock { Text = "外部ツールは公開用Markdownを TATAMI_CONTENT_DIR から読み、生成物を TATAMI_OUTPUT_DIR に書き出すよう設定してください。引数の {content} / {output} でも渡せます。", TextWrapping = TextWrapping.Wrap, FontSize = 12 },
                    error,
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, save } }
                }
            }
        };
    }
}
