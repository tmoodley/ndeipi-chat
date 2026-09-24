using SkiaSharp;

namespace NdeipiChat.Api.Livestock;

/// <summary>A region of an image as fractions of its width and height, origin top-left.</summary>
public sealed record NormalizedBox(double X, double Y, double Width, double Height);

public static class CattleImages
{
    static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>"jpg", "png", or null for anything else -- judged by content, not by what the client claims.</summary>
    public static string? Extension(byte[] data) => data switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "jpg",
        [0x89, 0x50, 0x4E, 0x47, ..] => "png",
        _ => null
    };

    public static string ContentType(string reference) => reference.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg";

    /// <summary>Decodes a photo the right way up: phones store rotation as an EXIF tag, not in the pixels.</summary>
    public static SKBitmap? DecodeUpright(byte[] data)
    {
        using var stream = new MemoryStream(data);
        using var codec = SKCodec.Create(stream);
        if (codec is null)
            return null;
        var bitmap = SKBitmap.Decode(codec);
        if (bitmap is null)
            return null;

        return codec.EncodedOrigin switch
        {
            SKEncodedOrigin.BottomRight => Rotate(bitmap, 180),
            SKEncodedOrigin.RightTop => Rotate(bitmap, 90),
            SKEncodedOrigin.LeftBottom => Rotate(bitmap, 270),
            _ => bitmap
        };
    }

    /// <summary>A JPEG no longer than <paramref name="maxEdge"/> on its long side.</summary>
    public static byte[] ToJpeg(SKBitmap bitmap, int maxEdge, int quality = 85)
    {
        var scale = Math.Min(1d, maxEdge / (double)Math.Max(bitmap.Width, bitmap.Height));
        var width = Math.Max(1, (int)Math.Round(bitmap.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bitmap.Height * scale));

        using var resized = bitmap.Resize(new SKImageInfo(width, height), Sampling);
        using var image = SKImage.FromBitmap(resized);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return encoded.ToArray();
    }

    /// <summary>The pixel rectangle for a normalised box plus padding, or the whole image if there's no box.</summary>
    public static SKRectI Region(SKBitmap bitmap, NormalizedBox? box, double padding)
    {
        if (box is null || box.Width <= 0 || box.Height <= 0)
            return new SKRectI(0, 0, bitmap.Width, bitmap.Height);

        var left = Math.Clamp((box.X - box.Width * padding) * bitmap.Width, 0, bitmap.Width - 1);
        var top = Math.Clamp((box.Y - box.Height * padding) * bitmap.Height, 0, bitmap.Height - 1);
        var right = Math.Clamp((box.X + box.Width * (1 + padding)) * bitmap.Width, left + 1, bitmap.Width);
        var bottom = Math.Clamp((box.Y + box.Height * (1 + padding)) * bitmap.Height, top + 1, bitmap.Height);

        // A sliver isn't a muzzle; fall back to the whole face.
        if (right - left < 16 || bottom - top < 16)
            return new SKRectI(0, 0, bitmap.Width, bitmap.Height);
        return new SKRectI((int)left, (int)top, (int)right, (int)bottom);
    }

    /// <summary>A crop resized to the model's input and laid out as a normalised CHW float tensor.</summary>
    public static float[] ToChwTensor(SKBitmap bitmap, SKRectI region, int width, int height, float[] mean, float[] std)
    {
        using var crop = new SKBitmap();
        if (!bitmap.ExtractSubset(crop, region))
            throw new InvalidOperationException("The muzzle region is outside the photo.");
        using var resized = crop.Resize(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul), Sampling);

        var plane = width * height;
        var tensor = new float[3 * plane];
        var pixels = resized.Pixels;
        for (var i = 0; i < plane; i++)
        {
            var p = pixels[i];
            tensor[i] = (p.Red / 255f - mean[0]) / std[0];
            tensor[plane + i] = (p.Green / 255f - mean[1]) / std[1];
            tensor[2 * plane + i] = (p.Blue / 255f - mean[2]) / std[2];
        }
        return tensor;
    }

    static SKBitmap Rotate(SKBitmap source, int degrees)
    {
        var quarterTurn = degrees % 180 != 0;
        var rotated = new SKBitmap(quarterTurn ? source.Height : source.Width, quarterTurn ? source.Width : source.Height);
        using (var canvas = new SKCanvas(rotated))
        using (var image = SKImage.FromBitmap(source))
        {
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(degrees);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawImage(image, 0, 0, Sampling);
        }
        source.Dispose();
        return rotated;
    }
}
