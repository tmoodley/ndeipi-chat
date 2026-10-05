using NdeipiChat.Client;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Platform;

/// <summary>
/// "Photo or video" in a chat's "+" panel (SRS §3.3): pick photos (resized on the phone before they
/// go, for slow connections, §4.1) or a video, or take a photo; they're uploaded and sent together as
/// one gallery.
/// </summary>
public sealed class MediaComposerAction(ChatApi api, ChatSession session) : IComposerAction
{
    const string Gallery = "Photos from the gallery", Camera = "Take a photo", Video = "A video from the gallery";

    public string Title => "Photo or video";
    public string Glyph => "📷";
    public int Order => 5;

    public async Task ExecuteAsync(ComposerContext context)
    {
        var choice = await Shell.Current.DisplayActionSheetAsync("Send", "Cancel", null, Gallery, Camera, Video);
        try
        {
            var files = choice switch
            {
                Gallery => await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions
                {
                    SelectionLimit = MarketContract.MaxMedia,
                    MaximumWidth = 1600,
                    MaximumHeight = 1600,
                    CompressionQuality = 80
                }),
                Camera => [await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { MaximumWidth = 1600, MaximumHeight = 1600, CompressionQuality = 80 })],
                Video => [await MediaPicker.Default.PickVideoAsync()],
                _ => []
            };
            var picked = files.OfType<FileResult>().Take(MarketContract.MaxMedia).ToList();
            if (picked.Count == 0)
                return;

            var items = new List<MediaItem>();
            foreach (var file in picked)
            {
                await using var stream = await file.OpenReadAsync();
                if (choice == Video && stream.CanSeek && stream.Length > MarketContract.MaxVideoBytes)
                {
                    await Shell.Current.DisplayAlertAsync("Video too long", $"Videos can be up to {MarketContract.MaxVideoBytes / 1024 / 1024} MB. Try a shorter one.", "OK");
                    return;
                }
                items.Add(await api.UploadChatMediaAsync(context.Conversation.Id, stream, file.FileName, file.ContentType ?? "application/octet-stream"));
            }
            await session.SendAsync(context.Conversation.Id, MessageKinds.Media, new MediaPayload(items), Guid.NewGuid());
        }
        catch (PermissionException)
        {
            await Shell.Current.DisplayAlertAsync("Camera", "Allow Ndeipi to use the camera in your phone's settings to take a photo.", "OK");
        }
        catch (ApiException ex)
        {
            await Shell.Current.DisplayAlertAsync("Couldn't send", ex.Message, "OK");
        }
        catch (ChatSendException ex)
        {
            await Shell.Current.DisplayAlertAsync("Couldn't send", ex.Message, "OK");
        }
    }
}
