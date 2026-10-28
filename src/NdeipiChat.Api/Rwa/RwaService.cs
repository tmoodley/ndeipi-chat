using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Rwa;

/// <summary>The gates and numbers for Ndeipi RWA (configuration "Rwa").</summary>
public sealed class RwaOptions
{
    public const string Section = "Rwa";

    /// <summary>
    /// The settlement gate: <see cref="RwaModes.Off"/> (the default) or <see cref="RwaModes.Simulated"/>.
    /// There is no live mode: real money and minting wait for a legal review and their own change.
    /// </summary>
    public string Mode { get; set; } = RwaModes.Off;

    /// <summary>FR-1.3: the statutory cooling-off before money leaves escrow for the Year 1 vault.</summary>
    public int CoolingOffDays { get; set; } = 7;

    /// <summary>The test balance each person starts with while simulated.</summary>
    public decimal SimStartingBalanceUsd { get; set; } = 50_000m;

    /// <summary>NFR-2: the most one oracle update may move a villa's NAV, in percent, so a bad feed can't reprice it.</summary>
    public decimal MaxNavStepPercent { get; set; } = 10m;

    /// <summary>How often the sweeper vests holdings whose cooling-off has ended.</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public bool Simulated => string.Equals(Mode, RwaModes.Simulated, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Green Island pre-sale (FR-1.2, FR-1.3, FR-2.1, FR-2.2, FR-2.4): the villa catalogue and its
/// calendar, buying a week, month or year into a cooling-off escrow, cancelling inside it, vesting
/// into the Year 1 vault after it, and the oracle's NAV updates.
/// </summary>
public sealed class RwaService(ChatDbContext db, IOptions<RwaOptions> options, TimeProvider clock, ILogger<RwaService> log)
{
    DateTimeOffset Now => clock.GetUtcNow();

    RwaOptions Options => options.Value;

    public static bool IsOracle(User user) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(FinanceContract.AdminRole, StringComparer.OrdinalIgnoreCase);

    public async Task<RwaMeDto> MeAsync(User me, CancellationToken ct)
    {
        var balance = Options.Simulated ? (await AccountAsync(me, ct)).BalanceUsd : 0m;
        return new RwaMeDto(Options.Simulated ? RwaModes.Simulated : RwaModes.Off, await KycVerifiedAsync(me, ct), IsOracle(me), balance,
            Options.CoolingOffDays, RwaContract.SimulatedNotice);
    }

    // ------------------------------------------------------------------ catalogue

    public async Task<IReadOnlyList<RwaAssetDto>> AssetsAsync(CancellationToken ct)
    {
        await SeedAsync(ct);
        var assets = await db.RwaAssets.AsNoTracking().OrderBy(a => a.Code).ToListAsync(ct);
        var sold = await db.RwaWeekSlots.GroupBy(s => s.AssetId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return assets.Select(a => ToDto(a, sold.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task<RwaAssetDetailDto?> AssetAsync(string code, CancellationToken ct)
    {
        await SeedAsync(ct);
        var asset = await db.RwaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Code == code, ct);
        if (asset is null)
            return null;
        var taken = (await db.RwaWeekSlots.Where(s => s.AssetId == asset.Id).Select(s => s.Week).ToListAsync(ct)).ToHashSet();

        var weeks = Enumerable.Range(1, RwaContract.WeeksPerYear).Select(w =>
        {
            var season = RwaSeasons.Of(w);
            return new RwaWeekDto(w, season, RwaSeasons.Weight(season), RwaPricing.Price(asset.UnitPriceUsd, RwaIntervals.Week, w), taken.Contains(w));
        }).ToList();
        var months = Enumerable.Range(1, RwaContract.MonthsPerYear).Select(m =>
        {
            var (first, last) = RwaCalendar.MonthWeeks(m);
            return new RwaMonthDto(m, first, last, RwaPricing.Price(asset.UnitPriceUsd, RwaIntervals.Month, m),
                !RwaCalendar.Weeks(RwaIntervals.Month, m).Any(taken.Contains));
        }).ToList();

        return new RwaAssetDetailDto(ToDto(asset, taken.Count), asset.Highlights.Split('\n', StringSplitOptions.RemoveEmptyEntries), weeks, months,
            RwaPricing.Price(asset.UnitPriceUsd, RwaIntervals.FullYear, 1), taken.Count == 0);
    }

    // ------------------------------------------------------------------ pre-sale (FR-1.2, FR-1.3, FR-2.1)

    public async Task<RwaHoldingDto> MintAsync(User me, RwaMintRequest r, CancellationToken ct)
    {
        if (!Options.Simulated)
            throw new ChatRejectedException("The Green Island pre-sale is switched off.");
        if (r.RequestId == Guid.Empty)
            throw new ChatRejectedException("Try the purchase again.");
        if (await HoldingDtoAsync(me.Id, h => h.RequestId == r.RequestId, ct) is { } already)
            return already;

        if (r.PaymentMethod != RwaPaymentMethods.Simulated)
            throw new ChatRejectedException("Only simulated settlement is switched on. Ndeipi RWA is a preview until its legal review is done.");
        if (!RwaIntervals.IsValid(r.IntervalType) || !RwaCalendar.IsValidIndex(r.IntervalType, r.IntervalIndex))
            throw new ChatRejectedException("Pick a week from 1 to 52, a month from 1 to 12, or the full year.");

        await SeedAsync(ct);
        var asset = await db.RwaAssets.AsNoTracking().FirstOrDefaultAsync(a => a.Code == r.AssetCode, ct)
            ?? throw new ChatRejectedException("That villa isn't on sale.");
        if (asset.ConstructionPhase != RwaPhases.LandPresale)
            throw new ChatRejectedException($"{asset.Name}'s pre-sale has closed.");

        // FR-1.2: identity first. Bridge's KYC stands in for Mpost.Global until that partner is live.
        var verified = await KycVerifiedAsync(me, ct);
        Audit("kyc_check", me.Id, asset.Id, null, verified ? "passed (bridge)" : "refused: identity not verified (bridge)");
        if (!verified)
        {
            await db.SaveChangesAsync(ct);
            throw new ChatRejectedException("Verify your identity in your Wallet before buying a villa share.");
        }

        var price = RwaPricing.Price(asset.UnitPriceUsd, r.IntervalType, r.IntervalIndex);
        var season = r.IntervalType == RwaIntervals.Week ? RwaSeasons.Of(r.IntervalIndex) : "";
        var holding = new RwaHolding
        {
            Id = Guid.NewGuid(),
            UserId = me.Id,
            AssetId = asset.Id,
            RequestId = r.RequestId,
            IntervalType = r.IntervalType,
            IntervalIndex = r.IntervalIndex,
            Season = season,
            SeasonWeight = season == "" ? 1m : RwaSeasons.Weight(season),
            ShareFraction = RwaContract.ShareFraction(r.IntervalType),
            PrincipalUsd = price,
            PaymentMethod = r.PaymentMethod,
            Status = RwaHoldingStatus.CoolingOff,
            PurchasedAt = Now,
            CoolingOffEndsAt = Now.AddDays(Options.CoolingOffDays)
        };

        await AccountAsync(me, ct);
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // The money goes into escrow: taken from the balance only if it's there.
            var paid = await db.RwaSimAccounts.Where(a => a.UserId == me.Id && a.BalanceUsd >= price)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.BalanceUsd, a => a.BalanceUsd - price), ct);
            if (paid == 0)
                throw new ChatRejectedException($"Your simulated balance doesn't cover {RwaContract.Money(price)}.");

            db.RwaHoldings.Add(holding);
            foreach (var week in RwaCalendar.Weeks(r.IntervalType, r.IntervalIndex))
                db.RwaWeekSlots.Add(new RwaWeekSlot { AssetId = asset.Id, Week = week, HoldingId = holding.Id });
            Audit("mint", me.Id, asset.Id, holding.Id,
                $"{RwaContract.IntervalLabel(r.IntervalType, r.IntervalIndex)} of {asset.Code} for {RwaContract.Money(price)}, simulated, cooling-off until {holding.CoolingOffEndsAt:u}");
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2627 or 2601 })
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                // The same request again (a double tap), or someone else took one of its weeks first.
                if (await HoldingDtoAsync(me.Id, h => h.RequestId == r.RequestId, ct) is { } first)
                    return first;
                throw new ChatRejectedException(r.IntervalType == RwaIntervals.Week
                    ? $"Week {r.IntervalIndex} of {asset.Name} has just been sold."
                    : $"Part of that {(r.IntervalType == RwaIntervals.Month ? "month" : "year")} at {asset.Name} has been sold. Pick another.");
            }
            await tx.CommitAsync(ct);
        }

        log.LogInformation("RWA simulated mint {Holding}: {Asset} {Type} {Index} for {Price}", holding.Id, asset.Code, r.IntervalType, r.IntervalIndex, price);
        return (await HoldingDtoAsync(me.Id, h => h.Id == holding.Id, ct))!;
    }

    /// <summary>FR-1.3: cancelling inside the cooling-off returns all the money and frees the weeks.</summary>
    public async Task<RwaHoldingDto?> CancelAsync(User me, Guid id, CancellationToken ct)
    {
        var holding = await db.RwaHoldings.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.UserId == me.Id, ct);
        if (holding is null)
            return null;
        if (holding.Status == RwaHoldingStatus.Cancelled)
            return await HoldingDtoAsync(me.Id, h => h.Id == id, ct);
        if (holding.Status != RwaHoldingStatus.CoolingOff || holding.CoolingOffEndsAt <= Now)
            throw new ChatRejectedException("The cooling-off period has ended, so this share can't be cancelled for a refund.");

        var now = Now;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var cancelled = await db.RwaHoldings.Where(h => h.Id == id && h.Status == RwaHoldingStatus.CoolingOff && h.CoolingOffEndsAt > now)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, RwaHoldingStatus.Cancelled).SetProperty(h => h.CancelledAt, now), ct);
            if (cancelled == 0)
                throw new ChatRejectedException("The cooling-off period has ended, so this share can't be cancelled for a refund.");
            await db.RwaWeekSlots.Where(s => s.HoldingId == id).ExecuteDeleteAsync(ct);
            await db.RwaSimAccounts.Where(a => a.UserId == me.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.BalanceUsd, a => a.BalanceUsd + holding.PrincipalUsd), ct);
            Audit("cancel", me.Id, holding.AssetId, id, $"refunded {RwaContract.Money(holding.PrincipalUsd)} inside the cooling-off");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return await HoldingDtoAsync(me.Id, h => h.Id == id, ct);
    }

    /// <summary>
    /// FR-2.2: holdings whose cooling-off has ended leave escrow for the Year 1 vault, as shares at the
    /// villa's NAV at that moment. Run by the sweeper, and before a portfolio is read so it's never stale.
    /// </summary>
    public async Task<int> VestDueAsync(CancellationToken ct, Guid? userId = null)
    {
        var now = Now;
        var due = await db.RwaHoldings
            .Where(h => h.Status == RwaHoldingStatus.CoolingOff && h.CoolingOffEndsAt <= now && (userId == null || h.UserId == userId))
            .Join(db.RwaAssets, h => h.AssetId, a => a.Id, (h, a) => new { h.Id, h.UserId, h.AssetId, h.PrincipalUsd, a.BaseNavUsd, a.CurrentNavUsd })
            .Take(500).ToListAsync(ct);
        var vested = 0;
        foreach (var d in due)
        {
            var assetsPerShare = AssetsPerShare(d.BaseNavUsd, d.CurrentNavUsd);
            var shares = Math.Round(d.PrincipalUsd / assetsPerShare, 6);
            var done = await db.RwaHoldings.Where(h => h.Id == d.Id && h.Status == RwaHoldingStatus.CoolingOff)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, RwaHoldingStatus.Vested).SetProperty(h => h.VestedAt, now).SetProperty(h => h.VaultShares, shares), ct);
            if (done == 0)
                continue;
            Audit("vest", d.UserId, d.AssetId, d.Id, $"{RwaContract.Money(d.PrincipalUsd)} into the Year 1 vault as {shares} shares at {assetsPerShare} per share");
            vested++;
        }
        if (vested > 0)
            await db.SaveChangesAsync(ct);
        return vested;
    }

    // ------------------------------------------------------------------ portfolio

    public async Task<RwaPortfolioDto> PortfolioAsync(User me, CancellationToken ct)
    {
        await VestDueAsync(ct, me.Id);
        var holdings = await HoldingDtosAsync(me.Id, _ => true, ct);
        var live = holdings.Where(h => h.Status != RwaHoldingStatus.Cancelled).ToList();
        var balance = Options.Simulated ? (await AccountAsync(me, ct)).BalanceUsd : 0m;
        return new RwaPortfolioDto(holdings, live.Sum(h => h.PrincipalUsd), live.Sum(h => h.ValueUsd),
            live.Where(h => h.Status == RwaHoldingStatus.CoolingOff).Sum(h => h.PrincipalUsd), balance);
    }

    // ------------------------------------------------------------------ NAV oracle (FR-2.4, NFR-2)

    public async Task<RwaAssetDto> PostNavAsync(User me, RwaNavUpdateRequest r, CancellationToken ct)
    {
        if (!IsOracle(me))
            throw new ChatRejectedException("Only a NAV oracle can post a NAV.");
        var attestation = r.Attestation?.Trim() ?? "";
        if (attestation.Length is 0 or > 200)
            throw new ChatRejectedException("Say what the new NAV is based on, such as the valuation or audit reference.");
        var asset = await db.RwaAssets.FirstOrDefaultAsync(a => a.Code == r.AssetCode, ct)
            ?? throw new ChatRejectedException("No asset has that code.");
        if (r.NewNavUsd <= 0)
            throw new ChatRejectedException("A NAV must be more than zero.");
        var step = Math.Abs(r.NewNavUsd - asset.CurrentNavUsd) / asset.CurrentNavUsd * 100m;
        if (step > Options.MaxNavStepPercent)
            throw new ChatRejectedException($"One update can move the NAV by at most {Options.MaxNavStepPercent:0.##}%. This one moves it {step:0.##}%.");

        Audit("nav", me.Id, asset.Id, null, $"{asset.CurrentNavUsd:0.######} -> {r.NewNavUsd:0.######}: {attestation}");
        (asset.CurrentNavUsd, asset.NavUpdatedAt, asset.UpdatedAt) = (r.NewNavUsd, Now, Now);
        await db.SaveChangesAsync(ct);
        var sold = await db.RwaWeekSlots.CountAsync(s => s.AssetId == asset.Id, ct);
        return ToDto(asset, sold);
    }

    // ------------------------------------------------------------------ helpers

    public static decimal AssetsPerShare(decimal baseNav, decimal currentNav) =>
        baseNav <= 0 ? 1m : Math.Round(currentNav / baseNav, 6);

    async Task<bool> KycVerifiedAsync(User me, CancellationToken ct) =>
        await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == me.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct) == BankingProfile.Approved;

    async Task<RwaSimAccount> AccountAsync(User me, CancellationToken ct)
    {
        if (await db.RwaSimAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == me.Id, ct) is { } account)
            return account;
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [RwaSimAccounts] ([UserId], [BalanceUsd], [CreatedAt])
                SELECT {me.Id}, {Options.SimStartingBalanceUsd}, {Now}
                WHERE NOT EXISTS (SELECT 1 FROM [RwaSimAccounts] WITH (UPDLOCK, HOLDLOCK) WHERE [UserId] = {me.Id})
                """, ct);
        }
        catch (SqlException e) when (e.Number is 2627 or 2601 or 1205)
        {
            // Another request made it first.
        }
        return await db.RwaSimAccounts.AsNoTracking().FirstAsync(a => a.UserId == me.Id, ct);
    }

    void Audit(string kind, Guid? userId, Guid? assetId, Guid? holdingId, string detail) =>
        db.RwaAuditEvents.Add(new RwaAuditEvent { At = Now, Kind = kind, UserId = userId, AssetId = assetId, HoldingId = holdingId, Detail = detail[..Math.Min(detail.Length, 1000)] });

    async Task<RwaHoldingDto?> HoldingDtoAsync(Guid userId, System.Linq.Expressions.Expression<Func<RwaHolding, bool>> where, CancellationToken ct) =>
        (await HoldingDtosAsync(userId, where, ct)).FirstOrDefault();

    async Task<List<RwaHoldingDto>> HoldingDtosAsync(Guid userId, System.Linq.Expressions.Expression<Func<RwaHolding, bool>> where, CancellationToken ct)
    {
        var rows = await db.RwaHoldings.AsNoTracking().Where(h => h.UserId == userId).Where(where)
            .Join(db.RwaAssets, h => h.AssetId, a => a.Id, (h, a) => new { h, a.Code, a.Name, a.BaseNavUsd, a.CurrentNavUsd })
            .OrderByDescending(x => x.h.PurchasedAt).ToListAsync(ct);
        return rows.Select(x =>
        {
            var h = x.h;
            var value = h.Status switch
            {
                RwaHoldingStatus.Vested => Math.Round(h.VaultShares * AssetsPerShare(x.BaseNavUsd, x.CurrentNavUsd), 2),
                RwaHoldingStatus.CoolingOff => h.PrincipalUsd,
                _ => 0m
            };
            return new RwaHoldingDto(h.Id, h.TokenId, x.Code, x.Name, h.IntervalType, h.IntervalIndex, RwaContract.IntervalLabel(h.IntervalType, h.IntervalIndex),
                h.Season, RwaContract.Fraction(h.IntervalType), h.PrincipalUsd, h.PaymentMethod, h.Status, h.PurchasedAt, h.CoolingOffEndsAt, h.VestedAt,
                h.CancelledAt, h.VaultShares, value, h.PaymentMethod == RwaPaymentMethods.Simulated);
        }).ToList();
    }

    static RwaAssetDto ToDto(RwaAsset a, int weeksSold) =>
        new(a.Id, a.Code, a.Name, a.Location, a.AssetClass, a.TokenStandard, a.Summary, a.Bedrooms, a.UnitPriceUsd, a.BaseNavUsd, a.CurrentNavUsd,
            AssetsPerShare(a.BaseNavUsd, a.CurrentNavUsd), a.ConstructionPhase, weeksSold, a.NavUpdatedAt);

    /// <summary>Puts the phase 1 villas in the catalogue the first time it's read.</summary>
    async Task SeedAsync(CancellationToken ct)
    {
        if (await db.RwaAssets.AnyAsync(ct))
            return;
        foreach (var a in RwaSeed.Villas(Now))
            db.RwaAssets.Add(a);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2627 or 2601 })
        {
            db.ChangeTracker.Clear();
        }
    }
}

/// <summary>The phase 1 catalogue. The prices are placeholders until the SPV's valuation is in.</summary>
public static class RwaSeed
{
    public static IEnumerable<RwaAsset> Villas(DateTimeOffset now) =>
    [
        new()
        {
            Id = Guid.NewGuid(), Code = "GI-VILLA-01", Name = "Green Island Villa 01", Location = "Green Island, Negril, Jamaica",
            AssetClass = RwaAssetClasses.ReitVilla, TokenStandard = "ERC1155", Bedrooms = 3, UnitPriceUsd = 520_000m, BaseNavUsd = 1m, CurrentNavUsd = 1m,
            ConstructionPhase = RwaPhases.LandPresale, NavUpdatedAt = now, CreatedAt = now, UpdatedAt = now,
            Summary = "A three-bedroom sea-view villa on the Green Island land pre-sale. Year 1 money is invested while permits are granted, and the returns pay for the build.",
            Highlights = "Three bedrooms, sea view\nPre-sale price: the land phase\nYear 1 money goes into the yield vault, not spent\nNAV grows with vault returns and audited build progress"
        },
        new()
        {
            Id = Guid.NewGuid(), Code = "GI-CONDO-01", Name = "Green Island Garden Condo 01", Location = "Green Island, Negril, Jamaica",
            AssetClass = RwaAssetClasses.ReitVilla, TokenStandard = "ERC1155", Bedrooms = 1, UnitPriceUsd = 156_000m, BaseNavUsd = 1m, CurrentNavUsd = 1m,
            ConstructionPhase = RwaPhases.LandPresale, NavUpdatedAt = now, CreatedAt = now, UpdatedAt = now,
            Summary = "A one-bedroom garden condo in the same Green Island phase, for a smaller first share.",
            Highlights = "One bedroom, garden terrace\nSame Year 1 vault as the villas\nWeeks priced by Negril's seasons"
        }
    ];
}

/// <summary>FR-2.2: vests holdings whose cooling-off has ended, every minute.</summary>
public sealed class RwaVestingSweeper(IServiceScopeFactory scopes, IOptions<RwaOptions> options, TimeProvider clock, ILogger<RwaVestingSweeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.SweepInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RwaService>().VestDueAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "RWA vesting sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
