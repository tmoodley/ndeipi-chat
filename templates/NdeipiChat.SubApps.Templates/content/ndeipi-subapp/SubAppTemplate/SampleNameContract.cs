namespace SubAppTemplate;

// What the app and its API agree on. When the app joins Ndeipi, this moves to NdeipiChat.Contracts
// so the API module shares it (see docs/micro-apps.md).

public static class SampleNameContract
{
    /// <summary>The app's id: its launcher entry, its route (apps/sample-id) and its API's filter.</summary>
    public const string AppId = "sample-id";

    public const string BasePath = "api/sample-id";

    /// <summary>The realtime topic every change to the list is published on.</summary>
    public const string ItemsTopic = "sample-id:items";

    public const int MaxTitleLength = 120;
}

public sealed record ItemDto(Guid Id, string Title, Guid AddedById, string AddedBy, DateTimeOffset AddedAt);

public sealed record AddItemRequest(string Title);

/// <summary>Published on <see cref="SampleNameContract.ItemsTopic"/>: an item added, or removed (Removed = true).</summary>
public sealed record ItemChanged(ItemDto Item, bool Removed);
