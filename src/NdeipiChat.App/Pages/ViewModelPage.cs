using NdeipiChat.Client;

namespace NdeipiChat.App.Pages;

/// <summary>
/// Connects a page to its view model's lifecycle: navigation parameters on first appearance,
/// Activate/Deactivate as it comes and goes, and Dispose once it's popped off the stack.
/// </summary>
public abstract class ViewModelPage : ContentPage, IQueryAttributable
{
    IReadOnlyDictionary<string, object> _parameters = new Dictionary<string, object>();
    bool _navigated;

    protected ViewModelPage(object viewModel) => BindingContext = viewModel;

    public void ApplyQueryAttributes(IDictionary<string, object> query)
    {
        _parameters = new Dictionary<string, object>(query);
        _navigated = false;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            if (!_navigated && BindingContext is INavigationAware aware)
            {
                _navigated = true;
                await aware.OnNavigatedToAsync(_parameters);
            }
            (BindingContext as IActivatable)?.Activate();
            await OnAppearedAsync();
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Something went wrong", ex.Message, "OK");
        }
    }

    /// <summary>Per-page work each time the page shows, e.g. refreshing a list.</summary>
    protected virtual Task OnAppearedAsync() => Task.CompletedTask;

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        (BindingContext as IActivatable)?.Deactivate();
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        base.OnNavigatedFrom(args);
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove && BindingContext is IDisposable disposable)
            disposable.Dispose();
    }
}
