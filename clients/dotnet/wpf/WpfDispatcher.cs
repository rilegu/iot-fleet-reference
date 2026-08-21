using System.Windows.Threading;
using Fleet.Client.Xaml;

namespace FleetWpf;

/// <summary>
/// The entire WPF-specific part of the ViewModel contract.
///
/// Everything else the ViewModels need is framework-neutral. This is the one thing that is
/// not, and it is three lines — which is the practical measure of how much a XAML host has
/// to supply in order to run ViewModels written without it in mind.
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>
    /// Background priority rather than Normal.
    ///
    /// Frames arrive four times a second and each one touches a great many bound
    /// properties. At Normal priority that work competes with input and rendering, so the
    /// window stops responding to the mouse while the data is perfectly healthy. Background
    /// yields to both, which is the correct trade for a display that is already coalesced.
    /// </summary>
    public void Post(Action action) =>
        _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
}
