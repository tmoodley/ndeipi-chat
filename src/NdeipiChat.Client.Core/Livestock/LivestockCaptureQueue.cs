using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NdeipiChat.Client.Livestock;

public static class CaptureStatuses
{
    /// <summary>Waiting to upload -- no connection yet, or the server asked to try later.</summary>
    public const string Pending = "Pending";

    /// <summary>The server refused it (unusable photos, duplicate); the farmer needs to act.</summary>
    public const string Rejected = "Rejected";
}

/// <param name="MetadataJson">Stored exactly as it will be signed and sent.</param>
public sealed record PendingCapture(
    Guid Id,
    DateTimeOffset CreatedAt,
    string Label,
    string MetadataJson,
    string FacePath,
    string FlankPath,
    string Status,
    int Attempts,
    string? LastError);

/// <summary>
/// Where captures wait to upload. Every capture is saved here first, then uploaded, so a farmer out
/// of signal loses nothing. The app keeps them in SQLite (<see cref="LivestockCaptureQueue"/>); the
/// web app, where SQLite and files don't survive a reload, in IndexedDB.
/// </summary>
public interface ILivestockCaptureQueue
{
    Task<PendingCapture> EnqueueAsync(byte[] face, byte[] flank, string metadataJson, string label, CancellationToken ct = default);
    Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken ct = default);
    Task<PendingCapture?> GetAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(PendingCapture capture, CancellationToken ct = default);

    /// <summary>Removes a capture and its photos -- once uploaded, or when the farmer discards it.</summary>
    Task RemoveAsync(Guid id, CancellationToken ct = default);

    Task<(byte[] Face, byte[] Flank)> ReadPhotosAsync(PendingCapture capture, CancellationToken ct = default);
}

/// <summary>
/// The spec's offline SQLite ingestion cache, with the photos as files beside it: they wait on the
/// phone until a connection returns.
/// </summary>
public sealed class LivestockCaptureQueue : ILivestockCaptureQueue
{
    readonly string _photoDirectory;
    readonly string _connectionString;
    readonly SemaphoreSlim _schema = new(1, 1);
    bool _ready;

    public LivestockCaptureQueue(ClientOptions options)
    {
        var directory = Path.Combine(options.DataDirectory, "livestock");
        _photoDirectory = Path.Combine(directory, "captures");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "capture-queue.db") }.ToString();
    }

    public async Task<PendingCapture> EnqueueAsync(byte[] face, byte[] flank, string metadataJson, string label, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var capture = new PendingCapture(
            Guid.NewGuid(), DateTimeOffset.UtcNow, label, metadataJson, "", "", CaptureStatuses.Pending, 0, null);
        capture = capture with
        {
            FacePath = Path.Combine(_photoDirectory, $"{capture.Id:N}-face.jpg"),
            FlankPath = Path.Combine(_photoDirectory, $"{capture.Id:N}-flank.jpg")
        };
        await File.WriteAllBytesAsync(capture.FacePath, face, ct);
        await File.WriteAllBytesAsync(capture.FlankPath, flank, ct);

        await using var insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO PendingRegistration (Id, CreatedAt, Label, MetadataJson, FacePath, FlankPath, Status, Attempts, LastError)
            VALUES ($id, $created, $label, $metadata, $face, $flank, $status, 0, NULL)
            """;
        insert.Parameters.AddWithValue("$id", capture.Id.ToString());
        insert.Parameters.AddWithValue("$created", capture.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("$label", label);
        insert.Parameters.AddWithValue("$metadata", metadataJson);
        insert.Parameters.AddWithValue("$face", capture.FacePath);
        insert.Parameters.AddWithValue("$flank", capture.FlankPath);
        insert.Parameters.AddWithValue("$status", capture.Status);
        await insert.ExecuteNonQueryAsync(ct);
        return capture;
    }

    public async Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT Id, CreatedAt, Label, MetadataJson, FacePath, FlankPath, Status, Attempts, LastError FROM PendingRegistration ORDER BY CreatedAt";
        var captures = new List<PendingCapture>();
        await using var reader = await select.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            captures.Add(Read(reader));
        return captures;
    }

    public async Task<PendingCapture?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT Id, CreatedAt, Label, MetadataJson, FacePath, FlankPath, Status, Attempts, LastError FROM PendingRegistration WHERE Id = $id";
        select.Parameters.AddWithValue("$id", id.ToString());
        await using var reader = await select.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task UpdateAsync(PendingCapture capture, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE PendingRegistration SET Status = $status, Attempts = $attempts, LastError = $error WHERE Id = $id";
        update.Parameters.AddWithValue("$id", capture.Id.ToString());
        update.Parameters.AddWithValue("$status", capture.Status);
        update.Parameters.AddWithValue("$attempts", capture.Attempts);
        update.Parameters.AddWithValue("$error", (object?)capture.LastError ?? DBNull.Value);
        await update.ExecuteNonQueryAsync(ct);
    }

    public async Task<(byte[] Face, byte[] Flank)> ReadPhotosAsync(PendingCapture capture, CancellationToken ct = default) =>
        (await File.ReadAllBytesAsync(capture.FacePath, ct), await File.ReadAllBytesAsync(capture.FlankPath, ct));

    public async Task RemoveAsync(Guid id, CancellationToken ct = default)
    {
        var capture = await GetAsync(id, ct);
        await using var connection = await OpenAsync(ct);
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM PendingRegistration WHERE Id = $id";
        delete.Parameters.AddWithValue("$id", id.ToString());
        await delete.ExecuteNonQueryAsync(ct);

        if (capture is not null)
        {
            File.Delete(capture.FacePath);
            File.Delete(capture.FlankPath);
        }
    }

    async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        if (!_ready)
        {
            await _schema.WaitAsync(ct);
            try
            {
                if (!_ready)
                {
                    Directory.CreateDirectory(_photoDirectory);
                    await connection.OpenAsync(ct);
                    await using var create = connection.CreateCommand();
                    create.CommandText = """
                        CREATE TABLE IF NOT EXISTS PendingRegistration (
                            Id TEXT PRIMARY KEY,
                            CreatedAt TEXT NOT NULL,
                            Label TEXT NOT NULL,
                            MetadataJson TEXT NOT NULL,
                            FacePath TEXT NOT NULL,
                            FlankPath TEXT NOT NULL,
                            Status TEXT NOT NULL,
                            Attempts INTEGER NOT NULL,
                            LastError TEXT NULL)
                        """;
                    await create.ExecuteNonQueryAsync(ct);
                    _ready = true;
                    return connection;
                }
            }
            finally
            {
                _schema.Release();
            }
        }
        await connection.OpenAsync(ct);
        return connection;
    }

    static PendingCapture Read(SqliteDataReader reader) => new(
        Guid.Parse(reader.GetString(0)),
        DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.GetInt32(7),
        reader.IsDBNull(8) ? null : reader.GetString(8));
}
