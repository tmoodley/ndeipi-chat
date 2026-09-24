using System.ComponentModel;
using CommunityToolkit.Maui.Core;
using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App.Pages;

public partial class HerdPage : ViewModelPage
{
    readonly HerdViewModel _viewModel;

    public HerdPage(HerdViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.RefreshCommand.ExecuteAsync(null);
}

public partial class CowDetailPage : ViewModelPage
{
    readonly CowDetailViewModel _viewModel;
    bool _shown;

    public CowDetailPage(CowDetailViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Reloads when coming back from a health check, so the new audit shows.</summary>
    protected override Task OnAppearedAsync()
    {
        if (!_shown)
        {
            _shown = true;
            return Task.CompletedTask;
        }
        return _viewModel.RefreshCommand.ExecuteAsync(null);
    }
}

/// <summary>
/// The capture flow's camera: a live preview with a guide frame (square for the muzzle, wide for
/// the side), the phone's camera app or gallery as fallbacks, and everything else in the view model.
/// </summary>
public partial class RegisterCowPage : ViewModelPage
{
    /// <summary>About 12 MP: plenty of muzzle detail without slow uploads over rural networks.</summary>
    const int MaxCaptureEdge = 4096;

    readonly RegisterCowViewModel _viewModel;
    bool _previewing;

    public RegisterCowPage(RegisterCowViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelChanged;
        CameraArea.SizeChanged += (_, _) => SizeReticle();
    }

    protected override async Task OnAppearedAsync()
    {
        if (!_viewModel.IsCapturing)
            return;
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
        {
            await DisplayAlertAsync("Camera access", "Allow camera access to photograph the animal, or pick photos from the gallery.", "OK");
            return;
        }
        await StartPreviewAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        StopPreview();
    }

    async Task StartPreviewAsync()
    {
        if (_previewing)
            return;
        try
        {
            var cameras = await Camera.GetAvailableCameras(CancellationToken.None);
            var rear = cameras.FirstOrDefault(c => c.Position == CameraPosition.Rear) ?? cameras.FirstOrDefault();
            if (rear is null)
                return;

            Camera.SelectedCamera = rear;
            var sizes = rear.SupportedResolutions;
            if (sizes.Count > 0)
            {
                var withinLimit = sizes.Where(s => Math.Max(s.Width, s.Height) <= MaxCaptureEdge).ToList();
                Camera.ImageCaptureResolution = withinLimit.Count > 0
                    ? withinLimit.MaxBy(s => s.Width * s.Height)
                    : sizes.MinBy(s => s.Width * s.Height);
            }

            await Camera.StartCameraPreview(CancellationToken.None);
            _previewing = true;
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Camera unavailable", $"{ex.Message} Use the camera app or the gallery instead.", "OK");
        }
    }

    void StopPreview()
    {
        if (!_previewing)
            return;
        Camera.StopCameraPreview();
        _previewing = false;
    }

    async void OnShutter(object? sender, EventArgs e)
    {
        Shutter.IsEnabled = false;
        try
        {
            await using var photo = await Camera.CaptureImage(CancellationToken.None);
            await AcceptAsync(photo);
        }
        catch (Exception ex)
        {
            await DisplayAlertAsync("Couldn't take the photo", ex.Message, "OK");
        }
        finally
        {
            Shutter.IsEnabled = true;
        }
    }

    async void OnSystemCamera(object? sender, EventArgs e) => await AcceptAsync(await MediaPicker.Default.CapturePhotoAsync());

    async void OnPickFromGallery(object? sender, EventArgs e) =>
        await AcceptAsync((await MediaPicker.Default.PickPhotosAsync())?.FirstOrDefault());

    async Task AcceptAsync(FileResult? file)
    {
        if (file is null)
            return;
        await using var photo = await file.OpenReadAsync();
        await AcceptAsync(photo);
    }

    async Task AcceptAsync(Stream photo)
    {
        using var buffer = new MemoryStream();
        await photo.CopyToAsync(buffer);
        _viewModel.AcceptPhoto(buffer.ToArray());
    }

    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(RegisterCowViewModel.Step))
            return;
        SizeReticle();
        if (_viewModel.IsCapturing)
            _ = StartPreviewAsync();
        else
            StopPreview();
    }

    /// <summary>The face frame is a near-square for the muzzle and forehead; the side frame is wide for the whole body.</summary>
    void SizeReticle()
    {
        var width = CameraArea.Width;
        var height = CameraArea.Height;
        if (width <= 0 || height <= 0)
            return;

        if (_viewModel.Step == RegistrationStep.Face)
        {
            var side = Math.Min(width * 0.72, height * 0.6);
            Reticle.WidthRequest = side;
            Reticle.HeightRequest = side * 1.15;
        }
        else
        {
            Reticle.WidthRequest = width * 0.92;
            Reticle.HeightRequest = Math.Min(height * 0.7, width * 0.92 * 0.62);
        }
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove)
            _viewModel.PropertyChanged -= OnViewModelChanged;
        base.OnNavigatedFrom(args);
    }
}
