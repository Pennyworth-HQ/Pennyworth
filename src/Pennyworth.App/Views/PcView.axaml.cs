using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Pennyworth.App.ViewModels;

namespace Pennyworth.App.Views;

public sealed partial class PcView : UserControl
{
    // Fills the drive drawing over 1.6 s after the drive part is selected.
    private readonly DispatcherTimer _animation = new() { Interval = TimeSpan.FromMilliseconds(40) };
    private readonly Stopwatch _sinceSelected = Stopwatch.StartNew();
    private PcViewModel? _viewModel;

    public PcView()
    {
        InitializeComponent();
        _animation.Tick += (_, _) =>
        {
            _viewModel?.AdvanceAnimation(_sinceSelected.Elapsed);
            if (_sinceSelected.Elapsed.TotalSeconds > 1.7)
            {
                _animation.Stop();
            }
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }

        _viewModel = DataContext as PcViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
        }

        Restart();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _animation.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PcViewModel.Part))
        {
            Restart();
        }
    }

    private void Restart()
    {
        _sinceSelected.Restart();
        _animation.Start();
    }
}
