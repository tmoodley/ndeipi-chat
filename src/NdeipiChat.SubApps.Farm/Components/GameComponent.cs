using Microsoft.AspNetCore.Components;
using NdeipiChat.SubApps.Farm.Models;
using NdeipiChat.SubApps.Farm.Services;

namespace NdeipiChat.SubApps.Farm.Components;

/// <summary>
/// Base for every game component: takes the engine FarmApp cascades (a sub-app has no DI of its
/// own) and re-renders when the farm changes.
/// </summary>
public abstract class GameComponent : ComponentBase, IDisposable
{
    [CascadingParameter] protected FarmGameService Game { get; set; } = default!;

    protected FarmState S => Game.State;

    protected override void OnInitialized() => Game.Changed += OnGameChanged;

    private void OnGameChanged() => InvokeAsync(StateHasChanged);

    public virtual void Dispose() => Game.Changed -= OnGameChanged;

    protected static string Pct(double value, double max) =>
        (max <= 0 ? 0 : Math.Clamp(value / max * 100, 0, 100)).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
}
