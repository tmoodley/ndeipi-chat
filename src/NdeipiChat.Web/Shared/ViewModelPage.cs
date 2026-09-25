using System.ComponentModel;
using Microsoft.AspNetCore.Components;
using NdeipiChat.Web.Platform;

namespace NdeipiChat.Web.Shared;

/// <summary>
/// A page bound to a Client.Core view model. It re-renders when the view model raises
/// PropertyChanged, and when a hub event lands through <see cref="WebDispatcher"/> -- the
/// collections and item view models it updates don't reach Blazor on their own.
/// </summary>
public abstract class ViewModelPage<TViewModel> : ComponentBase, IDisposable where TViewModel : class, INotifyPropertyChanged
{
    [Inject] protected TViewModel Vm { get; set; } = null!;
    [Inject] WebDispatcher Dispatcher { get; set; } = null!;

    protected override void OnInitialized()
    {
        Vm.PropertyChanged += OnChanged;
        Dispatcher.Changed += Refresh;
    }

    void OnChanged(object? sender, PropertyChangedEventArgs e) => Refresh();

    void Refresh() => _ = InvokeAsync(StateHasChanged);

    public virtual void Dispose()
    {
        Vm.PropertyChanged -= OnChanged;
        Dispatcher.Changed -= Refresh;
    }
}
