namespace NdeipiChat.Client.Livestock;

/// <summary>A photo's size, read from its JPEG or PNG header without decoding the pixels.</summary>
public static class ImageDimensions
{
    public static (int Width, int Height)? Read(byte[] data)
    {
        if (data is [0x89, 0x50, 0x4E, 0x47, ..] && data.Length >= 24)
            return (BigEndian32(data, 16), BigEndian32(data, 20)); // IHDR follows the 8-byte signature

        if (data is not [0xFF, 0xD8, ..])
            return null;

        // Walk the JPEG segments to the start-of-frame, which carries the size.
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF)
                return null;
            var marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++; // padding
                continue;
            }
            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                i += 2; // markers without a length
                continue;
            }

            var isStartOfFrame = marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC;
            if (isStartOfFrame)
                return ((data[i + 7] << 8) | data[i + 8], (data[i + 5] << 8) | data[i + 6]);

            i += 2 + ((data[i + 2] << 8) | data[i + 3]);
        }
        return null;
    }

    static int BigEndian32(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
