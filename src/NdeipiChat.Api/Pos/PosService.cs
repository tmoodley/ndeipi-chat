using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Shamwaris;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Pos;

/// <summary>A till request, once its session checks out: who's at it, where, for which merchant.</summary>
public sealed record Till(PosTillSession Session, PosStaff Staff, PosStore Store, PosMerchant Merchant, string StaffName);

/// <summary>
/// The POS sub-app (SRS "Ndeipi Super App — Point of Sale (POS) Module"): merchants, stores and
/// staff with roles (§3); PIN sign-in at the till (FR-AUTH-01); shifts and X/Z reports
/// (FR-AUTH-03); the catalogue with variants and modifiers (FR-CAT); pricing with tax and
/// discounts (FR-CAT-03/04); idempotent split-tender checkout (FR-PAY-01/02); offline sales
/// uploaded later (FR-OFF); stock per store (FR-INV); and a hash-chained audit trail (NFR-SEC-03).
/// Card numbers never come here: card tenders carry only the terminal's reference (NFR-SEC-02).
/// </summary>
public sealed class PosService(
    ChatDbContext db,
    ConversationService conversations,
    MessageService messages,
    IMemoryCache cache,
    TimeProvider clock)
{
    /// <summary>How long a till session lasts at most; the till itself locks far sooner (FR-AUTH-02).</summary>
    public static readonly TimeSpan TillSessionLifetime = TimeSpan.FromHours(12);

    const int PinIterations = 100_000;
    const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    DateTimeOffset Now => clock.GetUtcNow();

    // ---- Merchants and stores ----

    public async Task<List<PosMerchantDto>> MerchantsAsync(User me, CancellationToken ct)
    {
        var mine = await db.PosStaff.AsNoTracking().Where(s => s.UserId == me.Id).ToListAsync(ct);
        var result = new List<PosMerchantDto>();
        foreach (var staff in mine)
            if (await MerchantDtoAsync(staff, ct) is { } dto)
                result.Add(dto);
        return result.OrderBy(m => m.Name).ToList();
    }

    public async Task<PosMerchantDto?> MerchantAsync(User me, Guid merchantId, CancellationToken ct) =>
        await StaffOfAsync(me, merchantId, ct) is { } staff ? await MerchantDtoAsync(staff, ct) : null;

    async Task<PosMerchantDto?> MerchantDtoAsync(PosStaff staff, CancellationToken ct)
    {
        var merchant = await db.PosMerchants.AsNoTracking().FirstOrDefaultAsync(m => m.Id == staff.MerchantId, ct);
        if (merchant is null)
            return null;
        var stores = await db.PosStores.AsNoTracking().Where(s => s.MerchantId == merchant.Id && (staff.StoreId == null || s.Id == staff.StoreId))
            .OrderBy(s => s.Name).ToListAsync(ct);
        return new PosMerchantDto(merchant.Id, merchant.Name, merchant.Currency, merchant.TaxInclusive, merchant.DiscountLimitPercent, merchant.LockSeconds,
            stores.Select(ToDto).ToList(), staff.Role, staff.Id, staff.PinHash is not null);
    }

    /// <summary>A new business, its first store, and its owner as Merchant Admin.</summary>
    public async Task<PosMerchantDto> CreateMerchantAsync(User me, CreateMerchantRequest request, CancellationToken ct)
    {
        var merchant = new PosMerchant
        {
            Id = Guid.NewGuid(),
            OwnerId = me.Id,
            Name = Required(request.Name, 120, "Business name"),
            Currency = Currency(request.Currency),
            TaxInclusive = request.TaxInclusive,
            DiscountLimitPercent = 10,
            LockSeconds = PosContract.DefaultLockSeconds,
            CreatedAt = Now
        };
        var store = new PosStore
        {
            Id = Guid.NewGuid(),
            MerchantId = merchant.Id,
            Name = Required(request.StoreName, 120, "Store name"),
            TaxRatePercent = Rate(request.TaxRatePercent)
        };
        var owner = new PosStaff { Id = Guid.NewGuid(), MerchantId = merchant.Id, UserId = me.Id, Role = PosRoles.Admin, AddedAt = Now };
        db.PosMerchants.Add(merchant);
        db.PosStores.Add(store);
        db.PosStaff.Add(owner);
        await db.SaveChangesAsync(ct);
        await AuditAsync(merchant.Id, null, owner.Id, me.DisplayName, "web", "merchant.create", $"{merchant.Name}, store {store.Name}", ct);
        return (await MerchantDtoAsync(owner, ct))!;
    }

    public async Task<PosMerchantDto?> SaveMerchantAsync(User me, Guid merchantId, SaveMerchantRequest request, CancellationToken ct)
    {
        var staff = await RequireRoleAsync(me, merchantId, PosRoles.Admin, ct);
        var merchant = await db.PosMerchants.FirstAsync(m => m.Id == merchantId, ct);
        merchant.Name = Required(request.Name, 120, "Business name");
        merchant.Currency = Currency(request.Currency);
        merchant.TaxInclusive = request.TaxInclusive;
        merchant.DiscountLimitPercent = request.DiscountLimitPercent is >= 0 and <= 100
            ? request.DiscountLimitPercent
            : throw new ChatRejectedException("The discount limit is a percentage from 0 to 100.");
        merchant.LockSeconds = request.LockSeconds is >= 30 and <= 3600
            ? request.LockSeconds
            : throw new ChatRejectedException("The till locks after 30 seconds to an hour.");
        await db.SaveChangesAsync(ct);
        await AuditAsync(merchantId, null, staff.Id, me.DisplayName, "web", "merchant.settings",
            $"tax {(merchant.TaxInclusive ? "inclusive" : "exclusive")}, discount limit {merchant.DiscountLimitPercent}%, lock {merchant.LockSeconds}s", ct);
        return await MerchantDtoAsync(staff, ct);
    }

    public async Task<PosStoreDto> AddStoreAsync(User me, Guid merchantId, SaveStoreRequest request, CancellationToken ct)
    {
        var staff = await RequireRoleAsync(me, merchantId, PosRoles.Admin, ct);
        var store = new PosStore { Id = Guid.NewGuid(), MerchantId = merchantId, Name = Required(request.Name, 120, "Store name"), Address = Optional(request.Address, 300), TaxRatePercent = Rate(request.TaxRatePercent) };
        db.PosStores.Add(store);
        await db.SaveChangesAsync(ct);
        await AuditAsync(merchantId, store.Id, staff.Id, me.DisplayName, "web", "store.add", store.Name, ct);
        return ToDto(store);
    }

    public async Task<PosStoreDto?> SaveStoreAsync(User me, Guid storeId, SaveStoreRequest request, CancellationToken ct)
    {
        var store = await db.PosStores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null)
            return null;
        var staff = await RequireRoleAsync(me, store.MerchantId, PosRoles.Admin, ct);
        (store.Name, store.Address, store.TaxRatePercent) = (Required(request.Name, 120, "Store name"), Optional(request.Address, 300), Rate(request.TaxRatePercent));
        await db.SaveChangesAsync(ct);
        await AuditAsync(store.MerchantId, store.Id, staff.Id, me.DisplayName, "web", "store.save", $"{store.Name}, tax {store.TaxRatePercent}%", ct);
        return ToDto(store);
    }

    // ---- Staff ----

    public async Task<List<PosStaffDto>?> StaffAsync(User me, Guid merchantId, CancellationToken ct)
    {
        var staff = await StaffOfAsync(me, merchantId, ct);
        if (staff is null || !(PosRoles.CanManage(staff.Role) || PosRoles.CanAudit(staff.Role)))
            return null;
        var all = await db.PosStaff.AsNoTracking().Include(s => s.User).Where(s => s.MerchantId == merchantId).OrderBy(s => s.AddedAt).ToListAsync(ct);
        return all.Select(ToDto).ToList();
    }

    public async Task<PosStaffDto> AddStaffAsync(User me, Guid merchantId, AddStaffRequest request, CancellationToken ct)
    {
        var admin = await RequireRoleAsync(me, merchantId, PosRoles.Admin, ct);
        var role = Role(request.Role);
        await CheckStoreAsync(merchantId, request.StoreId, ct);
        var contact = ShamwariContact.Normalize(request.Email);
        var user = ShamwariContact.IsEmail(contact)
            ? await db.Users.FirstOrDefaultAsync(u => u.EmailVerified && u.Email == contact, ct)
            : await db.Users.FirstOrDefaultAsync(u => u.Phone == contact, ct);
        if (user is null)
            throw new ChatRejectedException("Nobody on Ndeipi has that email or phone yet. Ask them to sign up first.");
        if (await db.PosStaff.AnyAsync(s => s.MerchantId == merchantId && s.UserId == user.Id, ct))
            throw new ChatRejectedException($"{user.DisplayName} already works here.");

        var staff = new PosStaff { Id = Guid.NewGuid(), MerchantId = merchantId, UserId = user.Id, User = user, Role = role, StoreId = request.StoreId, AddedAt = Now };
        db.PosStaff.Add(staff);
        await db.SaveChangesAsync(ct);
        await AuditAsync(merchantId, request.StoreId, admin.Id, me.DisplayName, "web", "staff.add", $"{user.DisplayName} as {PosRoles.Label(role)}", ct);
        return ToDto(staff);
    }

    public async Task<PosStaffDto?> SaveStaffAsync(User me, Guid staffId, SaveStaffRequest request, CancellationToken ct)
    {
        var staff = await db.PosStaff.Include(s => s.User).FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null)
            return null;
        var admin = await RequireRoleAsync(me, staff.MerchantId, PosRoles.Admin, ct);
        var role = Role(request.Role);
        await CheckStoreAsync(staff.MerchantId, request.StoreId, ct);
        if (await IsOwnerAsync(staff) && role != PosRoles.Admin)
            throw new ChatRejectedException("The business's owner stays a Merchant admin.");
        (staff.Role, staff.StoreId) = (role, request.StoreId);
        await db.SaveChangesAsync(ct);
        await AuditAsync(staff.MerchantId, request.StoreId, admin.Id, me.DisplayName, "web", "staff.role", $"{staff.User.DisplayName} now {PosRoles.Label(role)}", ct);
        return ToDto(staff);
    }

    public async Task<bool> RemoveStaffAsync(User me, Guid staffId, CancellationToken ct)
    {
        var staff = await db.PosStaff.Include(s => s.User).FirstOrDefaultAsync(s => s.Id == staffId, ct);
        if (staff is null)
            return false;
        var admin = await RequireRoleAsync(me, staff.MerchantId, PosRoles.Admin, ct);
        if (await IsOwnerAsync(staff))
            throw new ChatRejectedException("The business's owner can't be removed.");
        db.PosStaff.Remove(staff);
        await db.PosTillSessions.Where(t => t.StaffId == staffId).ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, Now), ct);
        await db.SaveChangesAsync(ct);
        await AuditAsync(staff.MerchantId, staff.StoreId, admin.Id, me.DisplayName, "web", "staff.remove", staff.User.DisplayName, ct);
        return true;
    }

    /// <summary>Sets your own till PIN. PINs are unique within a business: the PIN alone says who's at the till.</summary>
    public async Task SetPinAsync(User me, Guid merchantId, string? pin, CancellationToken ct)
    {
        var staff = await db.PosStaff.FirstOrDefaultAsync(s => s.MerchantId == merchantId && s.UserId == me.Id, ct)
            ?? throw new ChatRejectedException("You don't work here.");
        if (pin is null || pin.Length is < PosContract.MinPinLength or > PosContract.MaxPinLength || !pin.All(char.IsAsciiDigit))
            throw new ChatRejectedException($"A PIN is {PosContract.MinPinLength} to {PosContract.MaxPinLength} digits.");
        if (pin.Distinct().Count() == 1 || "0123456789".Contains(pin) || "9876543210".Contains(pin))
            throw new ChatRejectedException("Pick a PIN that's harder to guess than a run or a repeat.");
        var others = await db.PosStaff.Where(s => s.MerchantId == merchantId && s.Id != staff.Id && s.PinHash != null).ToListAsync(ct);
        if (others.Any(o => Matches(o, pin)))
            throw new ChatRejectedException("Someone else here uses that PIN. Pick another.");

        staff.PinSalt = RandomNumberGenerator.GetBytes(16);
        staff.PinHash = HashPin(pin, staff.PinSalt);
        await db.SaveChangesAsync(ct);
        await AuditAsync(merchantId, staff.StoreId, staff.Id, me.DisplayName, "web", "staff.pin", "PIN set", ct);
    }

    // ---- The till: PIN sign-in (FR-AUTH-01/02) ----

    /// <summary>
    /// A staff PIN unlocks the till on a device signed in to a Ndeipi account that works here.
    /// Five wrong PINs at a store pause it for five minutes.
    /// </summary>
    public async Task<(TillSessionDto Session, string Token)> UnlockAsync(User device, Guid storeId, UnlockTillRequest request, CancellationToken ct)
    {
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct)
            ?? throw new ChatRejectedException("That store doesn't exist.");
        if (await StaffOfAsync(device, store.MerchantId, ct) is not { } deviceStaff || !CanWorkAt(deviceStaff, storeId))
            throw new ChatRejectedException("This device isn't signed in to an account that works at this store.");

        var lockKey = $"pos-pin:{storeId}";
        var attempts = cache.Get<PinAttempts>(lockKey);
        if (attempts is { Count: >= PosContract.MaxPinAttempts } && attempts.Until > Now)
            throw new ChatRejectedException($"Too many wrong PINs. Try again in {Math.Max(1, (int)Math.Ceiling((attempts.Until - Now).TotalMinutes))} minutes.");

        var candidates = await db.PosStaff.Include(s => s.User)
            .Where(s => s.MerchantId == store.MerchantId && s.PinHash != null && (s.StoreId == null || s.StoreId == storeId))
            .ToListAsync(ct);
        var staff = candidates.FirstOrDefault(s => Matches(s, request.Pin ?? ""));
        if (staff is null || !PosRoles.CanSell(staff.Role))
        {
            var count = (attempts is { } a && a.Until > Now ? a.Count : 0) + 1;
            cache.Set(lockKey, new PinAttempts(count, Now.AddMinutes(PosContract.PinLockoutMinutes)), TimeSpan.FromMinutes(PosContract.PinLockoutMinutes));
            throw new ChatRejectedException(staff is null ? "That PIN isn't right." : "Auditors can't use the till.");
        }
        cache.Remove(lockKey);

        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var session = new PosTillSession
        {
            Id = Guid.NewGuid(),
            TokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token)),
            StoreId = storeId,
            StaffId = staff.Id,
            DeviceUserId = device.Id,
            Terminal = Optional(request.Terminal, 120) ?? "Till",
            CreatedAt = Now,
            ExpiresAt = Now + TillSessionLifetime
        };
        db.PosTillSessions.Add(session);
        // Old sessions tidy themselves away.
        await db.PosTillSessions.Where(t => t.ExpiresAt < Now.AddHours(-PosContract.OfflineUploadHours)).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        return (new TillSessionDto(token, storeId, staff.Id, staff.User.DisplayName, staff.Role, session.ExpiresAt, await OpenShiftDtoAsync(staff.Id, storeId, ct)), token);
    }

    sealed record PinAttempts(int Count, DateTimeOffset Until);

    /// <summary>
    /// The till behind a request's session token. <paramref name="offlineAt"/>: an offline sale
    /// being uploaded may use a session that has since locked, if it was rung up while it was open.
    /// </summary>
    public async Task<Till?> TillAsync(User device, string? token, CancellationToken ct, DateTimeOffset? offlineAt = null)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var session = await db.PosTillSessions.FirstOrDefaultAsync(t => t.TokenHash == hash && t.DeviceUserId == device.Id, ct);
        if (session is null)
            return null;
        var live = session.ExpiresAt > Now;
        var offlineOk = offlineAt is { } at && at <= session.ExpiresAt && at >= session.CreatedAt.AddMinutes(-5) && Now - at <= TimeSpan.FromHours(PosContract.OfflineUploadHours);
        if (!live && !offlineOk)
            return null;
        var staff = await db.PosStaff.Include(s => s.User).FirstOrDefaultAsync(s => s.Id == session.StaffId, ct);
        var store = await db.PosStores.FirstOrDefaultAsync(s => s.Id == session.StoreId, ct);
        if (staff is null || store is null || !CanWorkAt(staff, store.Id))
            return null;
        var merchant = await db.PosMerchants.FirstAsync(m => m.Id == store.MerchantId, ct);
        return new Till(session, staff, store, merchant, staff.User.DisplayName);
    }

    public async Task<TillSessionDto> SessionAsync(Till till, string token, CancellationToken ct) =>
        new(token, till.Store.Id, till.Staff.Id, till.StaffName, till.Staff.Role, till.Session.ExpiresAt, await OpenShiftDtoAsync(till.Staff.Id, till.Store.Id, ct));

    public async Task LockAsync(Till till, CancellationToken ct)
    {
        till.Session.ExpiresAt = Now;
        await db.SaveChangesAsync(ct);
    }

    // ---- Shifts (FR-AUTH-03) ----

    public async Task<PosShiftDto> OpenShiftAsync(Till till, decimal openingFloat, CancellationToken ct)
    {
        if (await OpenShiftAsync(till.Staff.Id, till.Store.Id, ct) is not null)
            throw new ChatRejectedException("Your shift is already open.");
        if (openingFloat is < 0 or > 1_000_000)
            throw new ChatRejectedException("Enter the cash in the drawer to start with.");
        var shift = new PosShift { Id = Guid.NewGuid(), StoreId = till.Store.Id, StaffId = till.Staff.Id, OpenedAt = Now, OpeningFloat = Money(openingFloat) };
        db.PosShifts.Add(shift);
        await db.SaveChangesAsync(ct);
        await AuditAsync(till, "shift.open", $"float {shift.OpeningFloat:0.00}", ct);
        return await ShiftDtoAsync(shift, ct);
    }

    public async Task<PosShiftDto?> CurrentShiftAsync(Till till, CancellationToken ct) => await OpenShiftDtoAsync(till.Staff.Id, till.Store.Id, ct);

    /// <summary>Drops to the safe, pay-ins, and opening the drawer without a sale (always audited).</summary>
    public async Task<PosShiftDto> MoveCashAsync(Till till, CashMovementRequest request, CancellationToken ct)
    {
        var shift = await OpenShiftAsync(till.Staff.Id, till.Store.Id, ct) ?? throw new ChatRejectedException("Open your shift first.");
        var kind = request.Kind is "drop" or "payin" or "nosale" ? request.Kind : throw new ChatRejectedException("That isn't a drawer movement.");
        var amount = kind == "nosale" ? 0 : Money(request.Amount);
        if (kind != "nosale" && amount <= 0)
            throw new ChatRejectedException("Enter an amount.");
        db.PosCashMovements.Add(new PosCashMovement { Id = Guid.NewGuid(), ShiftId = shift.Id, StaffId = till.Staff.Id, Kind = kind, Amount = amount, Note = Optional(request.Note, 200), At = Now });
        await db.SaveChangesAsync(ct);
        await AuditAsync(till, kind == "nosale" ? "drawer.nosale" : $"drawer.{kind}", $"{amount:0.00} {Optional(request.Note, 200)}".Trim(), ct);
        return await ShiftDtoAsync(shift, ct);
    }

    /// <summary>Closes the shift with the counted cash: the Z report, with its variance.</summary>
    public async Task<PosShiftDto> CloseShiftAsync(Till till, decimal countedCash, CancellationToken ct)
    {
        var shift = await OpenShiftAsync(till.Staff.Id, till.Store.Id, ct) ?? throw new ChatRejectedException("Your shift isn't open.");
        if (countedCash is < 0 or > 10_000_000)
            throw new ChatRejectedException("Enter the cash you counted.");
        (shift.ClosedAt, shift.CountedCash) = (Now, Money(countedCash));
        await db.SaveChangesAsync(ct);
        var dto = await ShiftDtoAsync(shift, ct);
        await AuditAsync(till, "shift.close", $"counted {shift.CountedCash:0.00}, expected {dto.Totals.ExpectedCash:0.00}, variance {dto.Totals.Variance:0.00}", ct);
        return dto;
    }

    Task<PosShift?> OpenShiftAsync(Guid staffId, Guid storeId, CancellationToken ct) =>
        db.PosShifts.Where(s => s.StaffId == staffId && s.StoreId == storeId && s.ClosedAt == null).OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(ct);

    async Task<PosShiftDto?> OpenShiftDtoAsync(Guid staffId, Guid storeId, CancellationToken ct) =>
        await OpenShiftAsync(staffId, storeId, ct) is { } shift ? await ShiftDtoAsync(shift, ct) : null;

    async Task<PosShiftDto> ShiftDtoAsync(PosShift shift, CancellationToken ct)
    {
        var name = await db.PosStaff.Where(s => s.Id == shift.StaffId).Select(s => s.User.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        var sales = await db.PosSales.AsNoTracking().Include(s => s.Payments).Where(s => s.ShiftId == shift.Id).ToListAsync(ct);
        var refunds = await db.PosSales.AsNoTracking().Include(s => s.Payments).Where(s => s.ReversedShiftId == shift.Id && s.Status == PosSaleStatuses.Refunded).ToListAsync(ct);
        var moves = await db.PosCashMovements.AsNoTracking().Where(m => m.ShiftId == shift.Id).ToListAsync(ct);
        var totals = Totals(sales, refunds, moves, shift.OpeningFloat, shift.CountedCash);
        return new PosShiftDto(shift.Id, shift.StoreId, name, shift.OpenedAt, shift.OpeningFloat, shift.ClosedAt, shift.CountedCash, totals);
    }

    /// <summary>A shift's (or a day's) figures. Voided sales don't count; refunds count where they were paid out.</summary>
    static PosShiftTotalsDto Totals(List<PosSale> sales, List<PosSale> refunds, List<PosCashMovement> moves, decimal openingFloat, decimal? counted)
    {
        var kept = sales.Where(s => s.Status != PosSaleStatuses.Voided).ToList();
        var byTender = kept.SelectMany(s => s.Payments.Select(p => (p.Tender, Amount: p.Tender == PosTenders.Cash ? p.Amount - s.Change : p.Amount)))
            .GroupBy(p => p.Tender).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
        var cashIn = byTender.GetValueOrDefault(PosTenders.Cash);
        var cashRefunded = refunds.Sum(s => CashShare(s));
        var drops = moves.Where(m => m.Kind == "drop").Sum(m => m.Amount);
        var paidIn = moves.Where(m => m.Kind == "payin").Sum(m => m.Amount);
        var expected = openingFloat + cashIn - cashRefunded - drops + paidIn;
        return new PosShiftTotalsDto(
            kept.Count,
            kept.Sum(s => s.Subtotal),
            kept.Sum(s => s.Discount),
            kept.Sum(s => s.Tax),
            kept.Sum(s => s.Total),
            byTender,
            sales.Count(s => s.Status == PosSaleStatuses.Voided),
            refunds.Count,
            refunds.Sum(s => s.Total),
            drops,
            paidIn,
            moves.Count(m => m.Kind == "nosale"),
            expected,
            counted is { } c ? c - expected : null);
    }

    /// <summary>The cash a refund gives back: what was paid in cash, net of change.</summary>
    static decimal CashShare(PosSale sale) => Math.Max(0, sale.Payments.Where(p => p.Tender == PosTenders.Cash).Sum(p => p.Amount) - sale.Change);

    // ---- Catalogue (FR-CAT-01/02) ----

    public async Task<List<PosCategoryDto>?> CategoriesAsync(User me, Guid merchantId, CancellationToken ct) =>
        await StaffOfAsync(me, merchantId, ct) is null
            ? null
            : (await db.PosCategories.AsNoTracking().Where(c => c.MerchantId == merchantId).OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<PosCategoryDto> SaveCategoryAsync(User me, Guid merchantId, Guid? categoryId, SaveCategoryRequest request, CancellationToken ct)
    {
        var staff = await RequireManagerAsync(me, merchantId, ct);
        var category = categoryId is { } id
            ? await db.PosCategories.FirstOrDefaultAsync(c => c.Id == id && c.MerchantId == merchantId, ct) ?? throw new ChatRejectedException("That category doesn't exist.")
            : db.PosCategories.Add(new PosCategory { Id = Guid.NewGuid(), MerchantId = merchantId, Name = "", Icon = "", Tone = "" }).Entity;
        category.Name = Required(request.Name, 60, "Category name");
        category.Icon = Optional(request.Icon, 16) ?? "🏷️";
        category.Tone = request.Tone is { } t && SocialGraphContract.GroupTones.Contains(t) ? t : "blue";
        category.Order = request.Order;
        await db.SaveChangesAsync(ct);
        return ToDto(category);
    }

    public async Task<bool> DeleteCategoryAsync(User me, Guid categoryId, CancellationToken ct)
    {
        var category = await db.PosCategories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category is null)
            return false;
        await RequireManagerAsync(me, category.MerchantId, ct);
        await db.PosProducts.Where(p => p.CategoryId == categoryId).ExecuteUpdateAsync(s => s.SetProperty(p => p.CategoryId, (Guid?)null), ct);
        db.PosCategories.Remove(category);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Everything a till at this store needs, including to sell offline: the catalogue with this store's stock.</summary>
    public async Task<PosCatalogDto?> CatalogAsync(User me, Guid storeId, CancellationToken ct)
    {
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null || await StaffOfAsync(me, store.MerchantId, ct) is not { } staff || !CanWorkAt(staff, storeId))
            return null;
        var categories = await db.PosCategories.AsNoTracking().Where(c => c.MerchantId == store.MerchantId).OrderBy(c => c.Order).ThenBy(c => c.Name).ToListAsync(ct);
        var products = await db.PosProducts.AsNoTracking().Where(p => p.MerchantId == store.MerchantId).OrderBy(p => p.Name).ToListAsync(ct);
        var stock = await StockAsync(storeId, ct);
        return new PosCatalogDto(categories.Select(ToDto).ToList(), products.Select(p => ToDto(p, stock)).ToList(), Now);
    }

    /// <summary>A scanned or typed code: a product's (or variant's) barcode or SKU (FR-CAT-01).</summary>
    public async Task<PosLookupDto?> LookupAsync(User me, Guid storeId, string? code, CancellationToken ct)
    {
        code = code?.Trim();
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (string.IsNullOrEmpty(code) || store is null || await StaffOfAsync(me, store.MerchantId, ct) is null)
            return null;
        var stock = await StockAsync(storeId, ct);
        var direct = await db.PosProducts.AsNoTracking().FirstOrDefaultAsync(p => p.MerchantId == store.MerchantId && (p.Barcode == code || p.Sku == code), ct);
        if (direct is not null)
            return new PosLookupDto(ToDto(direct, stock), null);
        // A variant's own code: variants live in JSON, so look through the ones that could hold it.
        var withVariants = await db.PosProducts.AsNoTracking()
            .Where(p => p.MerchantId == store.MerchantId && p.VariantsJson != null && p.VariantsJson.Contains(code)).ToListAsync(ct);
        foreach (var product in withVariants)
            if (Variants(product).FirstOrDefault(v => v.Barcode == code || v.Sku == code) is { } variant)
                return new PosLookupDto(ToDto(product, stock), variant.Name);
        return null;
    }

    public async Task<PosProductDto> SaveProductAsync(User me, Guid merchantId, Guid? productId, SaveProductRequest request, CancellationToken ct)
    {
        var staff = await RequireManagerAsync(me, merchantId, ct);
        var product = productId is { } id
            ? await db.PosProducts.FirstOrDefaultAsync(p => p.Id == id && p.MerchantId == merchantId, ct) ?? throw new ChatRejectedException("That product doesn't exist.")
            : db.PosProducts.Add(new PosProduct { Id = Guid.NewGuid(), MerchantId = merchantId, Name = "", Icon = "", IsActive = true }).Entity;
        if (request.CategoryId is { } categoryId && !await db.PosCategories.AnyAsync(c => c.Id == categoryId && c.MerchantId == merchantId, ct))
            throw new ChatRejectedException("That category doesn't exist.");

        var oldPrice = product.Price;
        product.CategoryId = request.CategoryId;
        product.Name = Required(request.Name, 120, "Product name");
        product.Icon = Optional(request.Icon, 16) ?? "📦";
        product.Sku = Optional(request.Sku, 64);
        product.Barcode = Optional(request.Barcode, 64);
        product.Price = Price(request.Price, "Price");
        product.TaxRatePercent = request.TaxRatePercent is { } rate ? Rate(rate) : null;
        product.IsActive = request.IsActive;
        product.SafetyStock = Math.Clamp(request.SafetyStock, 0, 1_000_000);
        product.TrackStock = request.TrackStock;
        var variants = (request.Variants ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Name))
            .Select(v => new PosVariantDto(Required(v.Name, 60, "Variant name"), Price(v.Price, "Variant price"), Optional(v.Sku, 64), Optional(v.Barcode, 64))).ToList();
        if (variants.Select(v => v.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != variants.Count)
            throw new ChatRejectedException("Each variant needs its own name.");
        var modifiers = (request.Modifiers ?? []).Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new PosModifierDto(Required(m.Name, 60, "Modifier name"), Price(m.Price, "Modifier price", allowZero: true))).ToList();
        product.VariantsJson = variants.Count == 0 ? null : ContractJson.Write(variants);
        product.ModifiersJson = modifiers.Count == 0 ? null : ContractJson.Write(modifiers);
        product.UpdatedAt = Now;

        // Codes are how the till finds things: no two products (or variants) share one.
        var codes = new[] { product.Barcode, product.Sku }.Concat(variants.SelectMany(v => new[] { v.Barcode, v.Sku })).OfType<string>().ToList();
        if (codes.Count != codes.Distinct().Count())
            throw new ChatRejectedException("A barcode or SKU is used twice on this product.");
        foreach (var code in codes)
            if (await db.PosProducts.AnyAsync(p => p.MerchantId == merchantId && p.Id != product.Id && (p.Barcode == code || p.Sku == code || (p.VariantsJson != null && p.VariantsJson.Contains("\"" + code + "\""))), ct))
                throw new ChatRejectedException($"Another product already uses the code {code}.");

        await db.SaveChangesAsync(ct);
        if (productId is not null && oldPrice != product.Price)
            await AuditAsync(merchantId, null, staff.Id, await NameAsync(staff, ct), "web", "product.price", $"{product.Name}: {oldPrice:0.00} → {product.Price:0.00}", ct);
        return ToDto(product, []);
    }

    // ---- Stock (FR-INV) ----

    public async Task<PosStockLevelDto> AdjustStockAsync(User me, Guid storeId, StockAdjustRequest request, CancellationToken ct)
    {
        var store = await db.PosStores.FirstOrDefaultAsync(s => s.Id == storeId, ct) ?? throw new ChatRejectedException("That store doesn't exist.");
        var staff = await RequireManagerAsync(me, store.MerchantId, ct);
        if (!CanWorkAt(staff, storeId))
            throw new ChatRejectedException("You don't manage this store.");
        var product = await db.PosProducts.FirstOrDefaultAsync(p => p.Id == request.ProductId && p.MerchantId == store.MerchantId, ct)
            ?? throw new ChatRejectedException("That product doesn't exist.");
        var variant = request.Variant ?? "";
        if (variant.Length > 0 && Variants(product).All(v => v.Name != variant))
            throw new ChatRejectedException("That product has no such variant.");
        if (request.Quantity == 0 || Math.Abs(request.Quantity) > 1_000_000)
            throw new ChatRejectedException("Enter how many arrived (or went).");
        if (!product.TrackStock)
            throw new ChatRejectedException($"{product.Name} isn't counted. Turn on \"Count stock\" for it under Products first.");

        var name = product.Name + (variant.Length > 0 ? $" ({variant})" : "");
        if (request.Quantity > 0)
            await AddStockAsync(storeId, product.Id, variant, request.Quantity, ct);
        else if (!await TakeStockAsync(storeId, product.Id, variant, -request.Quantity, ct))
            throw new ChatRejectedException($"There {Are(await InStockAsync(storeId, product.Id, variant, ct))} of {name} in stock, so {-request.Quantity:0.###} can't be taken out.");
        await AuditAsync(store.MerchantId, storeId, staff.Id, await NameAsync(staff, ct), "web", "stock.adjust",
            $"{name} {request.Quantity:+0.###;-0.###} {Optional(request.Reason, 200)}".Trim(), ct);
        var quantity = await InStockAsync(storeId, product.Id, variant, ct);
        return Level(product, variant, quantity);
    }

    /// <summary>What's low or out at a store (FR-INV-02).</summary>
    public async Task<List<PosStockAlertDto>?> StockAlertsAsync(User me, Guid storeId, CancellationToken ct)
    {
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null || await StaffOfAsync(me, store.MerchantId, ct) is not { } staff || !CanWorkAt(staff, storeId))
            return null;
        return await AlertsAsync(store, ct);
    }

    async Task<List<PosStockAlertDto>> AlertsAsync(PosStore store, CancellationToken ct)
    {
        var rows = await (from s in db.PosStock
                          join p in db.PosProducts on s.ProductId equals p.Id
                          where s.StoreId == store.Id && p.IsActive && p.TrackStock && s.Quantity <= p.SafetyStock
                          orderby s.Quantity
                          select new { p.Id, p.Name, s.Variant, s.Quantity, p.SafetyStock }).ToListAsync(ct);
        return rows.Select(r => new PosStockAlertDto(r.Id, r.Name, r.Variant, r.Quantity, r.SafetyStock, r.Quantity <= 0)).ToList();
    }

    static PosStockLevelDto Level(PosProduct product, string variant, decimal quantity) =>
        new(variant, quantity, quantity <= product.SafetyStock, quantity <= 0);

    static string Are(decimal quantity) => quantity == 1 ? "is only 1" : quantity == 0 ? "are none" : $"are only {quantity:0.###}";

    async Task<decimal> InStockAsync(Guid storeId, Guid productId, string variant, CancellationToken ct) =>
        await db.PosStock.Where(s => s.StoreId == storeId && s.ProductId == productId && s.Variant == variant).Select(s => (decimal?)s.Quantity).FirstOrDefaultAsync(ct) ?? 0;

    /// <summary>Puts stock in (a delivery, or a sale coming back).</summary>
    async Task AddStockAsync(Guid storeId, Guid productId, string variant, decimal quantity, CancellationToken ct)
    {
        var updated = await db.PosStock.Where(s => s.StoreId == storeId && s.ProductId == productId && s.Variant == variant)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity + quantity), ct);
        if (updated == 0)
        {
            db.PosStock.Add(new PosStock { StoreId = storeId, ProductId = productId, Variant = variant, Quantity = quantity });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>
    /// Takes <paramref name="quantity"/> out if the store has that many, in one statement, so two
    /// tills selling the last one at once can't both have it. False (and nothing taken) otherwise.
    /// </summary>
    async Task<bool> TakeStockAsync(Guid storeId, Guid productId, string variant, decimal quantity, CancellationToken ct) =>
        await db.PosStock.Where(s => s.StoreId == storeId && s.ProductId == productId && s.Variant == variant && s.Quantity >= quantity)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, x => x.Quantity - quantity), ct) > 0;

    async Task<Dictionary<(Guid, string), decimal>> StockAsync(Guid storeId, CancellationToken ct) =>
        (await db.PosStock.AsNoTracking().Where(s => s.StoreId == storeId).ToListAsync(ct)).ToDictionary(s => (s.ProductId, s.Variant), s => s.Quantity);

    // ---- Pricing (FR-CAT-02/03/04) ----

    sealed record Priced(PosQuoteDto Quote, List<(PosProduct Product, PosSaleLineDto Line)> Lines);

    /// <summary>Prices a cart the way checkout will: variants and modifiers, line and cart discounts, tax per line.</summary>
    public async Task<PosQuoteDto> QuoteAsync(Till till, CheckoutRequest request, CancellationToken ct) => (await PriceAsync(till, request, ct)).Quote;

    /// <summary>The cart priced by <see cref="PosPricing"/>, the same rules the till uses offline.</summary>
    async Task<Priced> PriceAsync(Till till, CheckoutRequest request, CancellationToken ct)
    {
        if (request.Lines is not { Count: > 0 })
            throw new ChatRejectedException("The cart is empty.");
        var ids = request.Lines.Select(l => l.ProductId).Distinct().ToList();
        var products = await db.PosProducts.AsNoTracking().Where(p => p.MerchantId == till.Merchant.Id && ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var cart = request.Lines.Select(line => products.TryGetValue(line.ProductId, out var product)
            ? (ToDto(product, []), line)
            : throw new ChatRejectedException("Something in the cart isn't for sale any more.")).ToList();
        try
        {
            var quote = PosPricing.Quote(cart, request.DiscountKind, request.DiscountValue, till.Merchant.TaxInclusive, till.Store.TaxRatePercent, till.Merchant.DiscountLimitPercent);
            return new Priced(quote, quote.Lines.Select(l => (products[l.ProductId], l)).ToList());
        }
        catch (PosPricingException ex)
        {
            throw new ChatRejectedException(ex.Message);
        }
    }

    // ---- Checkout (FR-PAY-01/02, FR-OFF, FR-INV-01) ----

    public async Task<PosSaleDto> CheckoutAsync(Till till, CheckoutRequest request, CancellationToken ct)
    {
        if (request.ClientSaleId == Guid.Empty)
            throw new ChatRejectedException("A sale needs its own id from the till.");

        // FR-PAY-02: the same sale again returns the first receipt, without charging twice.
        if (await db.PosSales.AsNoTracking().FirstOrDefaultAsync(s => s.MerchantId == till.Merchant.Id && s.ClientSaleId == request.ClientSaleId, ct) is { } existing)
        {
            if (Now - existing.ReceivedAt > TimeSpan.FromMinutes(PosContract.IdempotencyMinutes))
                throw new ChatRejectedException("That sale was already recorded.");
            return (await SaleDtoAsync(existing.Id, ct))!;
        }

        var occurredAt = request.Offline && request.OccurredAt is { } at ? at : Now;
        if (occurredAt > Now.AddMinutes(5) || Now - occurredAt > TimeSpan.FromHours(PosContract.OfflineUploadHours))
            throw new ChatRejectedException("That sale's time is out of range.");

        // The shift it belongs to: open now, or (for an offline sale) open when it was rung up.
        var shift = request.Offline
            ? await db.PosShifts.Where(s => s.StaffId == till.Staff.Id && s.StoreId == till.Store.Id && s.OpenedAt <= occurredAt && (s.ClosedAt == null || s.ClosedAt >= occurredAt))
                  .OrderByDescending(s => s.OpenedAt).FirstOrDefaultAsync(ct)
            : await OpenShiftAsync(till.Staff.Id, till.Store.Id, ct);
        if (shift is null)
            throw new ChatRejectedException(request.Offline ? "That offline sale doesn't fall in one of your shifts." : "Open your shift first.");

        var priced = await PriceAsync(till, request, ct);
        var quote = priced.Quote;

        PosStaff? approver = null;
        if (quote.NeedsApproval)
        {
            approver = await ApproverAsync(till, request.ApprovalPin, ct)
                ?? throw new ChatRejectedException($"{quote.ApprovalReason} A manager's PIN is needed.");
        }

        // Tenders: cash may be more than what's left (the rest is change); card and voucher can't.
        if (request.Tenders is not { Count: > 0 })
            throw new ChatRejectedException("Add how it's being paid.");
        var remaining = quote.Total;
        var cashTendered = 0m;
        var payments = new List<PosPayment>();
        foreach (var tender in request.Tenders)
        {
            if (!PosTenders.All.Contains(tender.Tender))
                throw new ChatRejectedException("That isn't a way to pay here.");
            if (request.Offline && tender.Tender != PosTenders.Cash)
                throw new ChatRejectedException("Offline, only cash can be taken.");
            var amount = Money(tender.Amount);
            if (amount <= 0)
                throw new ChatRejectedException("Each payment needs an amount.");
            if (tender.Tender != PosTenders.Cash && amount > remaining)
                throw new ChatRejectedException($"{PosTenders.Label(tender.Tender)} can't be more than what's left to pay.");
            if (tender.Tender == PosTenders.Cash)
                cashTendered += amount;
            remaining -= amount;
            payments.Add(new PosPayment { Id = Guid.NewGuid(), Tender = tender.Tender, Amount = amount, Reference = Optional(tender.Reference, 64) });
        }
        if (remaining > 0)
            throw new ChatRejectedException($"{remaining:0.00} is still to pay.");
        var change = Math.Min(-remaining, cashTendered);
        if (-remaining > cashTendered)
            throw new ChatRejectedException("Only cash can be overpaid (for change).");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // FR-INV-01: the sale takes its stock first, all or nothing (the transaction undoes it all
        // if anything below fails). Counted stock never goes below zero, so a till can't sell what
        // the store doesn't have. An offline sale already happened, though: it takes what there is,
        // and the difference goes in the audit trail for the manager.
        var shortfalls = new List<string>();
        foreach (var group in priced.Lines.Where(l => l.Product.TrackStock).GroupBy(l => (l.Product.Id, l.Line.Variant)))
        {
            var quantity = group.Sum(l => l.Line.Quantity);
            if (await TakeStockAsync(till.Store.Id, group.Key.Id, group.Key.Variant, quantity, ct))
                continue;
            var have = await InStockAsync(till.Store.Id, group.Key.Id, group.Key.Variant, ct);
            var name = group.First().Product.Name + (group.Key.Variant.Length > 0 ? $" ({group.Key.Variant})" : "");
            if (!request.Offline)
                throw new ChatRejectedException(have <= 0 ? $"{name} is out of stock." : $"Only {have:0.###} of {name} left in stock.");
            if (have > 0)
                await TakeStockAsync(till.Store.Id, group.Key.Id, group.Key.Variant, have, ct);
            shortfalls.Add($"{name}: sold {quantity:0.###} offline with {have:0.###} in stock");
        }

        // Ndeipi Pay: each payment must be paid, for this store and amount, and pay for this sale only.
        foreach (var payment in payments.Where(p => p.Tender == PosTenders.Qr))
        {
            var qrId = Guid.TryParse(payment.Reference, out var id) ? id : Guid.Empty;
            var qr = await db.PosQrPayments.FirstOrDefaultAsync(q => q.Id == qrId && q.StoreId == till.Store.Id, ct)
                ?? throw new ChatRejectedException("That Ndeipi Pay payment isn't from this till.");
            if (qr.Status != PosQrStatuses.Paid)
                throw new ChatRejectedException("That Ndeipi Pay payment hasn't come through yet.");
            if (qr.Amount != payment.Amount)
                throw new ChatRejectedException("That Ndeipi Pay payment was for a different amount.");
            if (qr.SaleId is not null)
                throw new ChatRejectedException("That Ndeipi Pay payment has already paid for a sale.");
        }

        await db.PosStores.Where(s => s.Id == till.Store.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ReceiptCount, x => x.ReceiptCount + 1), ct);
        var receiptNumber = await db.PosStores.Where(s => s.Id == till.Store.Id).Select(s => s.ReceiptCount).FirstAsync(ct);

        var sale = new PosSale
        {
            Id = Guid.NewGuid(),
            MerchantId = till.Merchant.Id,
            StoreId = till.Store.Id,
            ShiftId = shift.Id,
            StaffId = till.Staff.Id,
            ClientSaleId = request.ClientSaleId,
            ReceiptNumber = receiptNumber,
            Status = PosSaleStatuses.Completed,
            Subtotal = quote.Subtotal,
            Discount = quote.Discount,
            Tax = quote.Tax,
            Total = quote.Total,
            Change = change,
            OccurredAt = occurredAt,
            ReceivedAt = Now,
            Offline = request.Offline,
            Payments = payments
        };
        var position = 0;
        foreach (var (product, line) in priced.Lines)
            sale.Lines.Add(new PosSaleLine
            {
                Id = Guid.NewGuid(),
                Position = position++,
                ProductId = line.ProductId,
                Name = line.Name,
                Variant = line.Variant,
                Modifiers = line.Modifiers.Count == 0 ? null : string.Join(", ", line.Modifiers),
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Discount = line.Discount,
                TaxRatePercent = line.TaxRatePercent,
                Tax = line.Tax,
                Total = line.Total
            });
        db.PosSales.Add(sale);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same sale sent twice at once: the first one stands.
            await tx.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            var first = await db.PosSales.AsNoTracking().FirstOrDefaultAsync(s => s.MerchantId == till.Merchant.Id && s.ClientSaleId == request.ClientSaleId, ct);
            return first is null ? throw new ChatRejectedException("The sale couldn't be recorded. Try again.") : (await SaleDtoAsync(first.Id, ct))!;
        }

        // A payment can only ever pay for one sale: the unique index on SaleId holds that even if
        // two tills try at once.
        var qrIds = payments.Where(p => p.Tender == PosTenders.Qr).Select(p => Guid.Parse(p.Reference!)).ToList();
        if (qrIds.Count > 0
            && await db.PosQrPayments.Where(q => qrIds.Contains(q.Id) && q.SaleId == null).ExecuteUpdateAsync(s => s.SetProperty(q => q.SaleId, sale.Id), ct) != qrIds.Count)
            throw new ChatRejectedException("That Ndeipi Pay payment has already paid for a sale.");
        await tx.CommitAsync(ct);

        if (approver is not null)
            await AuditAsync(till, "discount.override", $"receipt {Receipt(till.Store, receiptNumber)}: {quote.ApprovalReason} Approved by {await NameAsync(approver, ct)}; discount {quote.Discount:0.00}", ct);
        if (shortfalls.Count > 0)
            await AuditAsync(till, "stock.shortfall", $"receipt {Receipt(till.Store, receiptNumber)}: {string.Join("; ", shortfalls)}", ct);

        // Paid by scanning: the customer is known, so their receipt goes straight to their chats.
        if (qrIds.Count > 0)
            await SendReceiptToPayersAsync(till, sale.Id, qrIds, ct);
        return (await SaleDtoAsync(sale.Id, ct))!;
    }

    /// <summary>
    /// Voids (in the sale's own open shift) or refunds (later) a sale, with a manager's PIN. Stock
    /// comes back. A refund's cash leaves the drawer of the shift it's done in.
    /// </summary>
    public async Task<PosSaleDto?> ReverseAsync(Till till, Guid saleId, bool refund, ReverseSaleRequest request, CancellationToken ct)
    {
        var sale = await db.PosSales.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == saleId && s.MerchantId == till.Merchant.Id, ct);
        if (sale is null)
            return null;
        if (sale.Status != PosSaleStatuses.Completed)
            throw new ChatRejectedException($"That sale was already {sale.Status}.");
        var reason = Required(request.Reason, 300, "A reason");
        var approver = await ApproverAsync(till, request.ApprovalPin, ct)
            ?? throw new ChatRejectedException($"A manager's PIN is needed to {(refund ? "refund" : "void")} a sale.");
        var currentShift = await OpenShiftAsync(till.Staff.Id, till.Store.Id, ct) ?? throw new ChatRejectedException("Open your shift first.");
        if (!refund)
        {
            var saleShift = await db.PosShifts.FirstAsync(s => s.Id == sale.ShiftId, ct);
            if (saleShift.ClosedAt is not null)
                throw new ChatRejectedException("That sale's shift has closed: refund it instead.");
        }

        sale.Status = refund ? PosSaleStatuses.Refunded : PosSaleStatuses.Voided;
        sale.StatusReason = reason;
        sale.ReversedAt = Now;
        sale.ReversedShiftId = refund ? currentShift.Id : sale.ShiftId;
        var productIds = sale.Lines.Select(l => l.ProductId).Distinct().ToList();
        var counted = await db.PosProducts.Where(p => productIds.Contains(p.Id) && p.TrackStock).Select(p => p.Id).ToListAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.SaveChangesAsync(ct);
        foreach (var group in sale.Lines.Where(l => counted.Contains(l.ProductId)).GroupBy(l => (l.ProductId, l.Variant)))
            await AddStockAsync(sale.StoreId, group.Key.ProductId, group.Key.Variant, group.Sum(l => l.Quantity), ct);
        await tx.CommitAsync(ct);

        var store = await db.PosStores.AsNoTracking().FirstAsync(s => s.Id == sale.StoreId, ct);
        await AuditAsync(till, refund ? "sale.refund" : "sale.void",
            $"receipt {Receipt(store, sale.ReceiptNumber)}, {sale.Total:0.00}: {reason}. Approved by {await NameAsync(approver, ct)}", ct);
        return await SaleDtoAsync(sale.Id, ct);
    }

    /// <summary>A manager or admin of this store, by their PIN (FR-CAT-04 escalation).</summary>
    async Task<PosStaff?> ApproverAsync(Till till, string? pin, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(pin))
            return null;
        var managers = await db.PosStaff.Where(s => s.MerchantId == till.Merchant.Id && s.PinHash != null
            && (s.Role == PosRoles.Admin || s.Role == PosRoles.Manager) && (s.StoreId == null || s.StoreId == till.Store.Id)).ToListAsync(ct);
        return managers.FirstOrDefault(m => Matches(m, pin));
    }

    /// <summary>FR-PAY-03: the receipt, as a message in the customer's Ndeipi chats.</summary>
    public async Task<bool> SendReceiptAsync(User device, Till till, Guid saleId, string? emailOrPhone, CancellationToken ct)
    {
        var sale = await SaleDtoAsync(saleId, ct);
        if (sale is null || sale.StoreId != till.Store.Id)
            return false;
        var contact = ShamwariContact.Normalize(emailOrPhone);
        var customer = ShamwariContact.IsEmail(contact)
            ? await db.Users.FirstOrDefaultAsync(u => u.EmailVerified && u.Email == contact, ct)
            : await db.Users.FirstOrDefaultAsync(u => u.Phone == contact, ct);
        if (customer is null)
            throw new ChatRejectedException("Nobody on Ndeipi has that email or phone. Print the receipt instead.");
        if (customer.Id == device.Id)
            throw new ChatRejectedException("That's this till's own account.");

        await SendReceiptAsync(device, customer.Id, saleId, ct);
        return true;
    }

    /// <summary>The receipt as a receipt card in the customer's chat with the till's account (PosReceiptHandler fills it in).</summary>
    async Task SendReceiptAsync(User device, Guid customerId, Guid saleId, CancellationToken ct)
    {
        var chat = await conversations.CreateAsync(device, new CreateConversationRequest(ConversationType.Direct, [customerId], null), ct);
        await messages.SendAsync(device, new SendMessageRequest(chat.Id, MessageKinds.PosReceipt, ContractJson.ToElement(new PosReceiptPayload(saleId)), Guid.NewGuid()), ct);
    }

    /// <summary>After a sale paid by scanning: each payer gets the receipt, from the account signed in on the till.</summary>
    async Task SendReceiptToPayersAsync(Till till, Guid saleId, List<Guid> qrIds, CancellationToken ct)
    {
        var device = await db.Users.FirstAsync(u => u.Id == till.Session.DeviceUserId, ct);
        var payers = await db.PosQrPayments.Where(q => qrIds.Contains(q.Id) && q.PayerId != null && q.PayerId != device.Id)
            .Select(q => q.PayerId!.Value).Distinct().ToListAsync(ct);
        foreach (var payer in payers)
        {
            try
            {
                await SendReceiptAsync(device, payer, saleId, ct);
            }
            catch (ChatRejectedException)
            {
                // The sale stands either way; the till can still print or resend it.
            }
        }
    }

    /// <summary>A receipt as plain text: for chats, and the shape of a printed one.</summary>
    public static string ReceiptText(PosSaleDto sale)
    {
        var text = new StringBuilder();
        text.AppendLine($"🧾 {sale.MerchantName} · {sale.StoreName}");
        text.AppendLine($"Receipt {sale.ReceiptNumber} · {sale.OccurredAt:yyyy-MM-dd HH:mm} UTC");
        foreach (var line in sale.Lines)
        {
            var name = line.Name + (line.Variant.Length > 0 ? $" ({line.Variant})" : "") + (line.Modifiers.Count > 0 ? $" + {string.Join(", ", line.Modifiers)}" : "");
            text.AppendLine($"{line.Quantity:0.###} × {name}  {line.Total:0.00}");
        }
        if (sale.Discount > 0)
            text.AppendLine($"Discount  -{sale.Discount:0.00}");
        text.AppendLine($"Tax{(sale.TaxInclusive ? " (included)" : "")}  {sale.Tax:0.00}");
        text.AppendLine($"Total  {sale.Total:0.00} {sale.Currency}");
        foreach (var payment in sale.Payments)
            text.AppendLine($"{PosTenders.Label(payment.Tender)}  {payment.Amount:0.00}");
        if (sale.Change > 0)
            text.AppendLine($"Change  {sale.Change:0.00}");
        text.Append("Thank you!");
        return text.ToString();
    }

    // ---- Sales, shifts and dashboards for managers and auditors ----

    public async Task<PosSaleDto?> SaleAsync(User me, Guid saleId, CancellationToken ct)
    {
        var sale = await db.PosSales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null || await StaffOfAsync(me, sale.MerchantId, ct) is not { } staff || !CanWorkAt(staff, sale.StoreId))
            return null;
        // Cashiers see receipts from the till; the list and history are for managers and auditors.
        return await SaleDtoAsync(saleId, ct);
    }

    public async Task<List<PosSaleDto>?> SalesAsync(User me, Guid storeId, DateOnly? day, CancellationToken ct)
    {
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null || await StaffOfAsync(me, store.MerchantId, ct) is not { } staff || !CanWorkAt(staff, storeId) || !(PosRoles.CanManage(staff.Role) || PosRoles.CanAudit(staff.Role)))
            return null;
        var from = new DateTimeOffset((day ?? DateOnly.FromDateTime(Now.UtcDateTime)).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var ids = await db.PosSales.Where(s => s.StoreId == storeId && s.OccurredAt >= from && s.OccurredAt < from.AddDays(1))
            .OrderByDescending(s => s.OccurredAt).Select(s => s.Id).Take(500).ToListAsync(ct);
        var result = new List<PosSaleDto>();
        foreach (var id in ids)
            result.Add((await SaleDtoAsync(id, ct))!);
        return result;
    }

    /// <summary>Today at a store: its figures, open shifts, stock warnings and latest sales.</summary>
    public async Task<PosDashboardDto?> DashboardAsync(User me, Guid storeId, CancellationToken ct)
    {
        var store = await db.PosStores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null || await StaffOfAsync(me, store.MerchantId, ct) is not { } staff || !CanWorkAt(staff, storeId) || !(PosRoles.CanManage(staff.Role) || PosRoles.CanAudit(staff.Role)))
            return null;
        var from = new DateTimeOffset(Now.UtcDateTime.Date, TimeSpan.Zero);
        var sales = await db.PosSales.AsNoTracking().Include(s => s.Payments).Where(s => s.StoreId == storeId && s.OccurredAt >= from).ToListAsync(ct);
        var refunds = await db.PosSales.AsNoTracking().Include(s => s.Payments)
            .Where(s => s.StoreId == storeId && s.Status == PosSaleStatuses.Refunded && s.ReversedAt >= from).ToListAsync(ct);
        var shifts = await db.PosShifts.AsNoTracking().Where(s => s.StoreId == storeId && s.ClosedAt == null).ToListAsync(ct);
        var shiftIds = shifts.Select(s => s.Id).ToList();
        var moves = await db.PosCashMovements.AsNoTracking().Where(m => shiftIds.Contains(m.ShiftId)).ToListAsync(ct);
        var open = new List<PosShiftDto>();
        foreach (var shift in shifts)
            open.Add(await ShiftDtoAsync(shift, ct));
        var recent = new List<PosSaleDto>();
        foreach (var sale in sales.OrderByDescending(s => s.OccurredAt).Take(10))
            recent.Add((await SaleDtoAsync(sale.Id, ct))!);
        return new PosDashboardDto(Totals(sales, refunds, moves, 0, null), open, await AlertsAsync(store, ct), recent);
    }

    async Task<PosSaleDto?> SaleDtoAsync(Guid saleId, CancellationToken ct)
    {
        var sale = await db.PosSales.AsNoTracking().Include(s => s.Lines).Include(s => s.Payments).FirstOrDefaultAsync(s => s.Id == saleId, ct);
        if (sale is null)
            return null;
        var store = await db.PosStores.AsNoTracking().FirstAsync(s => s.Id == sale.StoreId, ct);
        var merchant = await db.PosMerchants.AsNoTracking().FirstAsync(m => m.Id == sale.MerchantId, ct);
        var cashier = await db.PosStaff.Where(s => s.Id == sale.StaffId).Select(s => s.User.DisplayName).FirstOrDefaultAsync(ct) ?? "";
        // Change is given from the cash; show it against the cash payment.
        var changeLeft = sale.Change;
        var payments = sale.Payments.Select(p =>
        {
            var change = p.Tender == PosTenders.Cash ? Math.Min(changeLeft, p.Amount) : 0;
            changeLeft -= change;
            return new PosPaymentDto(p.Tender, p.Amount, change, p.Reference);
        }).ToList();
        return new PosSaleDto(sale.Id, sale.ClientSaleId, Receipt(store, sale.ReceiptNumber), store.Id, store.Name, merchant.Name, merchant.Currency, merchant.TaxInclusive,
            cashier, sale.Status, sale.OccurredAt, sale.Offline,
            sale.Lines.OrderBy(l => l.Position).Select(l => new PosSaleLineDto(l.ProductId, l.Name, l.Variant,
                l.Modifiers?.Split(", ", StringSplitOptions.RemoveEmptyEntries) ?? [], l.Quantity, l.UnitPrice, l.Discount, l.TaxRatePercent, l.Tax, l.Total)).ToList(),
            sale.Subtotal, sale.Discount, sale.Tax, sale.Total, payments, sale.Change, sale.StatusReason);
    }

    internal static string Receipt(PosStore store, int number) => $"{store.Name[..Math.Min(3, store.Name.Length)].ToUpperInvariant()}-{number:000000}";

    // ---- Audit trail (NFR-SEC-03) ----

    Task AuditAsync(Till till, string action, string details, CancellationToken ct) =>
        AuditAsync(till.Merchant.Id, till.Store.Id, till.Staff.Id, till.StaffName, till.Session.Terminal, action, details, ct);

    /// <summary>Appends to the merchant's chain: each entry's hash covers it and the one before.</summary>
    async Task AuditAsync(Guid merchantId, Guid? storeId, Guid staffId, string staffName, string terminal, string action, string details, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var last = await db.PosAudit.AsNoTracking().Where(a => a.MerchantId == merchantId).OrderByDescending(a => a.Sequence)
                .Select(a => new { a.Sequence, a.Hash }).FirstOrDefaultAsync(ct);
            var entry = new PosAuditEntry
            {
                MerchantId = merchantId,
                Sequence = (last?.Sequence ?? 0) + 1,
                At = Now,
                StoreId = storeId,
                StaffId = staffId,
                StaffName = staffName.Length > 120 ? staffName[..120] : staffName,
                Terminal = terminal.Length > 120 ? terminal[..120] : terminal,
                Action = action,
                Details = details.Length > 2000 ? details[..2000] : details,
                PreviousHash = last?.Hash ?? GenesisHash,
                Hash = ""
            };
            entry.Hash = HashOf(entry);
            db.PosAudit.Add(entry);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException) when (attempt < 5)
            {
                // Someone else took that sequence number: go again on top of theirs.
                db.Entry(entry).State = EntityState.Detached;
            }
        }
    }

    static string HashOf(PosAuditEntry e) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join('|', e.MerchantId, e.Sequence, e.At.UtcDateTime.ToString("O"), e.StoreId, e.StaffId, e.StaffName, e.Terminal, e.Action, e.Details, e.PreviousHash))));

    public async Task<List<PosAuditDto>?> AuditTrailAsync(User me, Guid merchantId, long? before, CancellationToken ct)
    {
        if (await StaffOfAsync(me, merchantId, ct) is not { } staff || !PosRoles.CanAudit(staff.Role))
            return null;
        var stores = await db.PosStores.AsNoTracking().Where(s => s.MerchantId == merchantId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var entries = await db.PosAudit.AsNoTracking().Where(a => a.MerchantId == merchantId && (before == null || a.Sequence < before))
            .OrderByDescending(a => a.Sequence).Take(200).ToListAsync(ct);
        return entries.Select(a => new PosAuditDto(a.Sequence, a.At, a.Action, a.StaffName, a.StoreId is { } s ? stores.GetValueOrDefault(s) : null, a.Terminal, a.Details, a.Hash)).ToList();
    }

    /// <summary>Recomputes the whole chain: any edited, removed or inserted entry shows as the first break.</summary>
    public async Task<PosAuditCheckDto?> VerifyAuditAsync(User me, Guid merchantId, CancellationToken ct)
    {
        if (await StaffOfAsync(me, merchantId, ct) is not { } staff || !PosRoles.CanAudit(staff.Role))
            return null;
        var entries = await db.PosAudit.AsNoTracking().Where(a => a.MerchantId == merchantId).OrderBy(a => a.Sequence).ToListAsync(ct);
        var previous = GenesisHash;
        long expected = 1;
        foreach (var entry in entries)
        {
            if (entry.Sequence != expected || entry.PreviousHash != previous || HashOf(entry) != entry.Hash)
                return new PosAuditCheckDto(entries.Count, false, entry.Sequence);
            (previous, expected) = (entry.Hash, expected + 1);
        }
        return new PosAuditCheckDto(entries.Count, true, null);
    }

    // ---- Helpers ----

    Task<PosStaff?> StaffOfAsync(User me, Guid merchantId, CancellationToken ct) =>
        db.PosStaff.FirstOrDefaultAsync(s => s.MerchantId == merchantId && s.UserId == me.Id, ct);

    async Task<PosStaff> RequireRoleAsync(User me, Guid merchantId, string role, CancellationToken ct) =>
        await StaffOfAsync(me, merchantId, ct) is { } staff && staff.Role == role
            ? staff
            : throw new ChatRejectedException($"Only a {PosRoles.Label(role).ToLowerInvariant()} can do that.");

    async Task<PosStaff> RequireManagerAsync(User me, Guid merchantId, CancellationToken ct) =>
        await StaffOfAsync(me, merchantId, ct) is { } staff && PosRoles.CanManage(staff.Role)
            ? staff
            : throw new ChatRejectedException("Only a manager or merchant admin can do that.");

    static bool CanWorkAt(PosStaff staff, Guid storeId) => staff.StoreId is null || staff.StoreId == storeId;

    async Task<bool> IsOwnerAsync(PosStaff staff) => await db.PosMerchants.AnyAsync(m => m.Id == staff.MerchantId && m.OwnerId == staff.UserId);

    async Task CheckStoreAsync(Guid merchantId, Guid? storeId, CancellationToken ct)
    {
        if (storeId is { } id && !await db.PosStores.AnyAsync(s => s.Id == id && s.MerchantId == merchantId, ct))
            throw new ChatRejectedException("That store doesn't exist.");
    }

    async Task<string> NameAsync(PosStaff staff, CancellationToken ct) =>
        await db.Users.Where(u => u.Id == staff.UserId).Select(u => u.DisplayName).FirstOrDefaultAsync(ct) ?? "";

    static bool Matches(PosStaff staff, string pin) =>
        staff.PinHash is { } hash && staff.PinSalt is { } salt && pin.Length is >= PosContract.MinPinLength and <= PosContract.MaxPinLength
        && CryptographicOperations.FixedTimeEquals(hash, HashPin(pin, salt));

    static byte[] HashPin(string pin, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(pin, salt, PinIterations, HashAlgorithmName.SHA256, 32);

    static List<PosVariantDto> Variants(PosProduct p) => p.VariantsJson is { } json ? ContractJson.Read<List<PosVariantDto>>(json) ?? [] : [];

    static List<PosModifierDto> Modifiers(PosProduct p) => p.ModifiersJson is { } json ? ContractJson.Read<List<PosModifierDto>>(json) ?? [] : [];

    static decimal Money(decimal value) => PosPricing.Money(value);

    static decimal Price(decimal value, string what, bool allowZero = true) =>
        value < 0 || value > 10_000_000 || (!allowZero && value == 0) || decimal.Round(value, 2) != value
            ? throw new ChatRejectedException($"{what} is an amount to the cent.")
            : value;

    static decimal Rate(decimal value) => value is >= 0 and <= 100 ? decimal.Round(value, 2) : throw new ChatRejectedException("A tax rate is a percentage from 0 to 100.");

    static string Currency(string? code) => code?.Trim().ToUpperInvariant() is { Length: >= 3 and <= 8 } c && c.All(char.IsAsciiLetterOrDigit)
        ? c
        : throw new ChatRejectedException("Enter a currency code, e.g. USD or ZWG.");

    static string Role(string? role) => role is { } r && PosRoles.All.Contains(r) ? r : throw new ChatRejectedException("That isn't a role here.");

    static string Required(string? value, int max, string what) =>
        Optional(value, max) ?? throw new ChatRejectedException($"{what} is needed.");

    static string? Optional(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : throw new ChatRejectedException($"Keep it to {max} characters.");
    }

    static PosStoreDto ToDto(PosStore s) => new(s.Id, s.Name, s.Address, s.TaxRatePercent, s.ReceiptCount);

    static PosStaffDto ToDto(PosStaff s) => new(s.Id, ChatMapper.ToDto(s.User), s.Role, s.StoreId, s.PinHash is not null, s.AddedAt);

    static PosCategoryDto ToDto(PosCategory c) => new(c.Id, c.Name, c.Icon, c.Tone, c.Order);

    static PosProductDto ToDto(PosProduct p, Dictionary<(Guid, string), decimal> stock)
    {
        var variants = Variants(p);
        var levels = !p.TrackStock ? [] : new[] { "" }.Concat(variants.Select(v => v.Name))
            .Where(v => stock.ContainsKey((p.Id, v)))
            .Select(v => Level(p, v, stock[(p.Id, v)]))
            .ToList();
        return new PosProductDto(p.Id, p.CategoryId, p.Name, p.Icon, p.Sku, p.Barcode, p.Price, p.TaxRatePercent, p.IsActive, p.SafetyStock, variants, Modifiers(p), levels, p.TrackStock);
    }
}
