using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SkiaSharp;

namespace NdeipiChat.Tests.Infrastructure;

/// <summary>Anthropic's Messages API: answers with a canned assessment, and records every request.</summary>
public sealed class StubClaude : HttpMessageHandler
{
    public ConcurrentQueue<(HttpRequestMessage Request, JsonElement Body)> Requests { get; } = new();

    /// <summary>The assessment the next call returns; tests change it per scenario.</summary>
    public object Assessment { get; set; } = Assess();

    public static object Assess(
        string breed = "Boran",
        double confidence = 0.96,
        double bodyCondition = 5.8,
        bool isCattle = true,
        bool faceUsable = true,
        bool flankUsable = true,
        string[]? problems = null,
        object[]? anomalies = null,
        bool requiresVet = false,
        string rating = "PRIME") => new
    {
        isCattle,
        imageQuality = new { faceUsable, flankUsable, problems = problems ?? [] },
        detectedBreed = breed,
        breedConfidence = confidence,
        morphology = new { hasThoracicHump = true, dewlapProminence = "HIGH", hornState = "POLLED", earShape = "medium, horizontal", coatPattern = "white" },
        bodyConditionScore = bodyCondition,
        hydrationStatus = "NORMAL",
        anomalies = anomalies ?? [],
        requiresVetInspection = requiresVet,
        overallHealthRating = rating,
        muzzleBox = new { x = 0.0, y = 0.0, width = 1.0, height = 1.0 },
        notes = "Test assessment."
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
        Requests.Enqueue((request, body));

        var reply = new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = body.GetProperty("model").GetString(),
            content = new object[] { new { type = "tool_use", id = "toolu_test", name = "record_cattle_assessment", input = Assessment } },
            stop_reason = "tool_use",
            usage = new { input_tokens = 1, output_tokens = 1 }
        };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(reply), Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// Synthetic photos. A "cow" is a seed: its face is a 4×4 grid of colours, which the test muzzle
/// model reads as a 48-number print. Different seeds look nothing alike; jitter makes a new photo
/// of the same animal.
/// </summary>
public static class CowPhotos
{
    public static byte[] Face(int cow, int width = 1440, int height = 1080, int jitter = 0) => Grid(cow, width, height, jitter, 4);

    public static byte[] Flank(int cow, int width = 1280, int height = 720) => Grid(cow + 10_000, width, height, 0, 2);

    static byte[] Grid(int seed, int width, int height, int jitter, int cells)
    {
        var colours = new Random(seed);
        var noise = new Random(seed * 31 + jitter);
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint())
        {
            for (var row = 0; row < cells; row++)
            for (var column = 0; column < cells; column++)
            {
                byte Channel() => (byte)Math.Clamp(colours.Next(256) + (jitter == 0 ? 0 : noise.Next(-jitter, jitter + 1)), 0, 255);
                paint.Color = new SKColor(Channel(), Channel(), Channel());
                canvas.DrawRect(column * width / (float)cells, row * height / (float)cells, width / (float)cells, height / (float)cells, paint);
            }
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return jpeg.ToArray();
    }
}

/// <summary>
/// A real ONNX model small enough to write by hand: average-pool the input into a grid, flatten.
/// It exercises ONNX Runtime, the preprocessing and the similarity threshold exactly as a
/// production muzzle-print model would, without downloading one.
/// </summary>
public static class TinyMuzzleModel
{
    public const int InputSize = 224;
    public const int Grid = 4;

    public static readonly string Path = Write();

    static string Write()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ndeipi-muzzle-grid-{Guid.NewGuid():N}.onnx");
        File.WriteAllBytes(path, Build());
        return path;
    }

    static byte[] Build()
    {
        const int cell = InputSize / Grid;
        var pool = new Proto().Text(1, "image").Text(2, "pooled").Text(3, "pool").Text(4, "AveragePool")
            .Message(5, Ints("kernel_shape", cell, cell))
            .Message(5, Ints("strides", cell, cell));
        var flatten = new Proto().Text(1, "pooled").Text(2, "embedding").Text(3, "flatten").Text(4, "Flatten");
        var graph = new Proto()
            .Message(1, pool)
            .Message(1, flatten)
            .Text(2, "muzzle-grid")
            .Message(11, Tensor("image", 1, 3, InputSize, InputSize))
            .Message(12, Tensor("embedding", 1, 3 * Grid * Grid));

        return new Proto()
            .Number(1, 8)                                   // ir_version
            .Text(2, "ndeipi-tests")                        // producer_name
            .Message(7, graph)                              // graph
            .Message(8, new Proto().Text(1, "").Number(2, 13)) // opset_import: default domain, opset 13
            .ToArray();
    }

    static Proto Ints(string name, params long[] values)
    {
        var attribute = new Proto().Text(1, name);
        foreach (var value in values)
            attribute.Number(8, value);
        return attribute.Number(20, 7); // AttributeType.INTS
    }

    static Proto Tensor(string name, params long[] dims)
    {
        var shape = new Proto();
        foreach (var dim in dims)
            shape.Message(1, new Proto().Number(1, dim));
        var tensorType = new Proto().Number(1, 1).Message(2, shape); // elem_type FLOAT
        return new Proto().Text(1, name).Message(2, new Proto().Message(1, tensorType));
    }

    /// <summary>Just enough of the protobuf wire format for an ONNX ModelProto.</summary>
    sealed class Proto
    {
        readonly MemoryStream _bytes = new();

        public Proto Number(int field, long value)
        {
            Varint((ulong)(field << 3));
            Varint((ulong)value);
            return this;
        }

        public Proto Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));

        public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());

        public byte[] ToArray() => _bytes.ToArray();

        Proto Bytes(int field, byte[] value)
        {
            Varint((ulong)((field << 3) | 2));
            Varint((ulong)value.Length);
            _bytes.Write(value);
            return this;
        }

        void Varint(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.WriteByte((byte)(value | 0x80));
                value >>= 7;
            }
            _bytes.WriteByte((byte)value);
        }
    }
}

/// <summary>A connection that can be cut, for the offline tests.</summary>
public sealed class SwitchableConnection(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    public bool Offline { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
        Offline ? throw new HttpRequestException("No connection.") : base.SendAsync(request, ct);
}
