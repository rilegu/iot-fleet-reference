using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Fleet.Client.Xaml;

namespace FleetWpf;

/// <summary>
/// Code-behind for the detail panel, and it does exactly one thing the ViewModel cannot:
/// turn normalised sparkline vertices into a WPF <see cref="System.Windows.Shapes.Polyline"/>.
///
/// The shared ViewModel deliberately stops at normalised points. Producing a PointCollection
/// there would drag a WPF reference into an assembly WinUI also consumes, and the two
/// frameworks have incompatible geometry types — so the arithmetic is shared and only the
/// projection into pixels is duplicated, at about a dozen lines per host.
/// </summary>
public partial class DetailPanel : UserControl
{
    private DeviceDetailViewModel? _detail;

    public DetailPanel()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Rebind();
        SizeChanged += (_, _) => DrawSpark();
    }

    private void Rebind()
    {
        if (_detail is not null)
            _detail.Spark.CollectionChanged -= OnSparkChanged;

        _detail = (DataContext as FleetViewModel)?.Detail;

        if (_detail is not null)
            _detail.Spark.CollectionChanged += OnSparkChanged;

        DrawSpark();
    }

    private void OnSparkChanged(object? sender, NotifyCollectionChangedEventArgs e) => DrawSpark();

    /// <summary>
    /// Scales the 0-100 vertices onto the canvas.
    ///
    /// Redrawn on resize as well as on data change, because a polyline built for the old
    /// width would otherwise stay at that width inside a wider control.
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
