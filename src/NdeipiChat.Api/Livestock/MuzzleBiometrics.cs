using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using NdeipiChat.Api.Data;
using SkiaSharp;

namespace NdeipiChat.Api.Livestock;

/// <summary>The spec's biometric node: a muzzle photo in, a muzzle-print vector out.</summary>
public interface IMuzzleEmbedder
{
    bool IsAvailable { get; }
    string ModelId { get; }

    /// <summary>An L2-normalised embedding of the muzzle -- cropped to <paramref name="muzzle"/> when given.</summary>
    float[] Embed(SKBitmap face, NormalizedBox? muzzle);
}

/// <summary>Runs a muzzle-print model exported to ONNX: one NCHW float image in, one embedding out.</summary>
public sealed class OnnxMuzzleEmbedder : IMuzzleEmbedder, IDisposable
{
    static readonly float[] ImageNetMean = [0.485f, 0.456f, 0.406f];
    static readonly float[] ImageNetStd = [0.229f, 0.224f, 0.225f];

    readonly MuzzleModelOptions _options;
    readonly string _path;
    readonly Lazy<InferenceSession> _session;

    public OnnxMuzzleEmbedder(IOptions<LivestockOptions> options, IWebHostEnvironment environment)
    {
        _options = options.Value.Muzzle;
        _path = _options.ModelPath.Length == 0 || Path.IsPathRooted(_options.ModelPath)
            ? _options.ModelPath
            : Path.Combine(environment.ContentRootPath, _options.ModelPath);
        _session = new Lazy<InferenceSession>(() => new InferenceSession(_path));
    }

    public bool IsAvailable => _path.Length > 0 && File.Exists(_path);

    public string ModelId => _options.ModelId.Length > 0 ? _options.ModelId : Path.GetFileNameWithoutExtension(_path);

    public float[] Embed(SKBitmap face, NormalizedBox? muzzle)
    {
        var session = _session.Value;
        var region = CattleImages.Region(face, muzzle, _options.CropPadding);
        var pixels = CattleImages.ToChwTensor(face, region, _options.InputWidth, _options.InputHeight, _options.Mean ?? ImageNetMean, _options.Std ?? ImageNetStd);
        var input = new DenseTensor<float>(pixels, [1, 3, _options.InputHeight, _options.InputWidth]);

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(session.InputMetadata.Keys.First(), input)]);
        return MuzzleVectors.Normalise(results.First().AsEnumerable<float>().ToArray());
    }

    public void Dispose()
    {
        if (_session.IsValueCreated)
            _session.Value.Dispose();
    }
}

public static class MuzzleVectors
{
    public static float[] Normalise(float[] vector)
    {
        var length = Math.Sqrt(vector.Sum(v => (double)v * v));
        return length == 0 ? vector : vector.Select(v => (float)(v / length)).ToArray();
    }

    /// <summary>Cosine similarity of two normalised vectors: their dot product.</summary>
    public static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length)
            return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
            dot += a[i] * b[i];
        return dot;
    }

    public static byte[] ToBytes(float[] vector) => MemoryMarshal.AsBytes(vector.AsSpan()).ToArray();

    public static float[] FromBytes(byte[] bytes) => MemoryMarshal.Cast<byte, float>(bytes).ToArray();
}

/// <summary>
/// Every enrolled muzzle, in memory, for identity resolution against the whole register. Loads
/// once, then picks up registrations from other API instances incrementally before each search.
/// Past a few hundred thousand animals, move this to a vector index (SQL Server 2025 VECTOR or a
/// dedicated vector store).
/// </summary>
public sealed class MuzzleIndex
{
    // Overlap each incremental sync so rows committed out of timestamp order aren't missed.
    static readonly TimeSpan SyncOverlap = TimeSpan.FromMinutes(5);

    readonly ConcurrentDictionary<string, (float[] Vector, Guid OwnerId)> _entries = new();
    readonly SemaphoreSlim _sync = new(1, 1);
    string? _modelId;
    DateTime _syncedThrough = DateTime.MinValue;

    public async Task SyncAsync(ChatDbContext db, string modelId, CancellationToken ct)
    {
        await _sync.WaitAsync(ct);
        try
        {
            if (_modelId != modelId)
            {
                // A different model's vectors aren't comparable; start again.
                _entries.Clear();
                _syncedThrough = DateTime.MinValue;
                _modelId = modelId;
            }

            var since = _syncedThrough == DateTime.MinValue ? DateTime.MinValue : _syncedThrough - SyncOverlap;
            var rows = await db.Livestock.AsNoTracking()
                .Where(c => c.IsActive && c.EmbeddingModel == modelId && c.MuzzleEmbedding != null && c.RegistrationTimestamp >= since)
                .Select(c => new { c.CowId, c.OwnerUserId, c.MuzzleEmbedding, c.RegistrationTimestamp })
                .ToListAsync(ct);

            foreach (var row in rows)
                _entries[row.CowId] = (MuzzleVectors.FromBytes(row.MuzzleEmbedding!), row.OwnerUserId);
            if (rows.Count > 0)
                _syncedThrough = rows.Max(r => r.RegistrationTimestamp);
        }
        finally
        {
            _sync.Release();
        }
    }

    public (string CowId, Guid OwnerId, double Similarity)? BestMatch(float[] vector)
    {
        (string, Guid, double)? best = null;
        foreach (var (cowId, entry) in _entries)
        {
            var similarity = MuzzleVectors.Cosine(vector, entry.Vector);
            if (best is null || similarity > best.Value.Item3)
                best = (cowId, entry.OwnerId, similarity);
        }
        return best;
    }

    public void Add(string cowId, float[] vector, Guid ownerId) => _entries[cowId] = (vector, ownerId);
}
