using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The POS sub-app against its SRS: roles, PIN tills, shifts, pricing, checkout, offline, stock and audit.</summary>
public sealed class PosTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = PosContract.BasePath;

    /// <summary>A shop with one store, a product or two, its owner's PIN set and the owner's till unlocked.</summary>
    sealed class Shop
    {
        public required TestUser Owner { get; init; }
        public required PosMerchantDto Merchant { get; init; }
        public required Guid StoreId { get; init; }
        public required string Till { get; set; }
        public required PosProductDto Cola { get; init; }
        public required PosProductDto Burger { get; init; }
    }

    /// <param name="colaStock">Colas are counted, and the shop starts with this many. Burgers are made to order, so not counted.</param>
    async Task<Shop> ShopAsync(string name, bool taxInclusive = false, decimal taxRate = 15, decimal colaStock = 50)
    {
        var owner = await app.CreateUserAsync($"{name} Owner", $"{name.ToLowerInvariant()}.owner@example.test");
        var merchant = await SendAsync<PosMerchantDto>(owner, HttpMethod.Post, $"{Base}/merchants",
            new CreateMerchantRequest(name, "usd", taxInclusive, "Main Street", taxRate));
        await SendAsync<object>(owner, HttpMethod.Put, $"{Base}/merchants/{merchant.Id}/pin", new SetPinRequest("2580"));
        var cola = await SendAsync<PosProductDto>(owner, HttpMethod.Post, $"{Base}/merchants/{merchant.Id}/products",
            new SaveProductRequest(null, "Cola", "🥤", "COLA-1", "5000112637922", 1.50m, null, true, 5, null, null));
        var burger = await SendAsync<PosProductDto>(owner, HttpMethod.Post, $"{Base}/merchants/{merchant.Id}/products",
            new SaveProductRequest(null, "Burger", "🍔", "BURG", null, 5.00m, null, true, 0,
                [new PosVariantDto("Double", 7.00m, "BURG-D", "600000000001")],
                [new PosModifierDto("Cheese", 0.50m), new PosModifierDto("Bacon", 1.00m)], TrackStock: false));
        var storeId = merchant.Stores[0].Id;
        if (colaStock > 0)
            await SendAsync<PosStockLevelDto>(owner, HttpMethod.Post, $"{Base}/stores/{storeId}/stock", new StockAdjustRequest(cola.Id, null, colaStock, "Opening count"));
        var till = await SendAsync<TillSessionDto>(owner, HttpMethod.Post, $"{Base}/stores/{storeId}/till", new UnlockTillRequest("2580", "Front till"));
        return new Shop { Owner = owner, Merchant = merchant, StoreId = storeId, Till = till.Token, Cola = cola, Burger = burger };
    }

    static async Task<T> SendAsync<T>(TestUser user, HttpMethod method, string path, object? body = null, string? till = null)
    {
        using var response = await RawAsync(user, method, path, body, till);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}", null, response.StatusCode);
        return response.StatusCode == HttpStatusCode.NoContent ? default! : (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    static async Task<HttpResponseMessage> RawAsync(TestUser user, HttpMethod method, string path, object? body = null, string? till = null)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body, options: ContractJson.Options) };
        if (till is not null)
            request.Headers.Add(PosContract.TillHeader, till);
        return await user.Http.SendAsync(request);
    }

    static async Task<string> ProblemAsync(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("title").GetString()!;

    static CheckoutRequest Sale(IReadOnlyList<CartLineRequest> lines, IReadOnlyList<TenderRequest> tenders, Guid? id = null,
        string? discountKind = null, decimal discount = 0, string? approvalPin = null, bool offline = false, DateTimeOffset? at = null) =>
        new(id ?? Guid.NewGuid(), lines, discountKind, discount, tenders, at, offline, approvalPin);

    static CartLineRequest Line(PosProductDto product, decimal quantity = 1, string? variant = null, IReadOnlyList<string>? modifiers = null, string? discountKind = null, decimal discount = 0) =>
        new(product.Id, variant, modifiers, quantity, discountKind, discount);

    [Fact]
    public async Task A_merchant_is_set_up_with_its_owner_as_admin_and_staff_are_added_with_roles()
    {
        var shop = await ShopAsync("Kudzi Mart");
        Assert.Equal((PosRoles.Admin, true, "USD"), (shop.Merchant.MyRole, true, shop.Merchant.Currency));

        var cashier = await app.CreateUserAsync("Tari Cashier", "tari.cashier@example.test");
        var added = await SendAsync<PosStaffDto>(shop.Owner, HttpMethod.Post, $"{Base}/merchants/{shop.Merchant.Id}/staff",
            new AddStaffRequest("tari.cashier@example.test", PosRoles.Cashier, shop.StoreId));
        Assert.Equal((PosRoles.Cashier, false), (added.Role, added.PinSet));

        // A cashier can't run the back office: no catalogue changes, no staff list.
        using (var product = await RawAsync(cashier, HttpMethod.Post, $"{Base}/merchants/{shop.Merchant.Id}/products",
                   new SaveProductRequest(null, "Free lunch", null, null, null, 0, null, true, 0, null, null)))
            Assert.Equal(HttpStatusCode.BadRequest, product.StatusCode);
        using (var staff = await RawAsync(cashier, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/staff"))
            Assert.Equal(HttpStatusCode.NotFound, staff.StatusCode);

        // PINs are unique within the business, and not trivially guessable.
        using (var taken = await RawAsync(cashier, HttpMethod.Put, $"{Base}/merchants/{shop.Merchant.Id}/pin", new SetPinRequest("2580")))
            Assert.Contains("Someone else", await ProblemAsync(taken));
        using (var weak = await RawAsync(cashier, HttpMethod.Put, $"{Base}/merchants/{shop.Merchant.Id}/pin", new SetPinRequest("1234")))
            Assert.Equal(HttpStatusCode.BadRequest, weak.StatusCode);
        await SendAsync<object>(cashier, HttpMethod.Put, $"{Base}/merchants/{shop.Merchant.Id}/pin", new SetPinRequest("4711"));

        // The owner can't be demoted or removed.
        var ownerStaff = (await SendAsync<List<PosStaffDto>>(shop.Owner, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/staff")).Single(s => s.User.Id == shop.Owner.Id);
        using var demote = await RawAsync(shop.Owner, HttpMethod.Put, $"{Base}/staff/{ownerStaff.Id}", new SaveStaffRequest(PosRoles.Cashier, null));
        Assert.Equal(HttpStatusCode.BadRequest, demote.StatusCode);
    }

    [Fact]
    public async Task The_till_unlocks_with_a_PIN_locks_out_guessing_and_needs_a_session_for_till_calls()
    {
        var shop = await ShopAsync("Pin Place");
        using (var noSession = await RawAsync(shop.Owner, HttpMethod.Get, $"{Base}/till"))
            Assert.Equal(HttpStatusCode.Unauthorized, noSession.StatusCode);
        var session = await SendAsync<TillSessionDto>(shop.Owner, HttpMethod.Get, $"{Base}/till", till: shop.Till);
        Assert.Equal((PosRoles.Admin, (PosShiftDto?)null), (session.Role, session.Shift));

        // FR-AUTH-02 locking: the session stops working.
        await SendAsync<object>(shop.Owner, HttpMethod.Delete, $"{Base}/till", till: shop.Till);
        using (var locked = await RawAsync(shop.Owner, HttpMethod.Get, $"{Base}/till", till: shop.Till))
            Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);

        // Five wrong PINs pause the store's till, even for the right one.
        for (var i = 0; i < PosContract.MaxPinAttempts; i++)
            using (var wrong = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/till", new UnlockTillRequest("9999")))
                Assert.Equal("That PIN isn't right.", await ProblemAsync(wrong));
        using var paused = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/till", new UnlockTillRequest("2580"));
        Assert.Contains("Too many wrong PINs", await ProblemAsync(paused));

        // A device signed in to someone who doesn't work here can't unlock it at all.
        var stranger = await app.CreateUserAsync("Pin Stranger");
        using var outsider = await RawAsync(stranger, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/till", new UnlockTillRequest("2580"));
        Assert.Contains("isn't signed in to an account that works", await ProblemAsync(outsider));
    }

    [Fact]
    public async Task Prices_add_variants_and_modifiers_and_tax_is_added_or_included()
    {
        var added = await ShopAsync("Exclusive Eats", taxInclusive: false, taxRate: 15);
        var quote = await SendAsync<PosQuoteDto>(added.Owner, HttpMethod.Post, $"{Base}/till/quote",
            Sale([Line(added.Burger, 2, "Double", ["Cheese", "Bacon"]), Line(added.Cola, 3)], []), till: added.Till);
        // Burger: (7.00 + 0.50 + 1.00) × 2 = 17.00; cola 4.50. Tax added at 15%: 2.55 + 0.68.
        Assert.Equal(8.50m, quote.Lines[0].UnitPrice);
        Assert.Equal((21.50m, 3.23m, 24.73m), (quote.Subtotal, quote.Tax, quote.Total));

        var included = await ShopAsync("Inclusive Inn", taxInclusive: true, taxRate: 15);
        var vat = await SendAsync<PosQuoteDto>(included.Owner, HttpMethod.Post, $"{Base}/till/quote", Sale([Line(included.Cola, 2)], []), till: included.Till);
        // 3.00 including 15% VAT: 3.00 - 3.00/1.15 = 0.39.
        Assert.Equal((3.00m, 0.39m, 3.00m), (vat.Subtotal, vat.Tax, vat.Total));

        using var bad = await RawAsync(added.Owner, HttpMethod.Post, $"{Base}/till/quote", Sale([Line(added.Burger, 1, "Triple")], []), till: added.Till);
        Assert.Contains("has no Triple", await ProblemAsync(bad));
    }

    [Fact]
    public async Task Discounts_over_the_limit_need_a_managers_PIN()
    {
        var shop = await ShopAsync("Discount Den");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(50), till: shop.Till);

        // 5% on a line is within the 10% limit; 20% off the cart isn't.
        var small = await SendAsync<PosQuoteDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/quote",
            Sale([Line(shop.Burger, 2, discountKind: PosDiscountKinds.Percent, discount: 5)], []), till: shop.Till);
        Assert.Equal((0.50m, false), (small.Discount, small.NeedsApproval));

        var big = Sale([Line(shop.Burger, 2)], [new TenderRequest(PosTenders.Cash, 20, null)], discountKind: PosDiscountKinds.Percent, discount: 20);
        using (var refused = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", big, till: shop.Till))
            Assert.Contains("A manager's PIN is needed", await ProblemAsync(refused));
        using (var wrongPin = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", big with { ApprovalPin = "1111" }, till: shop.Till))
            Assert.Equal(HttpStatusCode.BadRequest, wrongPin.StatusCode);
        var sold = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", big with { ApprovalPin = "2580" }, till: shop.Till);
        // 10.00 less 20% = 8.00, plus 15% tax = 9.20.
        Assert.Equal((2.00m, 9.20m), (sold.Discount, sold.Total));

        var trail = await SendAsync<List<PosAuditDto>>(shop.Owner, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit");
        Assert.Contains(trail, a => a.Action == "discount.override" && a.Terminal == "Front till");
    }

    [Fact]
    public async Task Checkout_splits_tenders_gives_change_takes_stock_and_is_idempotent()
    {
        var shop = await ShopAsync("Split Shop", colaStock: 0);
        using (var noShift = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 5, null)]), till: shop.Till))
            Assert.Contains("Open your shift", await ProblemAsync(noShift));
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(100), till: shop.Till);
        await SendAsync<PosStockLevelDto>(shop.Owner, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/stock", new StockAdjustRequest(shop.Cola.Id, null, 10, "Delivery"));

        // 4 colas = 6.00 + 0.90 tax = 6.90: 5.00 on card, 5.00 in cash, 3.10 change.
        var request = Sale([Line(shop.Cola, 4)], [new TenderRequest(PosTenders.Card, 5, "AUTH-778812"), new TenderRequest(PosTenders.Cash, 5, null)]);
        var sale = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", request, till: shop.Till);
        Assert.Equal((6.90m, 3.10m, "MAI-000001"), (sale.Total, sale.Change, sale.ReceiptNumber));
        Assert.Equal("AUTH-778812", sale.Payments.Single(p => p.Tender == PosTenders.Card).Reference);

        // FR-PAY-02: the same key again is the same sale, not a second charge.
        var again = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", request, till: shop.Till);
        Assert.Equal((sale.Id, sale.ReceiptNumber), (again.Id, again.ReceiptNumber));

        // FR-INV-01/02: 10 - 4 = 6, now at or below... not yet: the safety stock is 5.
        var catalog = await SendAsync<PosCatalogDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/catalog");
        Assert.Equal((6m, false), (catalog.Products.Single(p => p.Id == shop.Cola.Id).Stock[0].Quantity, catalog.Products.Single(p => p.Id == shop.Cola.Id).Stock[0].IsLow));

        // Card can't be more than what's owed; the total has to be covered.
        using (var over = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Card, 5, null)]), till: shop.Till))
            Assert.Contains("can't be more than", await ProblemAsync(over));
        using (var under = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales", Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 1, null)]), till: shop.Till))
            Assert.Contains("still to pay", await ProblemAsync(under));

        // A scanned barcode finds the product; a variant's own barcode finds the variant.
        var scanned = await SendAsync<PosLookupDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/lookup?code=5000112637922");
        Assert.Equal(shop.Cola.Id, scanned.Product.Id);
        var variant = await SendAsync<PosLookupDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/lookup?code=600000000001");
        Assert.Equal((shop.Burger.Id, "Double"), (variant.Product.Id, variant.Variant));
    }

    [Fact]
    public async Task A_shift_reports_its_cash_and_closes_with_a_variance()
    {
        var shop = await ShopAsync("Shift Stall");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(50), till: shop.Till);
        await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Burger, 2)], [new TenderRequest(PosTenders.Cash, 20, null)]), till: shop.Till); // 11.50, change 8.50
        await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola, 2)], [new TenderRequest(PosTenders.Card, 3.45m, "A1")]), till: shop.Till); // 3.45 card
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift/cash", new CashMovementRequest("drop", 20, "To safe"), till: shop.Till);
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift/cash", new CashMovementRequest("nosale", 0, "Change for a note"), till: shop.Till);

        // The X report: 50 float + 11.50 cash - 20 drop = 41.50 expected.
        var x = await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Get, $"{Base}/till/shift", till: shop.Till);
        Assert.Equal((2, 14.95m, 41.50m, 1), (x.Totals.Sales, x.Totals.Net, x.Totals.ExpectedCash, x.Totals.NoSales));
        Assert.Equal((11.50m, 3.45m), (x.Totals.ByTender[PosTenders.Cash], x.Totals.ByTender[PosTenders.Card]));

        // The Z report: counted 41.00, so 0.50 short.
        var z = await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift/close", new CloseShiftRequest(41), till: shop.Till);
        Assert.Equal((41m, -0.50m), (z.CountedCash, z.Totals.Variance));
        Assert.NotNull(z.ClosedAt);
        var trail = await SendAsync<List<PosAuditDto>>(shop.Owner, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit");
        Assert.Contains(trail, a => a.Action == "drawer.nosale");
        Assert.Contains(trail, a => a.Action == "shift.close" && a.Details.Contains("variance -0.50"));
    }

    [Fact]
    public async Task Voids_and_refunds_need_a_manager_put_stock_back_and_count_in_the_right_shift()
    {
        var shop = await ShopAsync("Return Rack", colaStock: 2);
        var cashier = await app.CreateUserAsync("Rudo Returns", "rudo.returns@example.test");
        await SendAsync<PosStaffDto>(shop.Owner, HttpMethod.Post, $"{Base}/merchants/{shop.Merchant.Id}/staff", new AddStaffRequest("rudo.returns@example.test", PosRoles.Cashier, null));
        await SendAsync<object>(cashier, HttpMethod.Put, $"{Base}/merchants/{shop.Merchant.Id}/pin", new SetPinRequest("3141"));
        var cashierTill = (await SendAsync<TillSessionDto>(cashier, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/till", new UnlockTillRequest("3141"))).Token;
        await SendAsync<PosShiftDto>(cashier, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(20), till: cashierTill);

        var sale = await SendAsync<PosSaleDto>(cashier, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola, 2)], [new TenderRequest(PosTenders.Cash, 3.45m, null)]), till: cashierTill);
        // The cashier's own PIN isn't a manager's.
        using (var self = await RawAsync(cashier, HttpMethod.Post, $"{Base}/till/sales/{sale.Id}/void", new ReverseSaleRequest("Rang twice", "3141"), till: cashierTill))
            Assert.Contains("manager's PIN", await ProblemAsync(self));
        var voided = await SendAsync<PosSaleDto>(cashier, HttpMethod.Post, $"{Base}/till/sales/{sale.Id}/void", new ReverseSaleRequest("Rang twice", "2580"), till: cashierTill);
        Assert.Equal((PosSaleStatuses.Voided, "Rang twice"), (voided.Status, voided.StatusReason));
        // Both colas are back on the shelf.
        var catalog = await SendAsync<PosCatalogDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/catalog");
        Assert.Equal(2m, catalog.Products.Single(p => p.Id == shop.Cola.Id).Stock[0].Quantity);

        // A sale from a closed shift is refunded (not voided), paid from the drawer that's open now.
        var second = await SendAsync<PosSaleDto>(cashier, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Burger)], [new TenderRequest(PosTenders.Cash, 10, null)]), till: cashierTill); // 5.75
        await SendAsync<PosShiftDto>(cashier, HttpMethod.Post, $"{Base}/till/shift/close", new CloseShiftRequest(25.75m), till: cashierTill);
        await SendAsync<PosShiftDto>(cashier, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(30), till: cashierTill);
        using (var tooLate = await RawAsync(cashier, HttpMethod.Post, $"{Base}/till/sales/{second.Id}/void", new ReverseSaleRequest("Cold", "2580"), till: cashierTill))
            Assert.Contains("refund it instead", await ProblemAsync(tooLate));
        await SendAsync<PosSaleDto>(cashier, HttpMethod.Post, $"{Base}/till/sales/{second.Id}/refund", new ReverseSaleRequest("Cold", "2580"), till: cashierTill);
        var shift = await SendAsync<PosShiftDto>(cashier, HttpMethod.Get, $"{Base}/till/shift", till: cashierTill);
        Assert.Equal((1, 5.75m, 24.25m), (shift.Totals.Refunds, shift.Totals.Refunded, shift.Totals.ExpectedCash));

        var trail = await SendAsync<List<PosAuditDto>>(shop.Owner, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit");
        Assert.Contains(trail, a => a.Action == "sale.void" && a.StaffName == "Rudo Returns" && a.Details.Contains("Approved by Return Rack Owner"));
        Assert.Contains(trail, a => a.Action == "sale.refund");
    }

    [Fact]
    public async Task Offline_cash_sales_upload_later_in_their_shift_and_never_take_stock_below_zero()
    {
        var shop = await ShopAsync("Offline Outlet", colaStock: 1);
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(10), till: shop.Till);
        await Task.Delay(50);
        var ringUpAt = DateTimeOffset.UtcNow;

        // Only cash while offline.
        using (var card = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Card, 1.73m, null)], offline: true, at: ringUpAt), till: shop.Till))
            Assert.Contains("only cash", await ProblemAsync(card));

        // The till locked before it got back online; the sale still uploads.
        await SendAsync<object>(shop.Owner, HttpMethod.Delete, $"{Base}/till", till: shop.Till);
        var sale = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola, 3)], [new TenderRequest(PosTenders.Cash, 10, null)], offline: true, at: ringUpAt), till: shop.Till);
        Assert.True(sale.Offline);
        Assert.Equal(ringUpAt.ToUnixTimeSeconds(), sale.OccurredAt.ToUnixTimeSeconds());

        // 3 sold offline with only 1 counted in: the sale stands (it happened), stock stops at none,
        // and the 2 unaccounted for go in the audit trail for the manager.
        var alerts = await SendAsync<List<PosStockAlertDto>>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/alerts");
        Assert.Contains(alerts, a => a.ProductId == shop.Cola.Id && a.Quantity == 0 && a.IsOut);
        var trail = await SendAsync<List<PosAuditDto>>(shop.Owner, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit");
        Assert.Contains(trail, a => a.Action == "stock.shortfall" && a.Details.Contains("Cola: sold 3 offline with 1 in stock"));

        // But a live sale needs a live session.
        using var live = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 2, null)]), till: shop.Till);
        Assert.Equal(HttpStatusCode.Unauthorized, live.StatusCode);
    }

    [Fact]
    public async Task The_audit_trail_is_a_hash_chain_that_shows_tampering_and_only_auditors_and_admins_read_it()
    {
        var shop = await ShopAsync("Audit Arcade");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(10), till: shop.Till);
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift/cash", new CashMovementRequest("nosale", 0, null), till: shop.Till);

        var auditor = await app.CreateUserAsync("Ari Auditor", "ari.auditor@example.test");
        await SendAsync<PosStaffDto>(shop.Owner, HttpMethod.Post, $"{Base}/merchants/{shop.Merchant.Id}/staff", new AddStaffRequest("ari.auditor@example.test", PosRoles.Auditor, null));
        var check = await SendAsync<PosAuditCheckDto>(auditor, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit/verify");
        Assert.True(check.Intact);
        Assert.True(check.Entries >= 4);

        // An auditor can't use the till.
        await SendAsync<object>(auditor, HttpMethod.Put, $"{Base}/merchants/{shop.Merchant.Id}/pin", new SetPinRequest("8642"));
        using (var till = await RawAsync(auditor, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/till", new UnlockTillRequest("8642")))
            Assert.Contains("Auditors can't use the till", await ProblemAsync(till));

        // Someone edits an entry straight in the database: the chain breaks there.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
            await db.PosAudit.Where(a => a.MerchantId == shop.Merchant.Id && a.Action == "drawer.nosale")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Details, "nothing to see"));
        }
        var broken = await SendAsync<PosAuditCheckDto>(auditor, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit/verify");
        var nosale = (await SendAsync<List<PosAuditDto>>(auditor, HttpMethod.Get, $"{Base}/merchants/{shop.Merchant.Id}/audit")).Single(a => a.Action == "drawer.nosale");
        Assert.Equal((false, nosale.Sequence), (broken.Intact, broken.BrokenAt));
    }

    [Fact]
    public async Task A_receipt_goes_to_the_customers_Ndeipi_chats()
    {
        var shop = await ShopAsync("Receipt Road");
        var customer = await app.CreateUserAsync("Chipo Customer", "chipo.customer@example.test");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(0), till: shop.Till);
        var sale = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 2, null)]), till: shop.Till);

        await SendAsync<object>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales/{sale.Id}/receipt", new SendReceiptRequest("Chipo.Customer@example.test"), till: shop.Till);
        var chats = await customer.GetAsync<List<ConversationDto>>("api/conversations");
        var chat = Assert.Single(chats, c => c.Members.Any(m => m.Id == shop.Owner.Id));
        var messages = await customer.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages");

        // A receipt card, filled in by the server from the sale itself.
        var message = Assert.Single(messages, m => m.Kind == MessageKinds.PosReceipt);
        var receipt = ContractJson.Read<PosReceiptPayload>(message.Payload)!;
        Assert.Equal((sale.Id, "Receipt Road", sale.ReceiptNumber, 1.73m), (receipt.SaleId, receipt.MerchantName, receipt.ReceiptNumber, receipt.Total));
        Assert.Equal("Cola", Assert.Single(receipt.Lines!).Name);
        Assert.Contains(receipt.Payments!, p => p.Tender == "Cash" && p.Amount == 2);

        using var nobody = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales/{sale.Id}/receipt", new SendReceiptRequest("nobody@example.test"), till: shop.Till);
        Assert.Contains("Print the receipt instead", await ProblemAsync(nobody));
    }

    [Fact]
    public async Task Nobody_outside_the_shop_can_post_a_receipt_and_what_they_claim_is_ignored()
    {
        var shop = await ShopAsync("Forgery Fair");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(0), till: shop.Till);
        var sale = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 2, null)]), till: shop.Till);

        // A stranger who knows the sale's id tries to post "its" receipt, claiming a bigger total.
        var forger = await app.CreateUserAsync("Receipt Forger");
        var victim = await app.CreateUserAsync("Receipt Victim");
        var chat = await forger.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [victim.Id], null));
        var fake = ContractJson.ToElement(new PosReceiptPayload(sale.Id, "Forgery Fair", Total: 999));
        using (var refused = await forger.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages",
                   new SendMessageRequest(chat.Id, MessageKinds.PosReceipt, fake, Guid.NewGuid()), ContractJson.Options))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("Only the shop", await ProblemAsync(refused));
        }

        // The shop's own account sending it gets the real total, whatever the payload claims.
        var own = await shop.Owner.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Direct, [victim.Id], null));
        var sent = await shop.Owner.PostAsync<MessageDto>($"api/conversations/{own.Id}/messages", new SendMessageRequest(own.Id, MessageKinds.PosReceipt, fake, Guid.NewGuid()));
        Assert.Equal(1.73m, ContractJson.Read<PosReceiptPayload>(sent.Payload)!.Total);
    }

    [Fact]
    public async Task Counted_stock_never_goes_below_zero_and_uncounted_products_sell_freely()
    {
        var shop = await ShopAsync("Stock Stop", colaStock: 2);
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(0), till: shop.Till);

        // 2 colas in stock: 3 can't be sold, and nothing is taken or charged for trying.
        using (var three = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   Sale([Line(shop.Cola, 3)], [new TenderRequest(PosTenders.Cash, 10, null)]), till: shop.Till))
            Assert.Equal("Only 2 of Cola left in stock.", await ProblemAsync(three));
        await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Cola, 2)], [new TenderRequest(PosTenders.Cash, 5, null)]), till: shop.Till);
        using (var none = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   Sale([Line(shop.Cola)], [new TenderRequest(PosTenders.Cash, 2, null)]), till: shop.Till))
            Assert.Equal("Cola is out of stock.", await ProblemAsync(none));
        var shift = await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Get, $"{Base}/till/shift", till: shop.Till);
        Assert.Equal(1, shift.Totals.Sales);

        var cola = (await SendAsync<PosCatalogDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/catalog")).Products.Single(p => p.Id == shop.Cola.Id);
        Assert.Equal((0m, true), (cola.Stock[0].Quantity, cola.Stock[0].IsOut));

        // Taking stock out can't go below zero either.
        using (var takeOut = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/stock", new StockAdjustRequest(shop.Cola.Id, null, -1, "Broken")))
            Assert.Contains("are none of Cola in stock", await ProblemAsync(takeOut));

        // The database holds the line too, whatever the code does.
        var check = await Assert.ThrowsAnyAsync<Exception>(() => app.DbAsync(db =>
            db.PosStock.Where(s => s.ProductId == shop.Cola.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, -1))));
        Assert.Contains("CK_PosStock_NotNegative", check.Message);

        // Burgers aren't counted: they sell with no stock, and have none to adjust.
        await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            Sale([Line(shop.Burger, 5)], [new TenderRequest(PosTenders.Cash, 30, null)]), till: shop.Till);
        using (var adjust = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/stores/{shop.StoreId}/stock", new StockAdjustRequest(shop.Burger.Id, null, 5, "Delivery")))
            Assert.Contains("isn't counted", await ProblemAsync(adjust));
        var burger = (await SendAsync<PosCatalogDto>(shop.Owner, HttpMethod.Get, $"{Base}/stores/{shop.StoreId}/catalog")).Products.Single(p => p.Id == shop.Burger.Id);
        Assert.Equal((false, 0), (burger.TrackStock, burger.Stock.Count));
    }

    // ---- Ndeipi Pay ----

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    Task VerifyBankingAsync(TestUser user) => app.DbAsync(db =>
    {
        db.BankingProfiles.Add(new BankingProfile
        {
            UserId = user.Id,
            BridgeCustomerId = NewId("cust"),
            KycLinkId = NewId("kyc"),
            KycStatus = BankingProfile.Approved,
            TosStatus = BankingProfile.Approved,
            WalletId = NewId("wal"),
            WalletChain = "solana",
            WalletAddress = NewId("addr"),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return db.SaveChangesAsync();
    });

    async Task<HttpResponseMessage> DeliverTransferWebhookAsync(string bridgeTransferId, string state)
    {
        var body = System.Text.Json.JsonSerializer.Serialize(new
        {
            event_id = NewId("wh"),
            event_category = "transfer",
            event_type = "transfer.updated.status_transitioned",
            event_object_id = bridgeTransferId,
            event_object_status = state,
            event_object = new { id = bridgeTransferId, state }
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signature = Convert.ToBase64String(TestApp.WebhookKey.SignData(
            System.Text.Encoding.UTF8.GetBytes($"{timestamp}.{body}"), System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1));
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/bridge") { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Webhook-Signature", $"t={timestamp},v0={signature}");
        return await app.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task A_customer_pays_by_scanning_and_the_sale_completes_with_their_receipt_in_their_chats()
    {
        var shop = await ShopAsync("Scan Store");
        var customer = await app.CreateUserAsync("Nyasha Scanner");
        await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/shift", new OpenShiftRequest(0), till: shop.Till);
        var cart = Sale([Line(shop.Cola, 2)], []); // 3.45

        // The shop's owner takes the money, so they need a verified wallet first.
        using (var unverified = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(3.45m), till: shop.Till))
            Assert.Contains("needs a verified wallet", await ProblemAsync(unverified));
        await VerifyBankingAsync(shop.Owner);

        var qr = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(3.45m), till: shop.Till);
        Assert.Equal((PosQrStatuses.Waiting, "usdc"), (qr.Status, qr.Currency));
        Assert.Equal(qr.Code, PosPay.CodeFrom(qr.PayUrl));

        // Not paid yet: the sale can't use it.
        using (var early = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   cart with { Tenders = [new TenderRequest(PosTenders.Qr, 3.45m, qr.Id.ToString())] }, till: shop.Till))
            Assert.Contains("hasn't come through", await ProblemAsync(early));

        // The customer scans: they see who's asking for what. They need a verified wallet to pay.
        var request = await customer.GetAsync<PayRequestDto>($"{PosPay.BasePath}/{qr.Code}");
        Assert.Equal(("Scan Store", 3.45m, PosQrStatuses.Waiting, false), (request.MerchantName, request.Amount, request.Status, request.IsMine));
        using (var notVerified = await customer.Http.PostAsync($"{PosPay.BasePath}/{qr.Code}", null))
            Assert.Contains("Verify your identity", await ProblemAsync(notVerified));
        await VerifyBankingAsync(customer);

        var bridgeTransferId = NewId("tr");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = bridgeTransferId, state = "awaiting_funds" });
        using (var paying = await customer.Http.PostAsync($"{PosPay.BasePath}/{qr.Code}", null))
        {
            Assert.Equal(HttpStatusCode.OK, paying.StatusCode);
            var confirmed = (await paying.Content.ReadFromJsonAsync<PayRequestDto>(ContractJson.Options))!;
            Assert.Equal((PosQrStatuses.Paying, true), (confirmed.Status, confirmed.IsMine));
        }
        var transfer = await app.DbAsync(db => db.BankTransfers.AsNoTracking().SingleAsync(t => t.BridgeTransferId == bridgeTransferId));
        Assert.Equal((customer.Id, shop.Owner.Id, 3.45m, "usdc"), (transfer.SenderId, transfer.RecipientId, transfer.Amount, transfer.Currency));

        // Someone else scanning the same code can't pay it again.
        var other = await app.CreateUserAsync("Second Scanner");
        await VerifyBankingAsync(other);
        using (var twice = await other.Http.PostAsync($"{PosPay.BasePath}/{qr.Code}", null))
            Assert.Contains("already been used", await ProblemAsync(twice));

        // Bridge confirms: the till sees it paid, by whom.
        using (var webhook = await DeliverTransferWebhookAsync(bridgeTransferId, "payment_processed"))
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        var paid = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Get, $"{Base}/till/qr-payments/{qr.Id}", till: shop.Till);
        Assert.Equal((PosQrStatuses.Paid, "Nyasha Scanner"), (paid.Status, paid.PayerName));

        // The till completes the sale with it, and the customer gets the receipt without being asked.
        var sale = await SendAsync<PosSaleDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
            cart with { Tenders = [new TenderRequest(PosTenders.Qr, 3.45m, qr.Id.ToString())] }, till: shop.Till);
        Assert.Equal(PosTenders.Qr, Assert.Single(sale.Payments).Tender);
        var chat = Assert.Single(await customer.GetAsync<List<ConversationDto>>("api/conversations"), c => c.Members.Any(m => m.Id == shop.Owner.Id));
        var messages = await customer.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages");
        Assert.Equal(sale.ReceiptNumber, ContractJson.Read<PosReceiptPayload>(Assert.Single(messages, m => m.Kind == MessageKinds.PosReceipt).Payload)!.ReceiptNumber);
        var shift = await SendAsync<PosShiftDto>(shop.Owner, HttpMethod.Get, $"{Base}/till/shift", till: shop.Till);
        Assert.Equal(3.45m, shift.Totals.ByTender[PosTenders.Qr]);

        // One payment, one sale: it can't pay for another.
        using (var reuse = await RawAsync(shop.Owner, HttpMethod.Post, $"{Base}/till/sales",
                   Sale([Line(shop.Cola, 2)], [new TenderRequest(PosTenders.Qr, 3.45m, qr.Id.ToString())]), till: shop.Till))
            Assert.Contains("already paid for a sale", await ProblemAsync(reuse));
    }

    [Fact]
    public async Task A_QR_code_can_be_cancelled_expires_and_a_failed_payment_is_not_paid()
    {
        var shop = await ShopAsync("Cancel Corner");
        var customer = await app.CreateUserAsync("Tatenda Declined");
        await VerifyBankingAsync(shop.Owner);
        await VerifyBankingAsync(customer);

        // Cancelled on the till: nobody can pay it any more.
        var cancelled = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(2m), till: shop.Till);
        Assert.Equal(PosQrStatuses.Cancelled, (await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Delete, $"{Base}/till/qr-payments/{cancelled.Id}", till: shop.Till)).Status);
        using (var late = await customer.Http.PostAsync($"{PosPay.BasePath}/{cancelled.Code}", null))
            Assert.Contains("already been used", await ProblemAsync(late));

        // Past its time, it's expired.
        var old = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(2m), till: shop.Till);
        await app.DbAsync(db => db.PosQrPayments.Where(p => p.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(PosQrStatuses.Expired, (await customer.GetAsync<PayRequestDto>($"{PosPay.BasePath}/{old.Code}")).Status);
        using (var expired = await customer.Http.PostAsync($"{PosPay.BasePath}/{old.Code}", null))
            Assert.Contains("expired", await ProblemAsync(expired));

        // Bridge turns the transfer down: the payment has failed, and the till knows.
        var qr = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(2m), till: shop.Till);
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);
        using (var declined = await customer.Http.PostAsync($"{PosPay.BasePath}/{qr.Code}", null))
            Assert.Equal(PosQrStatuses.Failed, (await declined.Content.ReadFromJsonAsync<PayRequestDto>(ContractJson.Options))!.Status);
        var failed = await SendAsync<PosQrPaymentDto>(shop.Owner, HttpMethod.Get, $"{Base}/till/qr-payments/{qr.Id}", till: shop.Till);
        Assert.Equal(PosQrStatuses.Failed, failed.Status);
        Assert.NotNull(failed.Error);

        // Some shops can't take it at all: prices in a currency the wallet doesn't pay in.
        var zwg = await app.CreateUserAsync("Zig Owner", "zig.owner@example.test");
        var merchant = await SendAsync<PosMerchantDto>(zwg, HttpMethod.Post, $"{Base}/merchants", new CreateMerchantRequest("Zig Shop", "zwg", false, "Main", 15));
        await SendAsync<object>(zwg, HttpMethod.Put, $"{Base}/merchants/{merchant.Id}/pin", new SetPinRequest("7351"));
        var till = (await SendAsync<TillSessionDto>(zwg, HttpMethod.Post, $"{Base}/stores/{merchant.Stores[0].Id}/till", new UnlockTillRequest("7351"))).Token;
        using var wrongCurrency = await RawAsync(zwg, HttpMethod.Post, $"{Base}/till/qr-payments", new StartQrPaymentRequest(2m), till: till);
        Assert.Contains("this shop sells in ZWG", await ProblemAsync(wrongCurrency));
    }
}
