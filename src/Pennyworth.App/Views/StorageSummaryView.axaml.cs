using Avalonia.Controls;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class StorageSummaryView : UserControl
{
    public StorageSummaryView()
    {
        InitializeComponent();

        // A click on a block of the bar selects that category.
        Bar.SegmentPressed += (_, colorKey) => (DataContext as StorageSummaryViewModel)?.SelectByColorKey(colorKey);
    }
}
