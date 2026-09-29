namespace NdeipiChat.Desktop;

public sealed class App(ShellPage page) : Application
{
    protected override Window CreateWindow(IActivationState? activationState) => new(page)
    {
        Title = "Ndeipi",
        Width = 1280,
        Height = 840,
        MinimumWidth = 420,
        MinimumHeight = 560
    };
}
