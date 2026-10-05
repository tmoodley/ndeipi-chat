using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Chat;

public sealed class ChatMediaOptions
{
    public const string Section = "ChatMedia";

    /// <summary>Where chat photos and videos are kept.</summary>
    public string Path { get; set; } = "App_Data/chat-media";

    /// <summary>The longest edge of the photo shown full size, and of the gallery thumbnail.</summary>
    public int FullEdge { get; set; } = 1600;
    public int ThumbEdge { get; set; } = 480;
}

/// <summary>
/// Photos and videos in chats (SRS §3.3). Photos are re-encoded here whatever the phone sent, to a
/// full size and a thumbnail (§4.1 media compression, for slow connections); videos are kept as
/// sent, up to <see cref="MarketContract.MaxVideoBytes"/>, since there's no transcoder on the server.
/// </summary>
public sealed class ChatMediaService(ChatDbContext db, IOptions<ChatMediaOptions> options, IWebHostEnvironment environment, TimeProvider clock)
{
    public const string Full = "full", Thumb = "thumb", Video = "video";

    string Root => System.IO.Path.IsPathRooted(options.Value.Path)
        ? options.Value.Path
        : System.IO.Path.Combine(environment.ContentRootPath, options.Value.Path);

    public async Task<MediaItem> UploadAsync(User user, Guid conversationId, IFormFile file, CancellationToken ct)
    {
        if (!await db.Members.AnyAsync(m => m.ConversationId == conversationId && m.UserId == user.Id, ct))
            throw new ChatRejectedException("You aren't a member of this conversation.");
        if (file.Length == 0)
            throw new ChatRejectedException("That file is empty.");
        if (file.Length > MarketContract.MaxVideoBytes)
            throw new ChatRejectedException($"Files can be up to {MarketContract.MaxVideoBytes / 1024 / 1024} MB. Try a shorter video.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var media = new ChatMedia
        {
            Id = Guid.NewGuid(),
            UploaderId = user.Id,
            ConversationId = conversationId,
            Type = MediaTypes.Image,
            ContentType = "image/jpeg",
            Size = bytes.Length,
            CreatedAt = clock.GetUtcNow()
        };
        Directory.CreateDirectory(Root);

        if (SniffVideo(bytes) is { } video)
        {
            media.Type = MediaTypes.Video;
            media.ContentType = video.ContentType;
            await File.WriteAllBytesAsync(PathOf(media.Id, Video + video.Extension), bytes, ct);
        }
        else
        {
            if (bytes.Length > MarketContract.MaxImageBytes)
                throw new ChatRejectedException($"Photos can be up to {MarketContract.MaxImageBytes / 1024 / 1024} MB.");
            using var bitmap = CattleImages.DecodeUpright(bytes)
                ?? throw new ChatRejectedException("Send a photo (JPEG, PNG or WebP) or a video (MP4, MOV, WebM or 3GP).");
            var full = CattleImages.ToJpeg(bitmap, options.Value.FullEdge, 80);
            await File.WriteAllBytesAsync(PathOf(media.Id, Full + ".jpg"), full, ct);
            await File.WriteAllBytesAsync(PathOf(media.Id, Thumb + ".jpg"), CattleImages.ToJpeg(bitmap, options.Value.ThumbEdge, 72), ct);
            var scale = Math.Min(1d, options.Value.FullEdge / (double)Math.Max(bitmap.Width, bitmap.Height));
            (media.Width, media.Height, media.Size) = ((int)Math.Round(bitmap.Width * scale), (int)Math.Round(bitmap.Height * scale), full.Length);
        }

        db.ChatMedia.Add(media);
        await db.SaveChangesAsync(ct);
        return ToItem(media);
    }

    /// <summary>A video, from its first bytes: MP4 and MOV and 3GP ("ftyp" box), or WebM (EBML).</summary>
    static (string ContentType, string Extension)? SniffVideo(byte[] b)
    {
        if (b is [0x1A, 0x45, 0xDF, 0xA3, ..])
            return ("video/webm", ".webm");
        if (b.Length < 12 || b[4] != 'f' || b[5] != 't' || b[6] != 'y' || b[7] != 'p')
            return null;
        var brand = System.Text.Encoding.ASCII.GetString(b, 8, 4);
        // HEIC/AVIF photos are ISO media files too: those are images, not video.
        if (brand is "heic" or "heix" or "mif1" or "msf1" or "avif" or "heim" or "heis")
            return null;
        return brand.StartsWith("qt", StringComparison.Ordinal) ? ("video/quicktime", ".mov")
            : brand.StartsWith("3g", StringComparison.Ordinal) ? ("video/3gpp", ".3gp")
            : ("video/mp4", ".mp4");
    }

    string PathOf(Guid id, string name) => System.IO.Path.Combine(Root, $"{id:N}-{name}");

    /// <summary>A file of a photo (full or thumb) or video, for serving; null if there's no such thing.</summary>
    public (string Path, string ContentType)? Find(Guid id, string variant)
    {
        if (variant is Full or Thumb)
            return File.Exists(PathOf(id, variant + ".jpg")) ? (PathOf(id, variant + ".jpg"), "image/jpeg") : null;
        if (variant != Video || !Directory.Exists(Root))
            return null;
        var path = Directory.EnumerateFiles(Root, $"{id:N}-{Video}.*").FirstOrDefault();
        return path is null ? null : (path, System.IO.Path.GetExtension(path) switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".3gp" => "video/3gpp",
            _ => "video/mp4"
        });
    }

    public static MediaItem ToItem(ChatMedia m) => m.Type == MediaTypes.Video
        ? new MediaItem(m.Id, m.Type, $"media/chat/{m.Id:N}/{Video}", null, m.Width, m.Height, m.Size)
        : new MediaItem(m.Id, m.Type, $"media/chat/{m.Id:N}/{Full}", $"media/chat/{m.Id:N}/{Thumb}", m.Width, m.Height, m.Size);

    /// <summary>
    /// The media a message may carry, in the order given: uploaded by the sender for this chat and not
    /// sent yet, or (when forwarding) already sent in a chat the sender is in. Fresh uploads are
    /// claimed by the message.
    /// </summary>
    public async Task<IReadOnlyList<MediaItem>> ClaimAsync(MessageContext context, IReadOnlyList<Guid> ids, bool forwarded, CancellationToken ct)
    {
        if (ids.Count > MarketContract.MaxMedia)
            throw new ChatRejectedException($"Send up to {MarketContract.MaxMedia} photos or videos at a time.");
        var distinct = ids.Distinct().ToList();
        var rows = await db.ChatMedia.Where(m => distinct.Contains(m.Id)).ToListAsync(ct);
        var items = new List<MediaItem>();
        foreach (var id in distinct)
        {
            var m = rows.FirstOrDefault(r => r.Id == id) ?? throw new ChatRejectedException("A photo or video is missing. Please add it again.");
            var fresh = m.UploaderId == context.Sender.Id && m.ConversationId == context.Conversation.Id && m.MessageId is null;
            if (fresh)
            {
                m.MessageId = context.MessageId;
            }
            else if (!forwarded || !await db.Members.AnyAsync(x => x.ConversationId == m.ConversationId && x.UserId == context.Sender.Id, ct))
            {
                throw new ChatRejectedException("A photo or video can't be sent here. Please add it again.");
            }
            items.Add(ToItem(m));
        }
        return items;
    }
}

/// <summary>"media" messages: a gallery of photos and videos with an optional caption.</summary>
public sealed class MediaMessageHandler(ChatMediaService media) : IMessageKindHandler
{
    public string Kind => MessageKinds.Media;

    public async Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        var sent = MessagePayload.Read<MediaPayload>(payload);
        if (sent.Items is not { Count: > 0 })
            throw new ChatRejectedException("Add a photo or video.");
        var caption = sent.Caption?.Trim();
        if (caption is { Length: > MarketContract.MaxCaptionLength })
            throw new ChatRejectedException($"Keep the caption under {MarketContract.MaxCaptionLength} characters.");
        var items = await media.ClaimAsync(context, sent.Items.Select(i => i.Id).ToList(), sent.Forwarded, ct);
        return new PreparedMessage(ContractJson.ToElement(new MediaPayload(items, string.IsNullOrEmpty(caption) ? null : caption, sent.Forwarded)));
    }
}

/// <summary>Forwards a message to other chats (SRS §3.2): the same text, gallery or listing, marked "Forwarded".</summary>
public sealed class ForwardService(ChatDbContext db, MessageService messages)
{
    public const int MaxTargets = 10;

    public async Task<IReadOnlyList<MessageDto>> ForwardAsync(User user, Guid messageId, ForwardRequest request, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null || !await db.Members.AnyAsync(m => m.ConversationId == message.ConversationId && m.UserId == user.Id, ct))
            throw new ChatRejectedException("That message isn't here any more.");
        if (!MessageKinds.Forwardable.Contains(message.Kind))
            throw new ChatRejectedException("That kind of message can't be forwarded.");
        var targets = (request.ConversationIds ?? []).Distinct().ToList();
        if (targets.Count is 0 or > MaxTargets)
            throw new ChatRejectedException($"Choose up to {MaxTargets} chats to forward to.");

        using var original = JsonDocument.Parse(message.PayloadJson);
        object payload = message.Kind switch
        {
            MessageKinds.Text => new TextPayload(MessagePayload.Read<TextPayload>(original.RootElement).Text, Forwarded: true),
            MessageKinds.Media => MessagePayload.Read<MediaPayload>(original.RootElement) with { Forwarded = true },
            _ => MessagePayload.Read<MarketListingPayload>(original.RootElement) with { Forwarded = true }
        };

        var sent = new List<MessageDto>();
        foreach (var target in targets)
        {
            // The same forward twice (a retry) lands once: its client id comes from what and where.
            var clientId = Deterministic(messageId, target, user.Id);
            sent.Add(await messages.SendAsync(user, new SendMessageRequest(target, message.Kind, ContractJson.ToElement(payload), clientId), ct));
        }
        return sent;
    }

    static Guid Deterministic(Guid messageId, Guid conversationId, Guid userId)
    {
        Span<byte> input = stackalloc byte[48];
        messageId.TryWriteBytes(input[..16]);
        conversationId.TryWriteBytes(input[16..32]);
        userId.TryWriteBytes(input[32..]);
        return new Guid(System.Security.Cryptography.SHA256.HashData(input)[..16]);
    }
}
