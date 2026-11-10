using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class StorageSummaryView : UserControl
{
    public StorageSummaryView()
    {
        InitializeComponent();

        // A click on a block of the bar selects that category.
        Bar.SegmentPressed += (_, colorKey) => (DataContext as StorageSummaryViewModel)?.SelectByColorKey(colorKey);
        LegendList.Tapped += OnLegendTapped;
        LegendList.KeyDown += OnLegendKeyDown;
    }

    private void OnLegendTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is StorageSummaryViewModel summary
            && (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { DataContext: CategoryRowViewModel row }
            && row == summary.Unaccounted)
        {
            summary.ActivateCategory(row);
        }
    }

    private void OnLegendKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space
            && DataContext is StorageSummaryViewModel summary
            && LegendList.SelectedItem is CategoryRowViewModel row
            && row == summary.Unaccounted)
        {
            summary.ActivateCategory(row);
            e.Handled = true;
        }
    }
}
