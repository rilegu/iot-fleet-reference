using System.Windows;
using Fleet.Client.Core;
using Fleet.Client.Xaml;

namespace FleetWpf;

/// <summary>
/// The whole WPF host, and it is deliberately thin.
///
/// It constructs the shared store, connection and ViewModel, supplies a dispatcher, and
/// sets the DataContext. There is no fleet logic here at all — filtering, sorting,
/// selection and frame application live in the ViewModel that WinUI runs unchanged.
/// </summary>
public partial class MainWindow : Window
{
    private readonly FleetViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var store = new FleetStore();
        var options = new FleetClientOptions
        {
            BaseUrl = Environment.GetEnvironmentVariable("FLEET_API_URL") ?? "http://localhost:8080",
            MaxRateHz = 4,
        };
        var connection = new FleetConnection(store, options);

        _viewModel = new FleetViewModel(store, connection, new WpfDispatcher(Dispatcher));
        DataContext = _viewModel;

        // Connect after the window exists rather than in the constructor, so the first
        // frame has somewhere to render and a connection failure cannot prevent the window
        // from appearing at all.
        Loaded += async (_, _) => await _viewModel.ConnectCommand.ExecuteAsync(null);
        Closed += async (_, _) => await _viewModel.DisposeAsync();
    }
}
