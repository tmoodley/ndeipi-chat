using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Finance;

public sealed class FinanceOptions
{
    public const string Section = "Finance";

    /// <summary>Where uploaded application documents are kept. Never served directly: only through short-lived links.</summary>
    public string DocumentsPath { get; set; } = "App_Data/finance-documents";

    /// <summary>How long a document link works.</summary>
    public TimeSpan DocumentLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>What an application's live topic carries: enough for anyone watching to reload it.</summary>
public sealed record LoanChangedDto(Guid Id, string Status, string? Stage);

/// <summary>
/// The Absa gold mining loan (FinanceContract): applying, documents, and the committees an application
/// passes through. A committee sees and decides applications from its area at its stage; the most
/// specific committee covering the application's place handles it. Admins can act at any stage, so an
/// area without a committee yet isn't stuck.
/// </summary>
public sealed class FinanceService(
    ChatDbContext db,
    ChatNotifier notifier,
    IOptions<FinanceOptions> options,
    IDataProtectionProvider protection,
    IWebHostEnvironment environment,
    TimeProvider clock)
{
    const string SubmittedStage = "submitted";
    static readonly string[] Open = [LoanStatuses.InReview, LoanStatuses.Returned, LoanStatuses.Approved];

    DateTimeOffset Now => clock.GetUtcNow();

    ITimeLimitedDataProtector Links => protection.CreateProtector("finance-documents").ToTimeLimitedDataProtector();

    public static bool IsAdmin(User user) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(FinanceContract.AdminRole, StringComparer.OrdinalIgnoreCase);

    // ---- Who handles what ----

    static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a committee's area takes in a place: same province, and each part it names matches.</summary>
    static bool Covers(LoanCommittee c, string? province, string? constituency, string? ward, string? village) =>
        Same(c.Province, province)
        && (string.IsNullOrWhiteSpace(c.Constituency) || Same(c.Constituency, constituency))
        && (string.IsNullOrWhiteSpace(c.Ward) || Same(c.Ward, ward))
        && (string.IsNullOrWhiteSpace(c.Village) || Same(c.Village, village));

    static int Specificity(LoanCommittee c) =>
        (string.IsNullOrWhiteSpace(c.Constituency) ? 0 : 1) + (string.IsNullOrWhiteSpace(c.Ward) ? 0 : 2) + (string.IsNullOrWhiteSpace(c.Village) ? 0 : 4);

    /// <summary>The committee for each stage at a place (null where none is set up).</summary>
    async Task<Dictionary<string, LoanCommittee?>> CommitteesForAsync(string? province, string? constituency, string? ward, string? village, CancellationToken ct)
    {
        var inProvince = province is null
            ? []
            : await db.LoanCommittees.AsNoTracking().Include(c => c.Members).Where(c => c.Province == province.Trim()).ToListAsync(ct);
        return LoanStages.Order.ToDictionary(s => s, s => inProvince
            .Where(c => c.Stage == s && Covers(c, province, constituency, ward, village))
            .OrderByDescending(Specificity)
            .ThenBy(c => c.CreatedAt)
            .FirstOrDefault());
    }

    Task<Dictionary<string, LoanCommittee?>> CommitteesForAsync(LoanApplication a, CancellationToken ct) =>
        CommitteesForAsync(a.Province, a.Constituency, a.Ward, a.Village, ct);

    /// <summary>Who may decide at its current stage: an admin, or the committee for that stage there.</summary>
    static bool CanDecide(User user, LoanApplication a, Dictionary<string, LoanCommittee?> committees) =>
        a.Status is LoanStatuses.InReview or LoanStatuses.Approved && a.Stage is { } stage
        && (IsAdmin(user) || committees[stage]?.Members.Any(m => m.UserId == user.Id) == true);

    /// <summary>Who may see a submitted application: its applicant, admins, and its committees at any stage.</summary>
    static bool CanSee(User user, LoanApplication a, Dictionary<string, LoanCommittee?> committees) =>
        a.ApplicantId == user.Id
        || (a.Status != LoanStatuses.Draft && (IsAdmin(user) || committees.Values.Any(c => c?.Members.Any(m => m.UserId == user.Id) == true)));

    // ---- The applicant ----

    public async Task<FinanceMeDto> MeAsync(User user, CancellationToken ct)
    {
        var committees = await db.LoanCommittees.AsNoTracking().Include(c => c.Members)
            .Where(c => c.Members.Any(m => m.UserId == user.Id)).OrderBy(c => c.Province).ThenBy(c => c.Name).ToListAsync(ct);
        var toReview = (await QueueAsync(user, ct)).Count;
        return new FinanceMeDto(IsAdmin(user), await ToDtosAsync(committees, ct), toReview);
    }

    public async Task<IReadOnlyList<LoanSummaryDto>> MineAsync(User user, CancellationToken ct)
    {
        var mine = await db.LoanApplications.AsNoTracking().Where(a => a.ApplicantId == user.Id)
            .OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);
        return mine.Select(a => Summary(a, user.DisplayName)).ToList();
    }

    public async Task<LoanApplicationDto?> GetAsync(User user, Guid id, CancellationToken ct)
    {
        var a = await LoadAsync(id, ct);
        if (a is null)
            return null;
        var committees = await CommitteesForAsync(a, ct);
        return CanSee(user, a, committees) ? await ToDtoAsync(user, a, committees, ct) : null;
    }

    public async Task<LoanApplicationDto> CreateAsync(User user, SaveLoanApplicationRequest request, CancellationToken ct)
    {
        var a = new LoanApplication
        {
            Id = Guid.NewGuid(),
            Reference = NewReference(),
            ApplicantId = user.Id,
            Status = LoanStatuses.Draft,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        await ApplyAsync(a, request, ct);
        db.LoanApplications.Add(a);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                break;
            }
            catch (DbUpdateException) when (attempt < 3)
            {
                a.Reference = NewReference();
            }
        }
        return (await GetAsync(user, a.Id, ct))!;
    }

    public async Task<LoanApplicationDto?> SaveAsync(User user, Guid id, SaveLoanApplicationRequest request, CancellationToken ct)
    {
        var a = await db.LoanApplications.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id && x.ApplicantId == user.Id, ct);
        if (a is null)
            return null;
        EnsureEditable(a);
        await ApplyAsync(a, request, ct);
        a.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await GetAsync(user, id, ct);
    }

    /// <summary>A draft can be thrown away; once submitted it stays on record.</summary>
    public async Task<bool> DeleteDraftAsync(User user, Guid id, CancellationToken ct)
    {
        var a = await db.LoanApplications.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id && x.ApplicantId == user.Id, ct);
        if (a is null)
            return false;
        if (a.Status != LoanStatuses.Draft)
            throw new ChatRejectedException("Only a draft can be deleted.");
        foreach (var d in a.Documents)
            DeleteFile(d.StoredAs);
        db.LoanApplications.Remove(a);
        await db.SaveChangesAsync(ct);
        return true;
    }

    static void EnsureEditable(LoanApplication a)
    {
        if (a.Status is not (LoanStatuses.Draft or LoanStatuses.Returned))
            throw new ChatRejectedException("This application is with the committees and can't be changed now.");
    }

    async Task ApplyAsync(LoanApplication a, SaveLoanApplicationRequest r, CancellationToken ct)
    {
        var e = r.Eligibility;
        if (e.RegistrationBody is { } body && !RegistrationBodies.All.Any(b => b.Code == body))
            throw new ChatRejectedException("Choose where the group is registered.");
        if (e.LicenceType is { } licence && !LicenceTypes.All.Any(l => l.Code == licence))
            throw new ChatRejectedException("Choose the mining licence behind the proposal.");
        if (r.GroupType is { } type && !GroupTypes.All.Any(t => t.Code == type))
            throw new ChatRejectedException("Choose the kind of group.");
        if (Clean(r.Province, FinanceContract.MaxPlaceLength) is { } province && !FinanceContract.Provinces.Contains(province))
            throw new ChatRejectedException("Choose a province.");
        if (r.ClusterType is { } cluster && !ClusterTypes.All.Any(c => c.Code == cluster))
            throw new ChatRejectedException("Choose production or processing.");
        if (r.RunningMonths is { } months && !FinanceContract.RunningMonths.Contains(months))
            throw new ChatRejectedException($"Running costs can be financed for {FinanceContract.RunningMonths[0]} to {FinanceContract.RunningMonths[^1]} months.");

        a.ZambianOwned = e.ZambianOwned;
        a.RegistrationBody = e.RegistrationBody;
        a.LicenceType = e.LicenceType;
        a.HasBankAccount = e.HasBankAccount;
        a.GroupName = Clean(r.GroupName, FinanceContract.MaxNameLength);
        a.GroupType = r.GroupType;
        a.RegistrationNumber = Clean(r.RegistrationNumber, 60);
        a.Province = Clean(r.Province, FinanceContract.MaxPlaceLength);
        a.Constituency = Clean(r.Constituency, FinanceContract.MaxPlaceLength);
        a.Ward = Clean(r.Ward, FinanceContract.MaxPlaceLength);
        a.Village = Clean(r.Village, FinanceContract.MaxPlaceLength);
        a.ClusterType = r.ClusterType;
        a.RunningMonths = r.RunningMonths ?? a.RunningMonths;

        if (r.Items is { } items)
        {
            var ids = items.Select(i => i.EquipmentId).Distinct().ToList();
            var equipment = await db.LoanEquipment.AsNoTracking().Where(q => ids.Contains(q.Id) && q.Active).ToListAsync(ct);
            if (equipment.Count != ids.Count)
                throw new ChatRejectedException("Some of that equipment can't be financed any more. Please choose again.");
            if (a.ClusterType is { } c && equipment.Any(q => q.ClusterType != c))
                throw new ChatRejectedException("Choose equipment for the cluster type you picked.");
            // Kept items are updated in place: removing and re-adding the same key in one save isn't allowed.
            a.Items.RemoveAll(i => !ids.Contains(i.EquipmentId));
            foreach (var q in equipment.OrderBy(q => q.Order))
            {
                var buy = items.First(i => i.EquipmentId == q.Id).Buy;
                if (buy && q.BuyPrice is null && q.HirePerMonth is not null)
                    buy = false;
                var item = a.Items.FirstOrDefault(i => i.EquipmentId == q.Id);
                if (item is null)
                {
                    item = new LoanApplicationItem { ApplicationId = a.Id, EquipmentId = q.Id, Name = q.Name };
                    a.Items.Add(item);
                }
                item.Name = q.Name;
                item.Buy = buy;
                item.HirePerMonth = q.HirePerMonth;
                item.BuyPrice = q.BuyPrice;
                item.RunningPerMonth = q.RunningPerMonth;
            }
        }
        a.EstimateTotal = Estimate(a).Total;
    }

    static string? Clean(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed.Length > max ? trimmed[..max] : trimmed;
    }

    /// <summary>FR-TRM-01: equipment (hire for the months chosen, or buy) plus the running costs for those months.</summary>
    public static LoanEstimateDto Estimate(LoanApplication a)
    {
        var months = a.RunningMonths;
        var lines = new List<LoanEstimateLineDto>();
        foreach (var i in a.Items.OrderBy(i => i.Name, StringComparer.Ordinal))
            lines.Add(i.Buy
                ? new LoanEstimateLineDto($"{i.Name} (buy)", i.BuyPrice)
                : new LoanEstimateLineDto($"{i.Name} hire × {months} {(months == 1 ? "month" : "months")}", i.HirePerMonth * months));
        if (a.Items.Count > 0)
        {
            var running = a.Items.All(i => i.RunningPerMonth is not null) ? a.Items.Sum(i => i.RunningPerMonth!.Value) * months : (decimal?)null;
            lines.Add(new LoanEstimateLineDto($"Running costs × {months} {(months == 1 ? "month" : "months")}", running));
        }
        return new LoanEstimateDto(lines, lines.Sum(l => l.Amount ?? 0m), FinanceContract.Currency, lines.All(l => l.Amount is not null));
    }

    // ---- Documents (FR-DOC) ----

    public async Task<LoanApplicationDto?> UploadAsync(User user, Guid id, string kind, IFormFile file, CancellationToken ct)
    {
        var a = await db.LoanApplications.Include(x => x.Documents).FirstOrDefaultAsync(x => x.Id == id && x.ApplicantId == user.Id, ct);
        if (a is null)
            return null;
        EnsureEditable(a);
        if (!LoanDocumentKinds.All.Any(d => d.Kind == kind))
            throw new ChatRejectedException("That isn't one of the documents needed.");
        if (file.Length == 0)
            throw new ChatRejectedException("That file is empty.");
        if (file.Length > FinanceContract.MaxDocumentBytes)
            throw new ChatRejectedException($"Files can be up to {FinanceContract.MaxDocumentBytes / 1024 / 1024} MB. Try a smaller photo or PDF.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var (contentType, extension) = Sniff(bytes) ?? throw new ChatRejectedException("Upload a photo (JPEG, PNG or WebP) or a PDF.");

        var storedAs = Path.Combine(id.ToString("N"), $"{kind}-{Guid.NewGuid():N}{extension}");
        var path = Path.Combine(DocumentsRoot, storedAs);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);

        var name = Path.GetFileName(file.FileName ?? "");
        name = string.IsNullOrWhiteSpace(name) ? kind + extension : name.Length > 200 ? name[^200..] : name;
        var existing = a.Documents.FirstOrDefault(d => d.Kind == kind);
        if (existing is not null)
        {
            DeleteFile(existing.StoredAs);
            existing.FileName = name;
            existing.ContentType = contentType;
            existing.Size = bytes.Length;
            existing.StoredAs = storedAs;
            existing.UploadedAt = Now;
        }
        else
        {
            db.LoanDocuments.Add(new LoanDocument
            {
                Id = Guid.NewGuid(), ApplicationId = a.Id, Kind = kind, FileName = name, ContentType = contentType,
                Size = bytes.Length, StoredAs = storedAs, UploadedAt = Now
            });
        }
        a.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await GetAsync(user, id, ct);
    }

    /// <summary>What a file really is, from its first bytes, not its name.</summary>
    static (string ContentType, string Extension)? Sniff(byte[] b) => b switch
    {
        [0x25, 0x50, 0x44, 0x46, ..] => ("application/pdf", ".pdf"),
        [0xFF, 0xD8, 0xFF, ..] => ("image/jpeg", ".jpg"),
        [0x89, 0x50, 0x4E, 0x47, ..] => ("image/png", ".png"),
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ("image/webp", ".webp"),
        _ => null
    };

    string DocumentsRoot => Path.IsPathRooted(options.Value.DocumentsPath)
        ? options.Value.DocumentsPath
        : Path.Combine(environment.ContentRootPath, options.Value.DocumentsPath);

    void DeleteFile(string storedAs)
    {
        try
        {
            File.Delete(Path.Combine(DocumentsRoot, storedAs));
        }
        catch (IOException)
        {
        }
    }

    /// <summary>A link that opens the document for a few minutes, for anyone who may see the application.</summary>
    public async Task<LoanDocumentLinkDto?> DocumentLinkAsync(User user, Guid id, string kind, Uri site, CancellationToken ct)
    {
        if (await GetAsync(user, id, ct) is null)
            return null;
        var doc = await db.LoanDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.ApplicationId == id && d.Kind == kind, ct);
        if (doc is null)
            return null;
        var token = Links.Protect(doc.Id.ToString("N"), options.Value.DocumentLinkLifetime);
        return new LoanDocumentLinkDto(new Uri(site, $"{FinanceContract.BasePath}/files/{Uri.EscapeDataString(token)}").ToString());
    }

    /// <summary>The file behind a document link, or null if the link is wrong or has run out.</summary>
    public async Task<(string Path, string ContentType, string FileName)?> OpenLinkAsync(string token, CancellationToken ct)
    {
        Guid id;
        try
        {
            id = Guid.ParseExact(Links.Unprotect(token), "N");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
        var doc = await db.LoanDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (doc is null)
            return null;
        var path = Path.Combine(DocumentsRoot, doc.StoredAs);
        return File.Exists(path) ? (path, doc.ContentType, doc.FileName) : null;
    }

    // ---- Submitting (FR-SUB) ----

    public async Task<LoanApplicationDto?> SubmitAsync(User user, Guid id, SubmitLoanRequest request, CancellationToken ct)
    {
        var a = await db.LoanApplications.Include(x => x.Items).Include(x => x.Documents)
            .FirstOrDefaultAsync(x => x.Id == id && x.ApplicantId == user.Id, ct);
        if (a is null)
            return null;
        EnsureEditable(a);

        var eligibility = new EligibilityDto(a.ZambianOwned, a.RegistrationBody, a.LicenceType, a.HasBankAccount);
        if (eligibility.Problems() is [var problem, ..])
            throw new ChatRejectedException(problem);
        if (a.GroupName is null || a.GroupType is null || a.RegistrationNumber is null)
            throw new ChatRejectedException("Fill in the group's name, type and registration number.");
        if (a.Province is null || a.Constituency is null || a.Ward is null || a.Village is null)
            throw new ChatRejectedException("Fill in where the group is: province, constituency, ward and village.");
        if (a.ClusterType is null || a.Items.Count == 0)
            throw new ChatRejectedException("Choose the equipment the loan will finance.");
        if (LoanDocumentKinds.All.FirstOrDefault(d => a.Documents.All(x => x.Kind != d.Kind)) is { Kind: not null } missing)
            throw new ChatRejectedException($"Upload the {missing.Label.ToLowerInvariant()}.");
        if (!request.Declared)
            throw new ChatRejectedException("Confirm the group is fully Zambian-owned and the information is true and complete.");

        var number = a.RegistrationNumber;
        if (await db.LoanApplications.AnyAsync(x => x.Id != a.Id && x.RegistrationNumber == number && Open.Contains(x.Status), ct))
            throw new ChatRejectedException("This group already has an application in progress.");

        // A returned application goes back to the stage that returned it; a new one starts at the village.
        var resubmitted = a.Status == LoanStatuses.Returned;
        a.Stage = resubmitted ? a.Stage : LoanStages.Village;
        a.Status = LoanStatuses.InReview;
        a.Declared = true;
        a.SubmittedAt ??= Now;
        a.UpdatedAt = Now;
        a.EstimateTotal = Estimate(a).Total;
        db.LoanDecisions.Add(new LoanDecision
        {
            Id = Guid.NewGuid(), ApplicationId = a.Id, Stage = SubmittedStage, Decision = resubmitted ? "resubmitted" : "submitted",
            DeciderId = user.Id, CreatedAt = Now
        });
        await db.SaveChangesAsync(ct);
        await PublishAsync(a);
        return await GetAsync(user, id, ct);
    }

    // ---- Committees (FR-WF) and Absa (FR-DSB) ----

    /// <summary>Applications waiting on the committees the user sits on (all of them, for an admin).</summary>
    public async Task<IReadOnlyList<LoanSummaryDto>> QueueAsync(User user, CancellationToken ct)
    {
        var admin = IsAdmin(user);
        var mine = admin
            ? []
            : await db.LoanCommittees.AsNoTracking().Where(c => c.Members.Any(m => m.UserId == user.Id)).Select(c => new { c.Stage, c.Province }).ToListAsync(ct);
        if (!admin && mine.Count == 0)
            return [];

        var waiting = await db.LoanApplications.AsNoTracking()
            .Where(a => (a.Status == LoanStatuses.InReview || a.Status == LoanStatuses.Approved) && a.Province != null)
            .OrderBy(a => a.UpdatedAt)
            .ToListAsync(ct);
        if (!admin)
            waiting = waiting.Where(a => mine.Any(c => c.Stage == a.Stage && Same(c.Province, a.Province))).ToList();

        var result = new List<LoanApplication>();
        foreach (var a in waiting)
            if (CanDecide(user, a, await CommitteesForAsync(a, ct)))
                result.Add(a);
        var names = await NamesAsync(result.Select(a => a.ApplicantId), ct);
        return result.Select(a => Summary(a, names.GetValueOrDefault(a.ApplicantId, ""))).ToList();
    }

    public async Task<LoanApplicationDto?> DecideAsync(User user, Guid id, LoanDecisionRequest request, CancellationToken ct)
    {
        var a = await db.LoanApplications.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null)
            return null;
        var committees = await CommitteesForAsync(a, ct);
        if (!CanSee(user, a, committees))
            return null;
        if (!CanDecide(user, a, committees))
            throw new ChatRejectedException("This application isn't waiting on you.");

        var stage = a.Stage!;
        var comment = Clean(request.Comment, FinanceContract.MaxCommentLength);
        switch (request.Decision)
        {
            case LoanDecisions.Approve when stage == LoanStages.Absa:
                a.CollectionReference = Clean(request.Reference, 60)
                    ?? throw new ChatRejectedException("Enter Absa's reference for the loan collected.");
                a.Status = LoanStatuses.Collected;
                break;
            case LoanDecisions.Approve:
                a.Stage = LoanStages.Next(stage);
                a.Status = a.Stage == LoanStages.Absa ? LoanStatuses.Approved : LoanStatuses.InReview;
                break;
            case LoanDecisions.Return when stage != LoanStages.Absa:
                a.Status = LoanStatuses.Returned;
                if (comment is null)
                    throw new ChatRejectedException("Say what the group needs to change.");
                break;
            case LoanDecisions.Decline:
                a.Status = LoanStatuses.Declined;
                if (comment is null)
                    throw new ChatRejectedException("Give the reason it's declined.");
                break;
            default:
                throw new ChatRejectedException("Approve, send back or decline.");
        }
        a.UpdatedAt = Now;
        db.LoanDecisions.Add(new LoanDecision
        {
            Id = Guid.NewGuid(), ApplicationId = a.Id, Stage = stage, Decision = request.Decision, DeciderId = user.Id,
            CommitteeId = committees[stage]?.Id, Comment = comment, CreatedAt = Now
        });
        await db.SaveChangesAsync(ct);
        await PublishAsync(a);
        return await GetAsync(user, id, ct);
    }

    /// <summary>FR-GRP-04: who an application from this place would go to.</summary>
    public async Task<LoanRouteDto> RouteAsync(string? province, string? constituency, string? ward, string? village, CancellationToken ct)
    {
        var committees = await CommitteesForAsync(province, constituency, ward, village, ct);
        return new LoanRouteDto(LoanStages.Order
            .Select(s => new LoanStageDto(s, LoanStages.Label(s), LoanStages.Hint(s), "waiting", committees[s]?.Name, null, null, null))
            .ToList());
    }

    Task PublishAsync(LoanApplication a) =>
        notifier.PublishAsync(FinanceContract.ApplicationTopic(a.Id), new LoanChangedDto(a.Id, a.Status, a.Stage));

    /// <summary>For the topic policy: whether the user may follow this application.</summary>
    public async Task<bool> CanFollowAsync(User user, Guid id, CancellationToken ct)
    {
        var a = await db.LoanApplications.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        return a is not null && CanSee(user, a, await CommitteesForAsync(a, ct));
    }

    // ---- Admin: committees, equipment, constituencies ----

    static void EnsureAdmin(User user)
    {
        if (!IsAdmin(user))
            throw new ChatRejectedException("Only Finance admins can do that.");
    }

    public async Task<IReadOnlyList<LoanCommitteeDto>> CommitteesAsync(User user, CancellationToken ct)
    {
        EnsureAdmin(user);
        var all = await db.LoanCommittees.AsNoTracking().Include(c => c.Members).ToListAsync(ct);
        return await ToDtosAsync(all.OrderBy(c => c.Province).ThenBy(c => LoanStages.Position(c.Stage)).ThenBy(c => c.Name).ToList(), ct);
    }

    public async Task<LoanCommitteeDto?> SaveCommitteeAsync(User user, Guid? id, SaveCommitteeRequest r, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (!LoanStages.Order.Contains(r.Stage))
            throw new ChatRejectedException("Choose the stage this committee handles.");
        var name = Clean(r.Name, FinanceContract.MaxNameLength) ?? throw new ChatRejectedException("Give the committee a name.");
        var province = Clean(r.Province, FinanceContract.MaxPlaceLength);
        if (province is null || !FinanceContract.Provinces.Contains(province))
            throw new ChatRejectedException("Choose the committee's province.");
        var memberIds = r.MemberIds.Distinct().ToList();
        if (await db.Users.CountAsync(u => memberIds.Contains(u.Id), ct) != memberIds.Count)
            throw new ChatRejectedException("Some of those members aren't on Ndeipi.");

        LoanCommittee? c;
        if (id is { } existing)
        {
            c = await db.LoanCommittees.Include(x => x.Members).FirstOrDefaultAsync(x => x.Id == existing, ct);
            if (c is null)
                return null;
        }
        else
        {
            c = new LoanCommittee { Id = Guid.NewGuid(), Stage = r.Stage, Name = name, Province = province, CreatedAt = Now };
            db.LoanCommittees.Add(c);
        }
        c.Stage = r.Stage;
        c.Name = name;
        c.Province = province;
        c.Constituency = Clean(r.Constituency, FinanceContract.MaxPlaceLength);
        c.Ward = Clean(r.Ward, FinanceContract.MaxPlaceLength);
        c.Village = Clean(r.Village, FinanceContract.MaxPlaceLength);
        c.Members.RemoveAll(m => !memberIds.Contains(m.UserId));
        foreach (var userId in memberIds.Where(u => c.Members.All(m => m.UserId != u)))
            c.Members.Add(new LoanCommitteeMember { CommitteeId = c.Id, UserId = userId });
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([c], ct))[0];
    }

    public async Task<bool> DeleteCommitteeAsync(User user, Guid id, CancellationToken ct)
    {
        EnsureAdmin(user);
        return await db.LoanCommittees.Where(c => c.Id == id).ExecuteDeleteAsync(ct) > 0;
    }

    /// <summary>The equipment catalogue: what applicants can pick (active), or everything for an admin.</summary>
    public async Task<IReadOnlyList<LoanEquipmentDto>> EquipmentAsync(User user, bool all, CancellationToken ct)
    {
        if (all)
            EnsureAdmin(user);
        var items = await db.LoanEquipment.AsNoTracking().Where(q => all || q.Active).OrderBy(q => q.ClusterType).ThenBy(q => q.Order).ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<LoanEquipmentDto?> SaveEquipmentAsync(User user, Guid? id, SaveEquipmentRequest r, CancellationToken ct)
    {
        EnsureAdmin(user);
        var name = Clean(r.Name, FinanceContract.MaxNameLength) ?? throw new ChatRejectedException("Name the equipment.");
        if (!ClusterTypes.All.Any(c => c.Code == r.ClusterType))
            throw new ChatRejectedException("Choose production or processing.");
        if (new[] { r.HirePerMonth, r.BuyPrice, r.RunningPerMonth }.Any(p => p is < 0))
            throw new ChatRejectedException("Prices can't be negative.");

        LoanEquipment? q;
        if (id is { } existing)
        {
            q = await db.LoanEquipment.FirstOrDefaultAsync(x => x.Id == existing, ct);
            if (q is null)
                return null;
        }
        else
        {
            q = new LoanEquipment { Id = Guid.NewGuid(), Name = name, ClusterType = r.ClusterType };
            db.LoanEquipment.Add(q);
        }
        q.Name = name;
        q.Description = Clean(r.Description, 300) ?? "";
        q.ClusterType = r.ClusterType;
        q.Mtp = Clean(r.Mtp, 24);
        q.HirePerMonth = r.HirePerMonth;
        q.BuyPrice = r.BuyPrice;
        q.RunningPerMonth = r.RunningPerMonth;
        q.Active = r.Active;
        q.Order = r.Order;
        await db.SaveChangesAsync(ct);
        return ToDto(q);
    }

    public async Task<IReadOnlyList<string>> ConstituenciesAsync(string province, CancellationToken ct) =>
        await db.FinanceConstituencies.AsNoTracking().Where(c => c.Province == province).OrderBy(c => c.Name).Select(c => c.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<string>> SaveConstituenciesAsync(User user, SaveConstituenciesRequest r, CancellationToken ct)
    {
        EnsureAdmin(user);
        if (!FinanceContract.Provinces.Contains(r.Province))
            throw new ChatRejectedException("Choose a province.");
        var names = r.Names.Select(n => Clean(n, FinanceContract.MaxPlaceLength)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        await db.FinanceConstituencies.Where(c => c.Province == r.Province).ExecuteDeleteAsync(ct);
        db.FinanceConstituencies.AddRange(names.Select(n => new FinanceConstituency { Id = Guid.NewGuid(), Province = r.Province, Name = n }));
        await db.SaveChangesAsync(ct);
        return names;
    }

    // ---- Shapes ----

    Task<LoanApplication?> LoadAsync(Guid id, CancellationToken ct) =>
        db.LoanApplications.AsNoTracking().Include(a => a.Items).Include(a => a.Documents).Include(a => a.Decisions)
            .AsSplitQuery().FirstOrDefaultAsync(a => a.Id == id, ct);

    async Task<LoanApplicationDto> ToDtoAsync(User viewer, LoanApplication a, Dictionary<string, LoanCommittee?> committees, CancellationToken ct)
    {
        var decisions = a.Decisions.OrderBy(d => d.CreatedAt).ToList();
        var people = await db.Users.AsNoTracking().Where(u => u.Id == a.ApplicantId || decisions.Select(d => d.DeciderId).Contains(u.Id)).ToListAsync(ct);
        string? Name(Guid id) => people.FirstOrDefault(u => u.Id == id)?.DisplayName;

        var current = LoanStages.Position(a.Stage);
        var stages = LoanStages.Order.Select((s, i) =>
        {
            var last = decisions.LastOrDefault(d => d.Stage == s);
            var state = a.Status switch
            {
                LoanStatuses.Draft => "waiting",
                LoanStatuses.Collected => "done",
                _ when i < current => "done",
                _ when i > current => "waiting",
                LoanStatuses.Returned => "returned",
                LoanStatuses.Declined => "declined",
                _ => "current"
            };
            var shown = state is "done" or "returned" or "declined" ? last : null;
            return new LoanStageDto(s, LoanStages.Label(s), LoanStages.Hint(s), state, committees[s]?.Name,
                shown is null ? null : Name(shown.DeciderId), shown?.Comment, shown?.CreatedAt);
        }).ToList();

        var applicant = people.First(u => u.Id == a.ApplicantId);
        var documents = LoanDocumentKinds.All.Select(k =>
        {
            var d = a.Documents.FirstOrDefault(x => x.Kind == k.Kind);
            return new LoanDocumentDto(k.Kind, k.Label, k.Hint, d?.FileName, d?.Size, d?.UploadedAt);
        }).ToList();

        return new LoanApplicationDto(
            a.Id, a.Reference, a.Status, a.Stage,
            new EligibilityDto(a.ZambianOwned, a.RegistrationBody, a.LicenceType, a.HasBankAccount),
            a.GroupName, a.GroupType, a.RegistrationNumber, a.Province, a.Constituency, a.Ward, a.Village, a.ClusterType,
            a.Items.OrderBy(i => i.Name, StringComparer.Ordinal).Select(i => new LoanItemDto(i.EquipmentId, i.Name, i.Buy)).ToList(),
            a.RunningMonths, Estimate(a), documents, stages,
            ChatMapper.ToDto(applicant),
            a.ApplicantId == viewer.Id,
            a.ApplicantId == viewer.Id && a.Status is LoanStatuses.Draft or LoanStatuses.Returned,
            CanDecide(viewer, a, committees),
            a.Status == LoanStatuses.Returned ? decisions.LastOrDefault(d => d.Decision == LoanDecisions.Return)?.Comment : null,
            a.CollectionReference,
            a.CreatedAt, a.SubmittedAt);
    }

    static LoanSummaryDto Summary(LoanApplication a, string applicant) =>
        new(a.Id, a.Reference, a.Status, a.Stage, a.GroupName,
            string.Join(", ", new[] { a.Village, a.Ward, a.Province }.Where(p => !string.IsNullOrEmpty(p))) is { Length: > 0 } place ? place : null,
            a.EstimateTotal, applicant, a.UpdatedAt);

    async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    async Task<IReadOnlyList<LoanCommitteeDto>> ToDtosAsync(IReadOnlyList<LoanCommittee> committees, CancellationToken ct)
    {
        var ids = committees.SelectMany(c => c.Members.Select(m => m.UserId)).Distinct().ToList();
        var users = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        return committees.Select(c => new LoanCommitteeDto(c.Id, c.Stage, c.Name, c.Province, c.Constituency, c.Ward, c.Village,
            users.Where(u => c.Members.Any(m => m.UserId == u.Id)).OrderBy(u => u.DisplayName).Select(ChatMapper.ToDto).ToList())).ToList();
    }

    static LoanEquipmentDto ToDto(LoanEquipment q) =>
        new(q.Id, q.Name, q.Description, q.ClusterType, q.Mtp, q.HirePerMonth, q.BuyPrice, q.RunningPerMonth, q.Active, q.Order);

    /// <summary>"GL-" and six letters and digits that can't be mistaken for each other.</summary>
    static string NewReference()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return "GL-" + new string(RandomNumberGenerator.GetItems<char>(alphabet, 6));
    }
}

/// <summary>An application's live updates: its applicant, admins, and the committees that handle it.</summary>
public sealed class FinanceTopicPolicy(FinanceService finance) : ITopicPolicy
{
    public string Prefix => "finance";

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Guid.TryParseExact(key, "N", out var id) && await finance.CanFollowAsync(user, id, ct);
}
