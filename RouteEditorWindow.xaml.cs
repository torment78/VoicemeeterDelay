using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VoicemeeterDelay;

public partial class RouteEditorWindow : Window
{
    private static readonly MediaBrush PanelBrush = new MediaSolidColorBrush(MediaColor.FromRgb(26, 32, 37));
    private static readonly MediaBrush FieldBrush = new MediaSolidColorBrush(MediaColor.FromRgb(17, 23, 27));
    private static readonly MediaBrush TextBrush = new MediaSolidColorBrush(MediaColor.FromRgb(237, 243, 246));
    private static readonly MediaBrush SubtleBorderBrush = new MediaSolidColorBrush(MediaColor.FromRgb(51, 65, 74));

    private readonly IReadOnlyList<RouteBusChoice> _buses;
    private readonly List<RouteDestinationSnapshot> _destinations;
    private readonly Action _changed;
    private bool _updating;

    internal RouteEditorWindow(
        string sourceName,
        IReadOnlyList<RouteBusChoice> buses,
        List<RouteDestinationSnapshot> destinations,
        Action changed)
    {
        InitializeComponent();
        _buses = buses;
        _destinations = destinations;
        _changed = changed;

        TitleTextBlock.Text = sourceName;
        EnsureAtLeastOneDestination();
        RebuildRows();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        _destinations.Add(new RouteDestinationSnapshot());
        RebuildRows();
        NotifyChanged();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void RebuildRows()
    {
        _updating = true;
        try
        {
            RoutesPanel.Children.Clear();
            for (var index = 0; index < _destinations.Count; index++)
            {
                RoutesPanel.Children.Add(CreateRouteRow(index));
            }
        }
        finally
        {
            _updating = false;
        }
    }

    private UIElement CreateRouteRow(int index)
    {
        var destination = _destinations[index];
        var row = new Border
        {
            Background = PanelBrush,
            BorderBrush = SubtleBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(92) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
        row.Child = grid;

        var busComboBox = CreateBusComboBox(index, destination);
        Grid.SetColumn(busComboBox, 0);
        grid.Children.Add(busComboBox);

        var channelComboBox = CreateChannelComboBox(index, destination, busComboBox.SelectedItem as RouteBusChoice);
        Grid.SetColumn(channelComboBox, 2);
        grid.Children.Add(channelComboBox);

        busComboBox.SelectionChanged += (_, _) =>
        {
            if (_updating || busComboBox.SelectedItem is not RouteBusChoice bus)
            {
                return;
            }

            destination.BusIndex = bus.Index;
            destination.ChannelOffset = Math.Clamp(destination.ChannelOffset, 0, bus.ChannelCount - 1);
            RebuildRows();
            NotifyChanged();
        };

        channelComboBox.SelectionChanged += (_, _) =>
        {
            if (_updating || channelComboBox.SelectedItem is not int channel)
            {
                return;
            }

            destination.ChannelOffset = channel - 1;
            NotifyChanged();
        };

        var removeButton = new Button
        {
            Content = "Remove",
            Width = 78,
            MinHeight = 28,
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = SubtleBorderBrush,
            ToolTip = "Remove this output destination from the route list."
        };
        removeButton.Click += (_, _) =>
        {
            if (_destinations.Count <= 1)
            {
                _destinations[0].BusIndex = 0;
                _destinations[0].ChannelOffset = 0;
            }
            else
            {
                _destinations.RemoveAt(index);
            }

            RebuildRows();
            NotifyChanged();
        };
        Grid.SetColumn(removeButton, 4);
        grid.Children.Add(removeButton);

        return row;
    }

    private ComboBox CreateBusComboBox(int index, RouteDestinationSnapshot destination)
    {
        var comboBox = CreateComboBox(index);
        comboBox.ToolTip = "Choose which Voicemeeter output bus receives this routed channel.";
        foreach (var bus in _buses)
        {
            comboBox.Items.Add(bus);
        }

        if (_buses.Count > 0)
        {
            var selectedIndex = _buses
                .Select((bus, itemIndex) => new { bus, itemIndex })
                .FirstOrDefault(item => item.bus.Index == destination.BusIndex)
                ?.itemIndex ?? 0;
            comboBox.SelectedIndex = selectedIndex;
        }

        return comboBox;
    }

    private ComboBox CreateChannelComboBox(int index, RouteDestinationSnapshot destination, RouteBusChoice? bus)
    {
        var comboBox = CreateComboBox(index);
        comboBox.ToolTip = "Choose the destination channel inside the selected output bus.";
        var channelCount = bus?.ChannelCount ?? 8;
        for (var channel = 1; channel <= channelCount; channel++)
        {
            comboBox.Items.Add(channel);
        }

        comboBox.SelectedIndex = Math.Clamp(destination.ChannelOffset, 0, Math.Max(0, channelCount - 1));
        return comboBox;
    }

    private static ComboBox CreateComboBox(int index)
    {
        return new ComboBox
        {
            Tag = index,
            MinHeight = 28,
            Background = FieldBrush,
            Foreground = TextBrush,
            BorderBrush = SubtleBorderBrush,
            Margin = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center
        };
    }

    private void EnsureAtLeastOneDestination()
    {
        if (_destinations.Count == 0)
        {
            _destinations.Add(new RouteDestinationSnapshot());
        }
    }

    private void NotifyChanged()
    {
        foreach (var destination in _destinations)
        {
            destination.BusIndex = Math.Max(0, destination.BusIndex);
            destination.ChannelOffset = Math.Max(0, destination.ChannelOffset);
        }

        _changed();
    }
}
