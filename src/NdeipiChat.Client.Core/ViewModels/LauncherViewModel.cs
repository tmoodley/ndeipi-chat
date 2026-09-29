using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// The host shell's launcher (SRS SR-01): the sub-apps this user's manifest allows, pinned ones
/// first, and the ones open in this session. The last manifest is kept on the device, so the
/// launcher works offline (NFR-03-01).
/// </summary>
public sealed partial class LauncherViewModel : ObservableObject
{
    const string CacheKeyPrefix = "launcher.manifest.";

    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly INavigator _navigator;
    readonly IDialogs _dialogs;
    readonly ISettingsStore _settings;
    Guid _loadedFor;

    public LauncherViewModel(ChatApi api, ChatSession session, INavigator navigator, IDialogs dialogs, ISettingsStore settings)
    {
        (_api, _session, _navigator, _dialogs, _settings) = (api, session, navigator, dialogs, settings);
    }

    public ObservableCollection<SubAppItemViewModel> Apps { get; } = [];
    public ObservableCollection<SubAppItemViewModel> Pinned { get; } = [];

    /// <summary>Sub-apps launched in this session, most recent first, until closed.</summary>
    public ObservableCollection<SubAppItemViewModel> Running { get; } = [];

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    /// <summary>Showing the manifest saved on this device because the server couldn't be reached.</summary>
    [ObservableProperty]
    public partial bool IsOffline { get; set; }

    public string? ManifestVersion { get; private set; }
    public bool HasRunning => Running.Count > 0;

    /// <summary>Raised whenever the set of apps or pins changes, so a shell can rebuild its navigation.</summary>
    public event Action? ManifestChanged;

    /// <summary>Whether this user may open the sub-app; false before the manifest has loaded.</summary>
    public bool Allows(string appId) => Apps.Any(a => a.Id == appId);

    [RelayCommand]
    async Task LoadAsync()
    {
        try
        {
            var manifest = await _api.GetLauncherAsync();
            _settings.Set(CacheKey, JsonSerializer.Serialize(manifest, ContractJson.Options));
            Apply(manifest, offline: false);
        }
        catch (ApiException ex)
        {
            // Offline, or the server's having trouble: the apps from last time still open.
            if (Cached() is { } cached)
                Apply(cached, offline: true);
            else
                await _dialogs.AlertAsync("Couldn't load your apps", ex.Message);
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    async Task LaunchAsync(SubAppItemViewModel app)
    {
        var running = Running.FirstOrDefault(r => r.Id == app.Id);
        if (running is not null)
            Running.Remove(running);
        Running.Insert(0, app);
        app.IsRunning = true;
        OnPropertyChanged(nameof(HasRunning));
        await _navigator.GoToAsync(Routes.SubApp(app.Id), new Dictionary<string, object> { [Routes.SubAppRouteParameter] = app.Route });
    }

    /// <summary>Closes a sub-app without restarting the shell (SR-01-03).</summary>
    [RelayCommand]
    void Close(SubAppItemViewModel app)
    {
        var running = Running.FirstOrDefault(r => r.Id == app.Id);
        if (running is null)
            return;
        Running.Remove(running);
        app.IsRunning = false;
        OnPropertyChanged(nameof(HasRunning));
    }

    /// <summary>Pins or unpins, straight away on screen; put back if the server refuses.</summary>
    [RelayCommand]
    async Task TogglePinAsync(SubAppItemViewModel app)
    {
        var pins = Pinned.Select(p => p.Id).ToList();
        if (!pins.Remove(app.Id))
            pins.Add(app.Id);
        var before = Pinned.Select(p => p.Id).ToList();
        SetPins(pins);
        try
        {
            var manifest = await _api.SetPinsAsync(pins);
            _settings.Set(CacheKey, JsonSerializer.Serialize(manifest, ContractJson.Options));
            Apply(manifest, offline: false);
        }
        catch (ApiException ex)
        {
            SetPins(before);
            await _dialogs.AlertAsync("Couldn't update your pinned apps", ex.Message);
        }
    }

    void Apply(LauncherManifestDto manifest, bool offline)
    {
        // Someone else signed in on this device: nothing of the last user's carries over.
        if (_loadedFor != _session.MyUserId)
        {
            Apps.Clear();
            Running.Clear();
            Pinned.Clear();
            _loadedFor = _session.MyUserId;
        }

        var existing = Apps.ToDictionary(a => a.Id);
        Apps.Clear();
        foreach (var app in manifest.Apps)
        {
            var item = existing.TryGetValue(app.Id, out var known) ? known.With(app) : new SubAppItemViewModel(app);
            Apps.Add(item);
        }
        // Apps no longer allowed close.
        foreach (var gone in Running.Where(r => Apps.All(a => a.Id != r.Id)).ToList())
            Running.Remove(gone);

        SetPins(manifest.Apps.Where(a => a.Pinned).Select(a => a.Id).ToList());
        (ManifestVersion, IsOffline, IsLoaded) = (manifest.Version, offline, true);
        OnPropertyChanged(nameof(HasRunning));
    }

    void SetPins(IReadOnlyList<string> pins)
    {
        Pinned.Clear();
        foreach (var id in pins)
            if (Apps.FirstOrDefault(a => a.Id == id) is { } app)
                Pinned.Add(app);
        foreach (var app in Apps)
            app.IsPinned = pins.Contains(app.Id);
        ManifestChanged?.Invoke();
    }

    LauncherManifestDto? Cached()
    {
        try
        {
            return _settings.Get(CacheKey) is { } json ? JsonSerializer.Deserialize<LauncherManifestDto>(json, ContractJson.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Per user: a shared device mustn't show one person's apps to the next.</summary>
    string CacheKey => CacheKeyPrefix + _session.MyUserId.ToString("N");
}

public sealed partial class SubAppItemViewModel(SubAppDto app) : ObservableObject
{
    public SubAppDto App { get; private set; } = app;
    public string Id => App.Id;
    public string Title => App.Title;
    public string Description => App.Description;
    public string Icon => App.Icon;
    public string Route => App.Route;

    /// <summary>Its icon square's colour, the same on the web and the phone.</summary>
    public string Tone => AppTones.For(Id);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinText))]
    public partial bool IsPinned { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public string PinText => IsPinned ? "Unpin" : "Pin";

    /// <summary>The same item with the manifest's latest details, so open apps keep their identity.</summary>
    public SubAppItemViewModel With(SubAppDto latest)
    {
        App = latest;
        OnPropertyChanged(string.Empty);
        return this;
    }
}
