using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>The Herd tab: registered animals, and captures still waiting on the phone.</summary>
public sealed partial class HerdViewModel : ObservableObject
{
    readonly LivestockApi _api;
    readonly ILivestockCaptureQueue _queue;
    readonly LivestockSync _sync;
    readonly INavigator _navigator;
    readonly IUiDispatcher _ui;

    public HerdViewModel(LivestockApi api, ILivestockCaptureQueue queue, LivestockSync sync, INavigator navigator, IUiDispatcher ui)
    {
        (_api, _queue, _sync, _navigator, _ui) = (api, queue, sync, navigator, ui);
        _sync.CaptureUploaded += outcome => _ui.Post(() => _ = ReloadAsync(includeCows: outcome.Accepted));
    }

    public ObservableCollection<CowItemViewModel> Cows { get; } = [];
    public ObservableCollection<PendingCaptureViewModel> Pending { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasPending => Pending.Count > 0;
    public bool IsEmpty => Cows.Count == 0 && Pending.Count == 0;
    public int VetCount => Cows.Count(c => c.RequiresVet);

    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            await ReloadAsync(includeCows: true);
        }
        finally
        {
            IsRefreshing = false;
        }
        await SyncAsync();
    }

    /// <summary>Sends anything waiting on the phone.</summary>
    [RelayCommand]
    async Task SyncAsync()
    {
        if (IsSyncing)
            return;
        IsSyncing = true;
        try
        {
            await _sync.UploadPendingAsync();
        }
        finally
        {
            IsSyncing = false;
        }
        await ReloadAsync(includeCows: false);
    }

    [RelayCommand]
    Task RegisterAsync() => _navigator.GoToAsync(Routes.RegisterCow);

    [RelayCommand]
    Task OpenAsync(CowItemViewModel cow) =>
        _navigator.GoToAsync(Routes.Cow, new Dictionary<string, object> { [Routes.CowIdParameter] = cow.CowId });

    [RelayCommand]
    async Task DiscardAsync(PendingCaptureViewModel capture)
    {
        await _queue.RemoveAsync(capture.Id);
        Pending.Remove(capture);
        Changed();
    }

    async Task ReloadAsync(bool includeCows)
    {
        var pending = await _queue.ListAsync();
        Pending.Clear();
        foreach (var capture in pending.OrderByDescending(c => c.CreatedAt))
            Pending.Add(new PendingCaptureViewModel(capture));

        if (includeCows)
        {
            try
            {
                var cows = await _api.GetCowsAsync();
                ErrorMessage = null;
                Cows.Clear();
                foreach (var cow in cows)
                    Cows.Add(new CowItemViewModel(cow));
                _ = LoadThumbnailsAsync(Cows.ToList());
            }
            catch (ApiException ex)
            {
                ErrorMessage = ex.Message;
            }
        }
        Changed();
    }

    async Task LoadThumbnailsAsync(IReadOnlyList<CowItemViewModel> cows)
    {
        foreach (var cow in cows)
        {
            try
            {
                var thumbnail = await _api.GetImageAsync(cow.FaceImageRef, width: 200);
                _ui.Post(() => cow.Thumbnail = thumbnail);
            }
            catch (ApiException)
            {
                // The list still works without pictures.
            }
        }
    }

    void Changed()
    {
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(VetCount));
    }
}

public sealed partial class CowItemViewModel(CowSummaryDto cow) : ObservableObject
{
    public string CowId => cow.CowId;
    public string ShortId => CowDisplay.ShortId(cow.CowId);
    public string FaceImageRef => cow.FaceImageRef;
    public string Title => $"{cow.Breed} · {cow.Sex}";
    public string Subtitle => $"Body condition {cow.LatestBodyConditionScore:0.0} · {CowDisplay.Rating(cow.LatestHealthRating)}";
    public bool RequiresVet => cow.RequiresVetInspection;

    [ObservableProperty]
    public partial byte[]? Thumbnail { get; set; }
}

public sealed class PendingCaptureViewModel(PendingCapture capture)
{
    public Guid Id => capture.Id;
    public string Label => capture.Label;
    public string FacePath => capture.FacePath;
    public bool IsRejected => capture.Status == CaptureStatuses.Rejected;
    public string StatusText => IsRejected ? $"Not accepted: {capture.LastError}" : capture.LastError ?? "Waiting to upload";
    public string TimeText => capture.CreatedAt.ToLocalTime().ToString("d MMM, HH:mm", CultureInfo.CurrentCulture);
}

public enum RegistrationStep
{
    Face,
    Flank,
    Details,
    Submitting,
    Done
}

/// <summary>
/// Registering a cow, or a health check of one: face photo, side photo, a few details, then the
/// capture is queued on the phone and uploaded -- straight away if there's signal, later if not.
/// </summary>
public sealed partial class RegisterCowViewModel : ObservableObject, INavigationAware
{
    public const string RanchCodeSetting = "livestock.ranch-code";
    public const int MinFaceShortSide = 1080;
    public const int MinFlankShortSide = 720;
    public const string NotSure = "Not sure";

    readonly ILivestockCaptureQueue _queue;
    readonly LivestockSync _sync;
    readonly ChatSession _session;
    readonly ILocationProvider _location;
    readonly ISettingsStore _settings;
    readonly INavigator _navigator;
    readonly TimeProvider _clock;

    public RegisterCowViewModel(
        ILivestockCaptureQueue queue,
        LivestockSync sync,
        ChatSession session,
        ILocationProvider location,
        ISettingsStore settings,
        INavigator navigator,
        TimeProvider clock)
    {
        (_queue, _sync, _session, _location, _settings, _navigator, _clock) = (queue, sync, session, location, settings, navigator, clock);
        RanchCode = settings.Get(RanchCodeSetting) ?? "";
        ClaimedBreed = NotSure;
        Sex = CattleSex.Unknown;
        AgeMonths = "";
        OperatorWallet = "";
        Step = RegistrationStep.Face;
    }

    public IReadOnlyList<string> Breeds { get; } = [NotSure, .. CattleBreeds.All];
    public IReadOnlyList<string> Sexes => CattleSex.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCapturing), nameof(IsDetailsStep), nameof(IsSubmitting), nameof(IsDone), nameof(Instruction), nameof(StepText))]
    public partial RegistrationStep Step { get; set; }

    [ObservableProperty]
    public partial byte[]? FacePhoto { get; set; }

    [ObservableProperty]
    public partial byte[]? FlankPhoto { get; set; }

    [ObservableProperty]
    public partial string RanchCode { get; set; }

    [ObservableProperty]
    public partial string ClaimedBreed { get; set; }

    [ObservableProperty]
    public partial string Sex { get; set; }

    [ObservableProperty]
    public partial string AgeMonths { get; set; }

    [ObservableProperty]
    public partial string OperatorWallet { get; set; }

    [ObservableProperty]
    public partial string? PhotoWarning { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial RegistrationSummary? Result { get; set; }

    /// <summary>Set for a health check of an animal already on the register.</summary>
    public string? ExistingCowId { get; private set; }

    public bool IsHealthCheck => ExistingCowId is not null;
    public string Title => IsHealthCheck ? "Health check" : "Register a cow";
    public bool IsCapturing => Step is RegistrationStep.Face or RegistrationStep.Flank;
    public bool IsDetailsStep => Step == RegistrationStep.Details;
    public bool IsSubmitting => Step == RegistrationStep.Submitting;
    public bool IsDone => Step == RegistrationStep.Done;

    public string StepText => Step switch
    {
        RegistrationStep.Face => "1 of 3 · Face",
        RegistrationStep.Flank => "2 of 3 · Side",
        RegistrationStep.Details => "3 of 3 · Details",
        _ => ""
    };

    public string Instruction => Step switch
    {
        RegistrationStep.Face => "Face the cow head-on. Fill the frame with the muzzle, from nose to forehead, and hold still.",
        RegistrationStep.Flank => "Stand side-on, about 4 metres back, so the whole animal from shoulder to tail fits in the frame.",
        _ => ""
    };

    public Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters)
    {
        ExistingCowId = parameters.TryGetValue(Routes.CowIdParameter, out var id) ? id?.ToString() : null;
        OperatorWallet = _session.Me?.Wallets.Select(w => w.Address).FirstOrDefault(IsEvmAddress) ?? "";
        OnPropertyChanged(nameof(IsHealthCheck));
        OnPropertyChanged(nameof(Title));
        return Task.CompletedTask;
    }

    /// <summary>The photo the camera just took (or one picked from the gallery).</summary>
    public void AcceptPhoto(byte[] photo)
    {
        if (ImageDimensions.Read(photo) is { } size)
            AcceptPhoto(photo, size.Width, size.Height);
        else
            PhotoWarning = "That photo isn't a JPEG or PNG. Take it with the camera instead.";
    }

    /// <summary>
    /// A photo with its size in pixels. A photo too small for the registry is refused here, while
    /// the cow is still in front of the farmer.
    /// </summary>
    public void AcceptPhoto(byte[] photo, int width, int height)
    {
        if (!IsCapturing)
            return;
        var minimum = Step == RegistrationStep.Face ? MinFaceShortSide : MinFlankShortSide;
        if (Math.Min(width, height) < minimum)
        {
            PhotoWarning = $"That photo is only {width}×{height}. Use the main camera at full resolution; it needs at least {minimum} pixels on the shorter side.";
            return;
        }

        PhotoWarning = null;
        if (Step == RegistrationStep.Face)
        {
            FacePhoto = photo;
            Step = FlankPhoto is null ? RegistrationStep.Flank : RegistrationStep.Details;
        }
        else
        {
            FlankPhoto = photo;
            Step = RegistrationStep.Details;
        }
    }

    [RelayCommand]
    void RetakeFace()
    {
        FacePhoto = null;
        Step = RegistrationStep.Face;
    }

    [RelayCommand]
    void RetakeFlank()
    {
        FlankPhoto = null;
        Step = RegistrationStep.Flank;
    }

    [RelayCommand]
    async Task SubmitAsync()
    {
        ErrorMessage = null;
        if (FacePhoto is null || FlankPhoto is null)
        {
            ErrorMessage = "Take both photos first.";
            return;
        }

        var ranch = RanchCode.Trim().ToLowerInvariant();
        if (!RanchCodePattern().IsMatch(ranch))
        {
            ErrorMessage = "Enter your ranch code, starting with the country: e.g. zm-st-041.";
            return;
        }

        int? age = null;
        if (!string.IsNullOrWhiteSpace(AgeMonths))
        {
            if (!int.TryParse(AgeMonths.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var months) || months > 360)
            {
                ErrorMessage = "Enter the age in months, e.g. 28.";
                return;
            }
            age = months;
        }

        var wallet = OperatorWallet.Trim();
        if (wallet.Length > 0 && !IsEvmAddress(wallet))
        {
            ErrorMessage = "The wallet must be a 0x address.";
            return;
        }

        Step = RegistrationStep.Submitting;
        var gps = await _location.GetLocationAsync(CancellationToken.None);
        if (gps is null)
        {
            ErrorMessage = "Turn on location: the registry records where each animal was registered.";
            Step = RegistrationStep.Details;
            return;
        }
        _settings.Set(RanchCodeSetting, ranch);

        var metadata = new LivestockRegistrationMetadata(
            LivestockContract.RanchUrn(ranch),
            wallet.Length > 0 ? wallet : null,
            _clock.GetUtcNow().ToUnixTimeSeconds(),
            gps,
            new ManualOverrides(ClaimedBreed == NotSure ? null : ClaimedBreed, Sex, age),
            ExistingCowId);

        // Saved first, sent second: nothing is lost if the upload can't happen now.
        var capture = await _queue.EnqueueAsync(FacePhoto, FlankPhoto, ContractJson.Write(metadata), Label());
        Result = RegistrationSummary.From(await _sync.UploadAsync(capture.Id));
        Step = RegistrationStep.Done;
    }

    [RelayCommand]
    Task FinishAsync() => _navigator.GoBackAsync();

    string Label() => IsHealthCheck
        ? $"Health check · {CowDisplay.ShortId(ExistingCowId!)}"
        : ClaimedBreed == NotSure ? "New animal" : $"New {ClaimedBreed}";

    [GeneratedRegex("^[a-z]{2}-[a-z0-9][a-z0-9-]{0,40}$")]
    private static partial Regex RanchCodePattern();

    static bool IsEvmAddress(string value) =>
        value.Length == 42 && value.StartsWith("0x", StringComparison.Ordinal) && value[2..].All(char.IsAsciiHexDigit);
}

/// <summary>An animal's record: identity, breed, and every health screening so far.</summary>
public sealed partial class CowDetailViewModel(LivestockApi api, INavigator navigator) : ObservableObject, INavigationAware
{
    string? _cowId;

    public ObservableCollection<AuditItemViewModel> Audits { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(BreedText), nameof(SexAgeText), nameof(RanchText), nameof(RegisteredText),
        nameof(TraitsText), nameof(IdentityText), nameof(LocationText))]
    public partial CowDetailDto? Cow { get; set; }

    [ObservableProperty]
    public partial byte[]? FacePhoto { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public string Title => Cow is null ? "" : CowDisplay.ShortId(Cow.CowId);

    public string BreedText => Cow switch
    {
        null => "",
        { BreedSource: "MODEL" } c => $"{c.Breed} · {c.BreedConfidence:P0} confident",
        { BreedSource: "CLAIMED" } c => $"{c.Breed} · as stated at registration",
        var c => $"{c.Breed} · unconfirmed"
    };

    public string SexAgeText => Cow is null ? "" : Cow.ApproximateAgeMonths is { } months ? $"{Cow.Sex} · about {months} months at registration" : Cow.Sex;
    public string RanchText => Cow?.RanchId.Replace("urn:ndeipi:ranch:", "", StringComparison.Ordinal).ToUpperInvariant() ?? "";
    public string RegisteredText => Cow is null ? "" : $"Registered {Cow.RegisteredAt.ToLocalTime():d MMM yyyy}";
    public string LocationText => Cow is null ? "" : string.Create(CultureInfo.InvariantCulture, $"{Cow.Latitude:0.00000}, {Cow.Longitude:0.00000}");
    public string IdentityText => Cow is null ? "" : Cow.BiometricallyEnrolled ? "Muzzle print enrolled" : "Registered without a muzzle print";

    public string TraitsText => Cow?.MorphologicalTraits is not { } t ? "" : string.Join(" · ", new[]
    {
        t.HasThoracicHump ? "Humped" : "No hump",
        $"dewlap {t.DewlapProminence.ToLowerInvariant()}",
        t.HornState.ToLowerInvariant(),
        t.CoatPattern
    }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public async Task OnNavigatedToAsync(IReadOnlyDictionary<string, object> parameters)
    {
        _cowId = parameters[Routes.CowIdParameter].ToString();
        await RefreshAsync();
    }

    [RelayCommand]
    async Task RefreshAsync()
    {
        if (_cowId is null)
            return;
        try
        {
            Cow = await api.GetCowAsync(_cowId);
            Audits.Clear();
            foreach (var audit in Cow.Audits)
                Audits.Add(new AuditItemViewModel(audit));
            ErrorMessage = null;
            FacePhoto ??= await api.GetImageAsync(Cow.FaceImageRef, width: 900);
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    Task HealthCheckAsync() => Cow is null
        ? Task.CompletedTask
        : navigator.GoToAsync(Routes.RegisterCow, new Dictionary<string, object> { [Routes.CowIdParameter] = Cow.CowId });
}

public sealed class AuditItemViewModel(HealthAuditDto audit)
{
    public string DateText => audit.TimestampUtc.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);
    public string ConditionText => $"Body condition {audit.BodyConditionScore:0.0} / 9";
    public string HealthText => $"{CowDisplay.Rating(audit.HealthRating)} · {CowDisplay.Hydration(audit.HydrationStatus)}";
    public string FindingsText => audit.DetectedAnomalies.Count == 0
        ? "No visible problems"
        : string.Join("\n", audit.DetectedAnomalies.Select(CowDisplay.Finding));
    public bool RequiresVet => audit.RequiresVetInspection;
    public string AttestationText => $"Attestation {Display.ShortAddress(audit.AttestationHash)}";
}

/// <summary>What happened to a registration, worded for the farmer.</summary>
public sealed record RegistrationSummary(
    string Headline,
    string Detail,
    bool Succeeded,
    bool IsQueued,
    string? CowId,
    string? BreedText,
    string? ConditionText,
    string? HealthText,
    IReadOnlyList<string> Findings,
    bool RequiresVet,
    string? AttestationText)
{
    public bool HasFindings => Findings.Count > 0;

    public static RegistrationSummary From(UploadOutcome outcome)
    {
        if (outcome.Accepted && outcome.Response is { } response)
            return FromResponse(response);
        if (outcome.Capture.Status == CaptureStatuses.Pending)
            return new("Saved on your phone", outcome.Capture.LastError ?? LivestockSync.OfflineMessage, false, true, null, null, null, null, [], false, null);
        return new("Couldn't register", "Take new photos and try again.", false, false, null, null, null, null,
            outcome.Response?.Problems ?? [outcome.Capture.LastError ?? "The server refused the photos."], false, null);
    }

    public static RegistrationSummary FromResponse(LivestockRegistrationResponse response)
    {
        var existing = response.Biometrics?.EnrollmentStatus == EnrollmentStatuses.ExistingAnimal;
        var unverified = response.Biometrics?.EnrollmentStatus == EnrollmentStatuses.UnverifiedIdentity;
        var detail = CowDisplay.ShortId(response.CowId ?? "") + (unverified ? " · muzzle print not checked" : "");

        string? breed = response.Phenotype switch
        {
            null => null,
            { BreedConfirmed: true } p => $"{p.RecordedBreed} ({p.BreedConfidence:P0} confident)",
            var p when p.RecordedBreed != p.DetectedBreed => $"{p.RecordedBreed}, as you said (photos suggest {p.DetectedBreed}, {p.BreedConfidence:P0})",
            var p => $"{p.DetectedBreed}, unconfirmed ({p.BreedConfidence:P0})"
        };

        var health = response.HealthScreening;
        return new(
            existing ? "Health check recorded" : "Registered",
            detail,
            true,
            false,
            response.CowId,
            breed,
            health is null ? null : $"Body condition {health.BodyConditionScore:0.0} / 9",
            health is null ? null : $"{CowDisplay.Rating(health.OverallHealthRating)} · {CowDisplay.Hydration(health.HydrationStatus)}",
            health?.DetectedAnomalies.Select(CowDisplay.Finding).ToList() ?? [],
            health?.RequiresVetInspection ?? false,
            response.AttestationHash is { } hash ? $"Attestation {Display.ShortAddress(hash)}" : null);
    }
}

public static class CowDisplay
{
    /// <summary>"urn:ndeipi:asset:cattle:zm-49204891" is shown as "ZM-49204891".</summary>
    public static string ShortId(string cowId) => cowId[(cowId.LastIndexOf(':') + 1)..].ToUpperInvariant();

    public static string Rating(string rating) => rating switch
    {
        HealthRatings.Prime => "Prime",
        HealthRatings.Good => "Good",
        HealthRatings.Fair => "Fair",
        HealthRatings.Poor => "Poor",
        HealthRatings.Critical => "Critical",
        _ => rating
    };

    public static string Hydration(string status) => status switch
    {
        HydrationStatuses.Normal => "well hydrated",
        HydrationStatuses.Mild => "mildly dehydrated",
        HydrationStatuses.Severe => "severely dehydrated",
        _ => status
    };

    public static string Finding(DetectedAnomaly anomaly) =>
        $"{Anomaly(anomaly.Type)}, {anomaly.Location} ({anomaly.Confidence:P0})";

    static string Anomaly(string type) => type switch
    {
        AnomalyTypes.Ticks => "Ticks",
        AnomalyTypes.LumpySkin => "Lumpy skin nodules",
        AnomalyTypes.Ringworm => "Ringworm",
        AnomalyTypes.Wound => "Wound",
        AnomalyTypes.OcularDischarge => "Eye discharge",
        AnomalyTypes.NasalDischarge => "Nasal discharge",
        AnomalyTypes.CornealOpacity => "Cloudy eye",
        AnomalyTypes.SunkenEyes => "Sunken eyes",
        AnomalyTypes.Lameness => "Possible lameness",
        _ => "Other finding"
    };
}
