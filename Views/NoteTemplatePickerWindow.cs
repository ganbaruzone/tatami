using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Tatami.Services;

namespace Tatami.Views;

internal sealed class NoteTemplatePickerWindow : Window
{
    private readonly NoteTemplateService _templates = new();
    private readonly ComboBox _choices = new();
    private readonly TextBlock _preview = new();

    public NoteTemplatePickerWindow()
    {
        Title = "ノートのテンプレート";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
        _choices.ItemsSource = _templates.Templates.Select(template => template.Name).ToArray();
        _choices.SelectedIndex = 0;
        _choices.SelectionChanged += (_, _) => UpdatePreview();
        _preview.TextWrapping = TextWrapping.Wrap;
        _preview.MinHeight = 110;
        _preview.Padding = new Thickness(12);
        _preview.Background = new SolidColorBrush(Color.Parse("#181A1F"));
        var create = new Button { Content = "作成", MinWidth = 90, Background = new SolidColorBrush(Color.Parse("#285D4D")) };
        var cancel = new Button { Content = "キャンセル", MinWidth = 90, IsCancel = true };
        create.Click += (_, _) =>
        {
            if (_choices.SelectedIndex >= 0) Close(_templates.Templates[_choices.SelectedIndex].Id);
        };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "挿入するFront Matterと本文のひな形を選んでください。", TextWrapping = TextWrapping.Wrap },
                _choices,
                _preview,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { cancel, create } }
            }
        };
        Opened += (_, _) => UpdatePreview();
    }

    public async System.Threading.Tasks.Task<string?> ShowForAsync(Window owner) => await ShowDialog<string?>(owner);

    private void UpdatePreview()
    {
        if (_choices.SelectedIndex < 0) return;
        var template = _templates.Templates[_choices.SelectedIndex];
        _preview.Text = _templates.Render(template.Id, "サンプルタイトル", System.DateOnly.FromDateTime(System.DateTime.Today));
    }
}
