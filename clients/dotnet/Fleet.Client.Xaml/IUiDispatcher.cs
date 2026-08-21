namespace Fleet.Client.Xaml;

/// <summary>
/// Marshals work onto the UI thread.
///
/// Frames arrive on the connection's background task, and XAML permits touching bound
/// properties only from the thread that owns the objects. Every XAML framework solves this,
/// and each solves it with a different type — WPF has <c>Dispatcher</c>, WinUI has
/// <c>DispatcherQueue</c>, and neither reference assembly is available to the other.
///
/// So the ViewModels take this interface instead. That is what lets one set of ViewModels
/// serve both hosts: the only genuinely framework-specific thing they need is supplied by
/// the host in three lines, rather than compiled into the ViewModel and duplicated.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues an action on the UI thread. Returns immediately; never blocks the caller.</summary>
    void Post(Action action);
}

/// <summary>
/// Runs everything inline, for tests.
///
/// Without this the ViewModels would need a real UI thread to be testable, which is exactly
/// the coupling MVVM exists to avoid.
/// </summary>
public sealed class ImmediateDispatcher : IUiDispatcher
{
    public void Post(Action action) => action();
}
