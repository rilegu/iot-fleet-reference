using System.Collections.Specialized;
using Fleet.Client.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace FleetWinUI;

/// <summary>
/// The WinUI detail panel, and the counterpart to the WPF one.
///
/// Both do the same single job the shared ViewModel deliberately does not: project
/// normalised sparkline vertices into a framework-specific polyline. Reading the two files
/// side by side is the clearest illustration of where the shared layer stops — the
/// arithmetic is written once, and only the projection into pixels is duplicated.
/// </summary>
public sealed partial class DetailPanel : UserControl
{
    private FleetViewModel? _viewModel;
    private DeviceDetailViewModel? _detail;

    public DetailPanel()
    {
        InitializeComponent();
        SizeChanged += (_, _) => DrawSpark();
    }

    /// <summary>
    /// The ViewModel the compiled bindings read from, assigned by the hosting window.
    /// </summary>
    /// <remarks>
    /// A plain property rather than a dependency property, with an explicit
    /// <c>Bindings.Update()</c>: x:Bind resolves against this control's own members and is
    /// generated once, so it needs telling that the root of every path has been replaced.
    /// </remarks>
    public FleetViewModel? ViewModel
    {
        get => _viewModel;
        set
        {
            _viewModel = value;
            Bindings.Update();
            Rebind();
        }
    }

    private void Rebind()
    {
        if (_detail is not null)
            _detail.Spark.CollectionChanged -= OnSparkChanged;

        _detail = _viewModel?.Detail;

        if (_detail is not null)
            _detail.Spark.CollectionChanged += OnSparkChanged;

        DrawSpark();
    }

    private void OnSparkChanged(object? sender, NotifyCollectionChangedEventArgs e) => DrawSpark();

    /// <summary>
    /// Scales the 0-100 vertices onto the canvas.
    ///
    /// Redrawn on resize as well as on data change: a polyline built for the old width would
    /// otherwise stay that width inside a wider control.
    /// </summary>
    private void DrawSpark()
    {
        SparkLine.Points.Clear();

        if (_detail is null || _detail.Spark.Count < 2) return;

        var width = SparkCanvas.ActualWidth;
        var height = SparkCanvas.ActualHeight;
        if (width <= 0 || height <= 0) return;

        // Inset by the stroke width so the line is not clipped at the extremes.
        const double inset = 2;
        var usableWidth = Math.Max(width - inset * 2, 1);
        var usableHeight = Math.Max(height - inset * 2, 1);

        foreach (var point in _detail.Spark)
        {
            SparkLine.Points.Add(new Point(
                inset + point.X / 100.0 * usableWidth,
                inset + point.Y / 100.0 * usableHeight));
        }
    }
}
