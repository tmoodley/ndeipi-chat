using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;
using SkiaSharp;

namespace NdeipiChat.Tests;

/// <summary>
/// The livestock registry end to end. Claude is stubbed; the muzzle model is a real (tiny) ONNX
/// model, so identity resolution runs through ONNX Runtime and the similarity threshold for real.
/// </summary>
public sealed class LivestockTests(TestApp app) : IClassFixture<TestApp>
{
    sealed record Operator(TestUser User, ECDsa Key, Guid KeyId);

    async Task<Operator> OperatorAsync(string name)
    {
        var user = await app.CreateUserAsync(name);
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var registered = await user.PostAsync<OperatorKeyDto>("api/v1/livestock/operator-keys",
            new OperatorKeyRequest(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "Test phone"));
        return new Operator(user, key, registered.KeyId);
    }

    static string Metadata(string? claimedBreed = "Boran", string? existingCowId = null) =>
        ContractJson.Write(new LivestockRegistrationMetadata(
            "urn:ndeipi:ranch:zm-st-041",
            "0x892aC62E12fE07E815F79B641c8f1eD6C9e49B31",
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            new GpsTelemetry(-15.78341, 26.01258, 2.5),
            new ManualOverrides(claimedBreed, "Female", 28),
            existingCowId));

    /// <summary>Uploads as <paramref name="sender"/>, signed with <paramref name="signer"/>'s key (the sender's by default).</summary>
    async Task<(HttpStatusCode Status, LivestockRegistrationResponse Body)> SubmitAsync(
        Operator sender, byte[] face, byte[] flank, string metadata, Operator? signer = null, byte[]? uploadedFace = null, bool sign = true)
    {
        var by = signer ?? sender;
        var signature = Convert.ToBase64String(by.Key.SignData(LivestockContract.SigningPayload(face, flank, metadata), HashAlgorithmName.SHA256));

        using var content = new MultipartFormDataContent
        {
            { Jpeg(uploadedFace ?? face), "face_image", "face.jpg" },
            { Jpeg(flank), "flank_image", "flank.jpg" },
            { new StringContent(metadata, Encoding.UTF8, "application/json"), "metadata" }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/livestock/register") { Content = content };
        if (sign)
        {
            request.Headers.Add("X-Ndeipi-Key-Id", by.KeyId.ToString());
            request.Headers.Add("X-Ndeipi-Signature", signature);
        }

        using var response = await sender.User.Http.SendAsync(request);
        return (response.StatusCode, (await response.Content.ReadFromJsonAsync<LivestockRegistrationResponse>(ContractJson.Options))!);
    }

    static ByteArrayContent Jpeg(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return content;
    }

    [Fact]
    public async Task A_cow_is_registered_from_its_face_and_side_photos()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Rancher");
        var face = CowPhotos.Face(1);
        var flank = CowPhotos.Flank(1);

        var (status, body) = await SubmitAsync(alice, face, flank, Metadata());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(RegistrationStatuses.Success, body.Status);
        Assert.Matches("^urn:ndeipi:asset:cattle:zm-[0-9]{8}$", body.CowId);
        Assert.Equal(EnrollmentStatuses.NewRegistration, body.Biometrics!.EnrollmentStatus);
        Assert.Matches("^[0-9a-f]{64}$", body.Biometrics.FaceVectorSha256);
        Assert.Equal(0.9, body.Biometrics.SimilarityThreshold);
        Assert.Equal(("Boran", 0.96, true, "Boran"), (body.Phenotype!.DetectedBreed, body.Phenotype.BreedConfidence, body.Phenotype.BreedConfirmed, body.Phenotype.RecordedBreed));
        Assert.True(body.Phenotype.MorphologicalTraits.HasThoracicHump);
        Assert.Equal((5.8, "1-9", "NORMAL", false, "PRIME"),
            (body.HealthScreening!.BodyConditionScore, body.HealthScreening.BcsScale, body.HealthScreening.HydrationStatus, body.HealthScreening.RequiresVetInspection, body.HealthScreening.OverallHealthRating));
        Assert.Empty(body.HealthScreening.DetectedAnomalies);
        Assert.Matches("^0x[0-9a-f]{64}$", body.AttestationHash);

        var cow = await app.DbAsync(db => db.Livestock.SingleAsync(c => c.CowId == body.CowId));
        Assert.Equal(("urn:ndeipi:ranch:zm-st-041", "Boran", 0.96m, "MODEL", "Female", 28), (cow.RanchId, cow.Breed, cow.BreedConfidence, cow.BreedSource, cow.Sex, cow.ApproximateAgeMonths ?? 0));
        Assert.Equal((-15.78341m, 26.01258m), (cow.Latitude, cow.Longitude));
        Assert.Equal("0x892aC62E12fE07E815F79B641c8f1eD6C9e49B31", cow.OwnerWallet);
        Assert.Equal("test-grid-pool", cow.EmbeddingModel);
        Assert.Equal(TinyMuzzleModel.Grid * TinyMuzzleModel.Grid * 3 * sizeof(float), cow.MuzzleEmbedding!.Length);
        var audit = await app.DbAsync(db => db.HealthAudits.SingleAsync(a => a.CowId == body.CowId));
        Assert.Equal((5.8m, "PRIME", false, alice.KeyId), (audit.BodyConditionScore, audit.HealthRating, audit.RequiresVetInspection, audit.OperatorKeyId));
        Assert.Equal(body.AttestationHash, "0x" + audit.AttestationHash);

        // What was asked of Claude: both photos, downscaled, and a forced structured answer.
        var (claudeRequest, claudeBody) = app.Claude.Requests.Last();
        Assert.Equal("claude-test-key", claudeRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", claudeRequest.Headers.GetValues("anthropic-version").Single());
        Assert.Equal("claude-opus-5", claudeBody.GetProperty("model").GetString());
        Assert.Equal("record_cattle_assessment", claudeBody.GetProperty("tool_choice").GetProperty("name").GetString());
        var blocks = claudeBody.GetProperty("messages")[0].GetProperty("content").EnumerateArray().ToList();
        var photos = blocks.Where(b => b.GetProperty("type").GetString() == "image").ToList();
        Assert.Equal(2, photos.Count);
        foreach (var photo in photos)
        {
            using var decoded = SKBitmap.Decode(Convert.FromBase64String(photo.GetProperty("source").GetProperty("data").GetString()!));
            Assert.True(Math.Max(decoded.Width, decoded.Height) <= 1568);
        }
        Assert.Contains(blocks, b => b.GetProperty("type").GetString() == "text" && b.GetProperty("text").GetString()!.Contains("breed Boran"));

        // The herd, the record and the photos.
        var herd = await alice.User.GetAsync<List<CowSummaryDto>>("api/v1/livestock/cows");
        var summary = Assert.Single(herd);
        Assert.Equal((body.CowId, 5.8, "PRIME"), (summary.CowId, summary.LatestBodyConditionScore, summary.LatestHealthRating));
        var detail = await alice.User.GetAsync<CowDetailDto>($"api/v1/livestock/cows/{Uri.EscapeDataString(body.CowId!)}");
        Assert.True(detail.BiometricallyEnrolled);
        Assert.Single(detail.Audits);

        var original = await alice.User.Http.GetByteArrayAsync($"api/v1/livestock/images/{summary.FaceImageRef}");
        Assert.Equal(face, original);
        using var thumbnail = SKBitmap.Decode(await alice.User.Http.GetByteArrayAsync($"api/v1/livestock/images/{summary.FaceImageRef}?width=160"));
        Assert.Equal(160, Math.Max(thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public async Task Rescanning_your_own_cow_records_a_health_audit_not_a_second_cow()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Rescan");
        var (_, first) = await SubmitAsync(alice, CowPhotos.Face(2), CowPhotos.Flank(2), Metadata());

        // A new photo of the same animal, a few days later.
        app.Claude.Assessment = StubClaude.Assess(bodyCondition: 4.6, rating: "GOOD");
        var (status, again) = await SubmitAsync(alice, CowPhotos.Face(2, jitter: 6), CowPhotos.Flank(2), Metadata());

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(first.CowId, again.CowId);
        Assert.Equal(EnrollmentStatuses.ExistingAnimal, again.Biometrics!.EnrollmentStatus);
        Assert.True(again.Biometrics.ClosestMatchSimilarity >= 0.9);
        Assert.Equal(1, await app.DbAsync(db => db.Livestock.CountAsync(c => c.OwnerUserId == alice.User.Id)));
        Assert.Equal(new[] { 4.6m, 5.8m },
            await app.DbAsync(db => db.HealthAudits.Where(a => a.CowId == first.CowId).OrderByDescending(a => a.TimestampUtc).Select(a => a.BodyConditionScore).ToListAsync()));
    }

    [Fact]
    public async Task A_cow_registered_to_someone_else_is_refused()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Owner");
        var bob = await OperatorAsync("Bob Neighbour");
        await SubmitAsync(alice, CowPhotos.Face(3), CowPhotos.Flank(3), Metadata());

        var (status, body) = await SubmitAsync(bob, CowPhotos.Face(3, jitter: 6), CowPhotos.Flank(3), Metadata());

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal(RegistrationStatuses.Duplicate, body.Status);
        Assert.Contains("already registered to another owner", Assert.Single(body.Problems!));
        Assert.Equal(0, await app.DbAsync(db => db.Livestock.CountAsync(c => c.OwnerUserId == bob.User.Id)));
    }

    [Fact]
    public async Task A_different_cow_is_a_new_registration()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Two Cows");
        var (_, first) = await SubmitAsync(alice, CowPhotos.Face(4), CowPhotos.Flank(4), Metadata());
        var (_, second) = await SubmitAsync(alice, CowPhotos.Face(5), CowPhotos.Flank(5), Metadata());

        Assert.NotEqual(first.CowId, second.CowId);
        Assert.Equal(EnrollmentStatuses.NewRegistration, second.Biometrics!.EnrollmentStatus);
        Assert.True(second.Biometrics.ClosestMatchSimilarity < 0.9);
    }

    [Fact]
    public async Task A_resent_upload_gets_the_original_answer()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Resend");
        var (face, flank, metadata) = (CowPhotos.Face(6), CowPhotos.Flank(6), Metadata());

        var (_, first) = await SubmitAsync(alice, face, flank, metadata);
        var claudeCalls = app.Claude.Requests.Count;
        var (status, second) = await SubmitAsync(alice, face, flank, metadata);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((first.CowId, first.AttestationHash), (second.CowId, second.AttestationHash));
        Assert.Equal(claudeCalls, app.Claude.Requests.Count);
        Assert.Equal(1, await app.DbAsync(db => db.HealthAudits.CountAsync(a => a.CowId == first.CowId)));
    }

    [Fact]
    public async Task Only_uploads_signed_by_the_operators_own_device_are_accepted()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Signer");
        var bob = await OperatorAsync("Bob Borrower");
        var (face, flank, metadata) = (CowPhotos.Face(7), CowPhotos.Flank(7), Metadata());

        var (swapped, swappedBody) = await SubmitAsync(alice, face, flank, metadata, uploadedFace: CowPhotos.Face(8));
        Assert.Equal(HttpStatusCode.Forbidden, swapped);
        Assert.Contains("isn't signed", Assert.Single(swappedBody.Problems!));

        var (borrowed, _) = await SubmitAsync(bob, face, flank, metadata, signer: alice);
        Assert.Equal(HttpStatusCode.Forbidden, borrowed);

        var (unsigned, _) = await SubmitAsync(alice, face, flank, metadata, sign: false);
        Assert.Equal(HttpStatusCode.Forbidden, unsigned);
    }

    [Fact]
    public async Task A_low_resolution_face_photo_is_refused_before_analysis()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Blurry");
        var claudeCalls = app.Claude.Requests.Count;

        var (status, body) = await SubmitAsync(alice, CowPhotos.Face(9, 1280, 720), CowPhotos.Flank(9), Metadata());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Contains("at least 1080 pixels", Assert.Single(body.Problems!));
        Assert.Equal(claudeCalls, app.Claude.Requests.Count);
    }

    [Fact]
    public async Task Photos_Claude_finds_unusable_come_back_with_its_advice()
    {
        app.Claude.Assessment = StubClaude.Assess(faceUsable: false, problems: ["Move closer so the muzzle fills the frame"]);
        var alice = await OperatorAsync("Alice Far Away");

        var (status, body) = await SubmitAsync(alice, CowPhotos.Face(10), CowPhotos.Flank(10), Metadata());

        Assert.Equal(HttpStatusCode.UnprocessableEntity, status);
        Assert.Equal(RegistrationStatuses.Rejected, body.Status);
        Assert.Equal(["Move closer so the muzzle fills the frame"], body.Problems);
    }

    [Fact]
    public async Task Visible_disease_signs_call_for_a_vet()
    {
        app.Claude.Assessment = StubClaude.Assess(
            anomalies: [new { type = "LUMPY_SKIN_NODULES", location = "neck and shoulder", confidence = 0.82, note = "firm raised nodules" }]);
        var alice = await OperatorAsync("Alice Vet");

        var (_, body) = await SubmitAsync(alice, CowPhotos.Face(11), CowPhotos.Flank(11), Metadata());

        var health = body.HealthScreening!;
        Assert.True(health.RequiresVetInspection);
        Assert.Equal(HealthRatings.Fair, health.OverallHealthRating); // the model said PRIME
        Assert.Equal("LUMPY_SKIN_NODULES", Assert.Single(health.DetectedAnomalies).Type);
        var audit = await app.DbAsync(db => db.HealthAudits.SingleAsync(a => a.CowId == body.CowId));
        Assert.Contains("LUMPY_SKIN_NODULES", audit.AnomalySummary);
    }

    [Fact]
    public async Task An_unconfident_breed_keeps_the_farmers_claim()
    {
        app.Claude.Assessment = StubClaude.Assess(breed: "Brahman", confidence: 0.62);
        var alice = await OperatorAsync("Alice Nguni");

        var (_, body) = await SubmitAsync(alice, CowPhotos.Face(12), CowPhotos.Flank(12), Metadata(claimedBreed: "Nguni"));

        Assert.Equal(("Brahman", false, "Nguni"), (body.Phenotype!.DetectedBreed, body.Phenotype.BreedConfirmed, body.Phenotype.RecordedBreed));
        var cow = await app.DbAsync(db => db.Livestock.SingleAsync(c => c.CowId == body.CowId));
        Assert.Equal(("Nguni", "CLAIMED"), (cow.Breed, cow.BreedSource));
    }

    [Fact]
    public async Task A_health_check_must_be_of_the_animal_named()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Check");
        var (_, registered) = await SubmitAsync(alice, CowPhotos.Face(13), CowPhotos.Flank(13), Metadata());

        var (wrongStatus, wrong) = await SubmitAsync(alice, CowPhotos.Face(14), CowPhotos.Flank(14), Metadata(existingCowId: registered.CowId));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, wrongStatus);
        Assert.Contains("doesn't match", Assert.Single(wrong.Problems!));

        var (rightStatus, right) = await SubmitAsync(alice, CowPhotos.Face(13, jitter: 5), CowPhotos.Flank(13), Metadata(existingCowId: registered.CowId));
        Assert.Equal(HttpStatusCode.OK, rightStatus);
        Assert.Equal((registered.CowId, EnrollmentStatuses.ExistingAnimal), (right.CowId, right.Biometrics!.EnrollmentStatus));
    }

    [Fact]
    public async Task Metadata_that_breaks_the_contract_is_refused()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Metadata");
        var bad = ContractJson.Write(new LivestockRegistrationMetadata(
            "ranch-41", "0x123", DateTimeOffset.UtcNow.AddDays(-45).ToUnixTimeSeconds(), new GpsTelemetry(-95, 26, 2.5), new ManualOverrides("Boran", "Heifer", 28)));

        var (status, body) = await SubmitAsync(alice, CowPhotos.Face(15), CowPhotos.Flank(15), bad);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(5, body.Problems!.Count); // ranch id, wallet, capture age, latitude, sex
    }

    [Fact]
    public async Task Photos_and_records_are_private_to_the_owner()
    {
        app.Claude.Assessment = StubClaude.Assess();
        var alice = await OperatorAsync("Alice Private Herd");
        var bob = await OperatorAsync("Bob Curious");
        var (_, body) = await SubmitAsync(alice, CowPhotos.Face(16), CowPhotos.Flank(16), Metadata());
        var faceRef = (await alice.User.GetAsync<List<CowSummaryDto>>("api/v1/livestock/cows")).Single().FaceImageRef;

        using var image = await bob.User.Http.GetAsync($"api/v1/livestock/images/{faceRef}");
        Assert.Equal(HttpStatusCode.NotFound, image.StatusCode);
        using var record = await bob.User.Http.GetAsync($"api/v1/livestock/cows/{Uri.EscapeDataString(body.CowId!)}");
        Assert.Equal(HttpStatusCode.NotFound, record.StatusCode);
        Assert.Empty(await bob.User.GetAsync<List<CowSummaryDto>>("api/v1/livestock/cows"));
    }

    [Fact]
    public async Task Device_keys_must_be_P256()
    {
        var alice = await app.CreateUserAsync("Alice Wrong Curve");
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        using var response = await alice.Http.PostAsJsonAsync("api/v1/livestock/operator-keys",
            new OperatorKeyRequest(Convert.ToBase64String(p384.ExportSubjectPublicKeyInfo()), "Old phone"), ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("P-256", await response.Content.ReadAsStringAsync());
    }

    // ---- The app's side: capture flow, offline queue, device key ----

    sealed record Phone(ClientHarness Client, SwitchableConnection Connection, LivestockApi Api, LivestockCaptureQueue Queue, LivestockSync Sync) : IAsyncDisposable
    {
        public RegisterCowViewModel NewForm() =>
            new(Queue, Sync, Client.Session, new FixedLocation(), new InMemorySettingsStore(), Client.Navigator, TimeProvider.System);

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }

    async Task<Phone> PhoneAsync(string name)
    {
        var farmer = await app.CreateUserAsync(name);
        var client = await ClientHarness.SignInAsync(app, farmer);
        var connection = new SwitchableConnection(app.Server.CreateHandler());
        var api = new LivestockApi(new HttpClient(new AuthHeaderHandler(client.Auth) { InnerHandler = connection }) { BaseAddress = app.Server.BaseAddress });
        var options = new ClientOptions
        {
            ApiBaseUrl = app.Server.BaseAddress,
            DataDirectory = Path.Combine(app.FilesDirectory, "phone-" + Guid.NewGuid().ToString("N")),
            DeviceName = "Test phone"
        };
        var queue = new LivestockCaptureQueue(options);
        var sync = new LivestockSync(queue, api, new OperatorSigner(api, new InMemoryOperatorKeyStore(), client.Session, options));
        return new Phone(client, connection, api, queue, sync);
    }

    static async Task FillAsync(RegisterCowViewModel form, int cow, IDictionary<string, object>? navigation = null)
    {
        await form.OnNavigatedToAsync(new Dictionary<string, object>(navigation ?? new Dictionary<string, object>()));
        form.AcceptPhoto(CowPhotos.Face(cow), 1440, 1080);
        form.AcceptPhoto(CowPhotos.Flank(cow), 1280, 720);
        form.RanchCode = "ZM-ST-041";
        form.ClaimedBreed = "Boran";
        form.Sex = CattleSex.Female;
        form.AgeMonths = "28";
    }

    [Fact]
    public async Task Registering_in_the_app_shows_the_analysis_straight_away()
    {
        app.Claude.Assessment = StubClaude.Assess();
        await using var phone = await PhoneAsync("Tendai App");
        var form = phone.NewForm();

        await form.OnNavigatedToAsync(new Dictionary<string, object>());
        form.AcceptPhoto(CowPhotos.Face(20, 1280, 720), 1280, 720);
        Assert.Contains("needs at least 1080", form.PhotoWarning);
        Assert.Equal(RegistrationStep.Face, form.Step);

        await FillAsync(form, 20);
        Assert.Equal(RegistrationStep.Details, form.Step);
        await form.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(RegistrationStep.Done, form.Step);
        var result = form.Result!;
        Assert.Equal(("Registered", true, false), (result.Headline, result.Succeeded, result.IsQueued));
        Assert.StartsWith("Boran (", result.BreedText);
        Assert.Equal("Body condition 5.8 / 9", result.ConditionText);
        Assert.Empty(await phone.Queue.ListAsync());

        var record = new CowDetailViewModel(phone.Api, phone.Client.Navigator);
        await record.OnNavigatedToAsync(new Dictionary<string, object> { [Routes.CowIdParameter] = result.CowId! });
        Assert.Equal(("Muzzle print enrolled", "ZM-ST-041"), (record.IdentityText, record.RanchText));
        Assert.Single(record.Audits);
        Assert.NotNull(record.FacePhoto);

        // A health check from the record goes to the same animal.
        await record.HealthCheckCommand.ExecuteAsync(null);
        Assert.Equal(Routes.RegisterCow, phone.Client.Navigator.Routes.Last());
        var check = phone.NewForm();
        await FillAsync(check, 20, new Dictionary<string, object> { [Routes.CowIdParameter] = result.CowId! });
        await check.SubmitCommand.ExecuteAsync(null);
        Assert.Equal(("Health check recorded", result.CowId), (check.Result!.Headline, check.Result.CowId));
    }

    [Fact]
    public async Task A_capture_taken_offline_uploads_when_the_connection_returns()
    {
        app.Claude.Assessment = StubClaude.Assess();
        await using var phone = await PhoneAsync("Chipo Offline");
        var form = phone.NewForm();
        await FillAsync(form, 21);

        phone.Connection.Offline = true;
        await form.SubmitCommand.ExecuteAsync(null);

        Assert.Equal(("Saved on your phone", true), (form.Result!.Headline, form.Result.IsQueued));
        var waiting = Assert.Single(await phone.Queue.ListAsync());
        Assert.Equal((CaptureStatuses.Pending, LivestockSync.OfflineMessage), (waiting.Status, waiting.LastError));
        Assert.True(File.Exists(waiting.FacePath));

        phone.Connection.Offline = false;
        Assert.Equal(1, await phone.Sync.UploadPendingAsync());
        Assert.Empty(await phone.Queue.ListAsync());
        Assert.False(File.Exists(waiting.FacePath));

        var herd = new HerdViewModel(phone.Api, phone.Queue, phone.Sync, phone.Client.Navigator, phone.Client.Ui);
        await herd.RefreshCommand.ExecuteAsync(null);
        var cow = phone.Client.Ui.Read(() => Assert.Single(herd.Cows));
        Assert.Equal("Boran · Female", cow.Title);
        Assert.StartsWith("ZM-", cow.ShortId);
    }

    [Fact]
    public async Task A_refused_capture_waits_for_the_farmer_instead_of_retrying()
    {
        app.Claude.Assessment = StubClaude.Assess(flankUsable: false, problems: ["Step back so the tail is in the photo"]);
        await using var phone = await PhoneAsync("Farai Refused");
        var form = phone.NewForm();
        await FillAsync(form, 22);

        await form.SubmitCommand.ExecuteAsync(null);

        Assert.Equal("Couldn't register", form.Result!.Headline);
        Assert.Equal(["Step back so the tail is in the photo"], form.Result.Findings);
        var refused = Assert.Single(await phone.Queue.ListAsync());
        Assert.Equal(CaptureStatuses.Rejected, refused.Status);
        Assert.Equal(0, await phone.Sync.UploadPendingAsync());
    }

    sealed class FixedLocation : ILocationProvider
    {
        public Task<GpsTelemetry?> GetLocationAsync(CancellationToken ct) => Task.FromResult<GpsTelemetry?>(new GpsTelemetry(-15.78341, 26.01258, 2.5));
    }
}
