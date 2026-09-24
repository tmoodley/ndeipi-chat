using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App;

public partial class App : Application
{
    readonly AppShell _shell;
    readonly AppCoordinator _coordinator;

    public App(AppShell shell, AppCoordinator coordinator)
    {
        InitializeComponent();
        _shell = shell;
        _coordinator = coordinator;

        // Livestock captures taken out of signal upload the moment the phone is back online.
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
                await _coordinator.SendWaitingCapturesAsync();
        };
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell);
        window.Created += async (_, _) => await _coordinator.StartAsync();
        return window;
    }
}
