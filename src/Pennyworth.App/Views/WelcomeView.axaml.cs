using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class WelcomeView : UserControl
{
    // The boot lines appear one by one, like the design's typing screen.
    private readonly DispatcherTimer _boot = new() { Interval = TimeSpan.FromMilliseconds(300) };

    public WelcomeView()
    {
        InitializeComponent();
        _boot.Tick += (_, _) => (DataContext as WelcomeViewModel)?.AdvanceBoot();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _boot.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _boot.Stop();
        base.OnDetachedFromVisualTree(e);
    }
}
