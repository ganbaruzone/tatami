using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Layout;
using Avalonia.Media;
using Tatami.Services;

namespace Tatami.Views;

internal sealed class SlashCommandPalette
{
    private readonly SlashCommandService _service = new();
    private readonly Popup _popup;
    private readonly StackPanel _items = new() { Spacing = 2 };
    private readonly Border _surface;
    private IReadOnlyList<SlashCommand> _filtered = [];
    private readonly List<Button> _buttons = [];
    private int _selectedIndex;

    public SlashCommandPalette(Action<SlashCommand> onSelected)
    {
        var heading = new TextBlock
        {
            Text = "挿入コマンド",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#8D95A5")),
            Margin = new Thickness(8, 6, 8, 8)
        };
        _surface = new Border
        {
            Width = 330,
            MaxHeight = 330,
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Color.Parse("#202229")),
            BorderBrush = new SolidColorBrush(Color.Parse("#454B58")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, OffsetY = 5, Color = Color.FromArgb(90, 0, 0, 0) }),
            Child = new StackPanel
            {
                Spacing = 2,
                Children = { heading, new ScrollViewer { MaxHeight = 290, Content = _items } }
            }
        };
        _popup = new Popup
        {
            Child = _surface,
            Placement = PlacementMode.AnchorAndGravity,
            PlacementAnchor = PopupAnchor.BottomLeft,
            PlacementGravity = PopupGravity.BottomRight,
            PlacementConstraintAdjustment = PopupPositionerConstraintAdjustment.FlipY | PopupPositionerConstraintAdjustment.SlideX | PopupPositionerConstraintAdjustment.SlideY,
            IsLightDismissEnabled = true
        };
        _popup.Closed += (_, _) => _filtered = [];
        Selected = onSelected;
    }

    private Action<SlashCommand> Selected { get; }
    public bool IsOpen => _popup.IsOpen;

    public void Show(Control target, Rect caretRect, string query)
    {
        _popup.PlacementTarget = target;
        _popup.PlacementRect = caretRect;
        Update(query);
        _popup.IsOpen = true;
    }

    public void UpdatePosition(Rect caretRect) => _popup.PlacementRect = caretRect;

    public void Update(string query)
    {
        _filtered = _service.Filter(query.Trim());
        _selectedIndex = 0;
        _items.Children.Clear();
        _buttons.Clear();
        if (_filtered.Count == 0)
        {
            _items.Children.Add(new TextBlock
            {
                Text = "一致するコマンドがありません",
                Foreground = new SolidColorBrush(Color.Parse("#8D95A5")),
                Margin = new Thickness(8)
            });
            return;
        }

        foreach (var command in _filtered)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(9, 7),
                Background = Brushes.Transparent,
                Content = new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = command.Title, FontSize = 13, Foreground = new SolidColorBrush(Color.Parse("#E5E7EB")) },
                        new TextBlock { Text = command.Description, FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#9AA3B2")), TextWrapping = TextWrapping.Wrap }
                    }
                }
            };
            button.Click += (_, _) => Selected(command);
            _buttons.Add(button);
            _items.Children.Add(button);
        }
        RefreshSelection();
    }

    public void MoveSelection(int delta)
    {
        if (_filtered.Count == 0) return;
        _selectedIndex = (_selectedIndex + delta + _filtered.Count) % _filtered.Count;
        RefreshSelection();
    }

    public void SelectCurrent()
    {
        if (_selectedIndex >= 0 && _selectedIndex < _filtered.Count)
            Selected(_filtered[_selectedIndex]);
    }

    public void Close() => _popup.IsOpen = false;

    private void RefreshSelection()
    {
        for (var index = 0; index < _buttons.Count; index++)
            _buttons[index].Background = new SolidColorBrush(Color.Parse(index == _selectedIndex ? "#343A46" : "#202229"));
    }
}
