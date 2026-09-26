using System.Collections.Specialized;
using NdeipiChat.App.Extensions;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Pages;

public partial class FeedPage : ViewModelPage
{
    readonly FeedViewModel _viewModel;

    public FeedPage(FeedViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Loads once; pull down to refresh. A post published here is added without a reload.</summary>
    protected override Task OnAppearedAsync() =>
        _viewModel.Posts.Count == 0 ? _viewModel.RefreshCommand.ExecuteAsync(null) : Task.CompletedTask;

    async void OnDelete(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is PostItemViewModel post
            && await DisplayAlertAsync("Delete post?", "This can't be undone.", "Delete", "Cancel"))
            await _viewModel.DeleteCommand.ExecuteAsync(post);
    }
}

public partial class ComposePostPage : ViewModelPage
{
    const int MaxEdge = 2048;

    readonly ComposePostViewModel _viewModel;

    public ComposePostPage(ComposePostViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Phone photos are shrunk as they're picked: less to upload, and well under the size limit.</summary>
    static MediaPickerOptions Options(int limit) => new()
    {
        SelectionLimit = limit,
        MaximumWidth = MaxEdge,
        MaximumHeight = MaxEdge,
        CompressionQuality = 90
    };

    async void OnTakePhoto(object? sender, EventArgs e) =>
        await AddAsync([await MediaPicker.Default.CapturePhotoAsync(Options(1))]);

    async void OnChoosePhotos(object? sender, EventArgs e) =>
        await AddAsync(await MediaPicker.Default.PickPhotosAsync(Options(SocialContract.MaxPhotos - _viewModel.Photos.Count)) ?? []);

    async Task AddAsync(IEnumerable<FileResult?> files)
    {
        foreach (var file in files)
        {
            if (file is null)
                continue;
            await using var stream = await file.OpenReadAsync();
            using var photo = new MemoryStream();
            await stream.CopyToAsync(photo);
            if (!_viewModel.AddPhoto(photo.ToArray()))
                break;
        }
    }
}

public partial class SignInPage : ViewModelPage
{
    public SignInPage(SignInViewModel viewModel) : base(viewModel) => InitializeComponent();
}

public partial class ChatsPage : ViewModelPage
{
    readonly ChatsViewModel _viewModel;

    public ChatsPage(ChatsViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.RefreshCommand.ExecuteAsync(null);

    async void OnNewChat(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//main/contacts");
}

public partial class ChatPage : ViewModelPage
{
    readonly ChatViewModel _viewModel;

    public ChatPage(ChatViewModel viewModel, MessageTemplateSelector templates) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        MessageList.ItemTemplate = templates;
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    /// <summary>Opening a chat lands on the newest message; KeepLastItemInView handles the rest.</summary>
    void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && _viewModel.Messages.Count == e.NewItems?.Count))
            Dispatcher.Dispatch(() =>
            {
                if (_viewModel.Messages.Count > 0)
                    MessageList.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            });
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove)
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
        base.OnNavigatedFrom(args);
    }
}

public partial class ContactsPage : ViewModelPage
{
    readonly ContactsViewModel _viewModel;

    public ContactsPage(ContactsViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);
}

public partial class MePage : ViewModelPage
{
    readonly MeViewModel _viewModel;

    public MePage(MeViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);
}

public partial class WalletPage : ViewModelPage
{
    readonly WalletViewModel _viewModel;

    public WalletPage(WalletViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Also runs when the user comes back from Bridge's KYC pages in the browser.</summary>
    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);
}

public partial class AssetTransferPage : ViewModelPage
{
    public AssetTransferPage(AssetTransferViewModel viewModel) : base(viewModel) => InitializeComponent();
}

public partial class BankTransferPage : ViewModelPage
{
    public BankTransferPage(BankTransferViewModel viewModel) : base(viewModel) => InitializeComponent();
}
