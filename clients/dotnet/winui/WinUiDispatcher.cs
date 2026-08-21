using Fleet.Client.Xaml;
using Microsoft.UI.Dispatching;

namespace FleetWinUI;

/// <summary>
/// The entire WinUI-specific part of the ViewModel contract, and the counterpart to the WPF
/// implementation.
///
/// Comparing the two is the point: both are a handful of lines wrapping a framework type
/// that the other framework cannot reference. Everything else the ViewModels need is
/// shared, which is what makes the reuse real rather than nominal.
/// </summary>
public sealed class WinUiDispatcher : IUiDispatcher
{
    private readonly DispatcherQueue _queue;

    public WinUiDispatcher(DispatcherQueue queue) => _queue = queue;

    /// <summary>
    /// Low priority, matching the WPF host's use of Background: frame application must yield
    /// to input and rendering, or the window stops responding while the data is healthy.
    ///
    /// TryEnqueue returns false when the queue is shutting down. That is not an error worth
    /// surfacing — the window is closing and the frame has nowhere to go.
    /// </summary>
    public void Post(Action action) => _queue.TryEnqueue(DispatcherQueuePriority.Low, () => action());
}
