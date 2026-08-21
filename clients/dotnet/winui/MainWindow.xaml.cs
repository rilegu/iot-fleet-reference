using Fleet.Client.Core;
using Fleet.Client.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace FleetWinUI;

/// <summary>
/// The whole WinUI host.
///
/// Structurally identical to the WPF one: build the shared store, connection and ViewModel,
/// supply a dispatcher, bind. No fleet logic lives here either — that is the claim this
/// client exists to test, and a host that needed its own filtering or frame handling would
/// disprove it.
/// </summary>
public sealed partial class MainWindow : Window
{
    public FleetViewModel ViewModel { get; }

    public MainWindow()
    {
        InitializeComponent();
        Title = "Fleet";

        var store = new FleetStore();
        var options = new FleetClientOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("FLEET_API_URL") ?? "http://localhost:8080",
            MaxRateHz = 4,
        };
        var connection = new FleetConnection(store, options);

        ViewModel = new FleetViewModel(store, connection, new WinUiDispatcher(DispatcherQueue));

        // The panel reads through a plain property, so it is assigned rather than bound.
        DetailPane.ViewModel = ViewModel;

        Activated += OnFirstActivation;
        Closed += async (_, _) => await ViewModel.DisposeAsync();
    }

    /// <summary>
    /// Selects the tapped row.
    ///
    /// The one piece of behaviour WPF gets for free and this host has to write: ItemsRepeater
    /// carries no selection model, so the tapped row has to be identified and pushed to the
    /// ViewModel, which owns the selection from there on.
    ///
    /// The row is found by index rather than by reading its DataContext. A repeater whose
    /// template is compiled with x:Bind feeds the item to the generated bindings directly and
    /// never sets DataContext on the realised element, so reading it back yields null and the
    /// tap is silently dropped.
    /// </summary>
    private void OnRowTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement row) return;

        var index = DeviceGrid.GetElementIndex(row);
        if (index >= 0 && index < ViewModel.Devices.Count)
            ViewModel.Selected = ViewModel.Devices[index];
    }

    // Connect once the window is live rather than during construction, so a connection
    // failure cannot stop the window appearing.
    private async void OnFirstActivation(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivation;
        await ViewModel.ConnectCommand.ExecuteAsync(null);
    }
}
