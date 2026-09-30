using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using NdeipiChat.SubApps.DevHost;
using SubAppTemplate;

// Runs the app in the dev host: a stand-in for the Ndeipi shell. The mocks below play the part of
// the app's API module (NdeipiChat.Api, under api/sample-id), so the app can be built before the
// API exists. Keep them close to what the real API will do, errors included.

var builder = WebAssemblyHostBuilder.CreateDefault(args);

builder.AddSubAppDevHost<SampleNameApp>(dev =>
{
    dev.Title = "Sample Title";

    var items = new List<ItemDto>
    {
        new(Guid.NewGuid(), "Welcome to Sample Title", dev.Users[1].Id, dev.Users[1].DisplayName, DateTimeOffset.Now.AddHours(-2)),
    };

    dev.Api.MapGet(SampleNameContract.BasePath + "/items", _ => items.OrderByDescending(i => i.AddedAt).ToList());

    dev.Api.MapPost(SampleNameContract.BasePath + "/items", async request =>
    {
        var title = request.ReadJson<AddItemRequest>()?.Title?.Trim();
        if (string.IsNullOrEmpty(title))
            return DevResults.BadRequest("Enter something to add.");
        if (title.Length > SampleNameContract.MaxTitleLength)
            return DevResults.BadRequest($"Keep it under {SampleNameContract.MaxTitleLength} characters.");

        var item = new ItemDto(Guid.NewGuid(), title, request.User.Id, request.User.DisplayName, DateTimeOffset.Now);
        items.Add(item);
        await request.Realtime.PublishAsync(SampleNameContract.ItemsTopic, new ItemChanged(item, Removed: false));
        return DevResults.Created(item);
    });

    dev.Api.MapDelete(SampleNameContract.BasePath + "/items/{id}", async request =>
    {
        var item = items.FirstOrDefault(i => i.Id.ToString() == request.Route("id"));
        if (item is null)
            return DevResults.NotFound();
        if (item.AddedById != request.User.Id)
            return DevResults.Forbidden("Only the person who added this can remove it.");

        items.Remove(item);
        await request.Realtime.PublishAsync(SampleNameContract.ItemsTopic, new ItemChanged(item, Removed: true));
        return DevResults.NoContent();
    });
});

await builder.Build().RunAsync();
