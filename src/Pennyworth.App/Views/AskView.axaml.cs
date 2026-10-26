using Avalonia;
using Avalonia.Controls;

namespace Pennyworth.App.Views;

public partial class AskView : UserControl
{
    public AskView()
    {
        InitializeComponent();

        // Follow the answer as it streams, unless the person scrolled up to read.
        MessagesScroll.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty && e.OldValue is Size old && MessagesScroll.Offset.Y + MessagesScroll.Viewport.Height >= old.Height - 40)
            {
                MessagesScroll.ScrollToEnd();
            }
        };
    }
}
