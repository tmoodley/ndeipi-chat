using System.Collections.Specialized;
using NdeipiChat.App.Extensions;
using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.App.Pages;

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
