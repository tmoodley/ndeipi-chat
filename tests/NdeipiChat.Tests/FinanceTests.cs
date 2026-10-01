using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NdeipiChat.Api.Finance;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// The Absa gold mining loan against its application screens: eligibility (FR-ELG), the group (FR-GRP),
/// what it finances (FR-LTY, FR-TRM), documents (FR-DOC), submitting (FR-SUB), and the committee
/// workflow through to collection at Absa (FR-WF, FR-DSB), followed live (FR-NTF).
/// </summary>
public sealed class FinanceTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = FinanceContract.BasePath;

    static readonly Guid JawCrusher = FinanceModel.Equipment[0].Id;
    static readonly Guid GoldKacha = FinanceModel.Equipment[2].Id;

    static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% test document\n");

    static string Unique(string prefix) => $"{prefix} {Guid.NewGuid().ToString("N")[..6]}";

    Task<TestUser> AdminAsync(string name = "Finance Admin") => app.CreateUserAsync(name, roles: [FinanceContract.AdminRole]);

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    static EligibilityDto Eligible => new(true, RegistrationBodies.Cooperatives, LicenceTypes.Artisanal, true);

    /// <summary>A complete application from a village in its own ward, so each test's committees cover only its own.</summary>
    static SaveLoanApplicationRequest Complete(string ward, string village, string? registration = null) => new(
        Eligible, "Kasenengwa Mining Cooperative", "cooperative", registration ?? Unique("COOP"),
        "Eastern", "Kasenengwa", ward, village, ClusterTypes.Processing,
        [new LoanItemRequest(GoldKacha, Buy: false), new LoanItemRequest(JawCrusher, Buy: true)], 3);

    static async Task<LoanApplicationDto> UploadAsync(TestUser user, Guid id, string kind, byte[] bytes, string name = "doc.pdf")
    {
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        form.Add(part, "file", name);
        using var response = await user.Http.PostAsync($"{Base}/applications/{id}/documents/{kind}", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<LoanApplicationDto>(ContractJson.Options))!;
    }

    async Task<LoanApplicationDto> ReadyAsync(TestUser applicant, SaveLoanApplicationRequest request)
    {
        var draft = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications", request);
        foreach (var (kind, _, _) in LoanDocumentKinds.All)
            await UploadAsync(applicant, draft.Id, kind, Pdf, $"{kind}.pdf");
        return draft;
    }

    async Task<LoanApplicationDto> SubmittedAsync(TestUser applicant, SaveLoanApplicationRequest request)
    {
        var draft = await ReadyAsync(applicant, request);
        return await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications/{draft.Id}/submit", new SubmitLoanRequest(true));
    }

    /// <summary>A committee for every stage covering a ward, each with its own member.</summary>
    async Task<Dictionary<string, TestUser>> CommitteesAsync(TestUser admin, string ward)
    {
        var members = new Dictionary<string, TestUser>();
        foreach (var stage in LoanStages.Order)
        {
            var member = await app.CreateUserAsync($"{stage} member");
            members[stage] = member;
            await admin.PostAsync<LoanCommitteeDto>($"{Base}/committees",
                new SaveCommitteeRequest(stage, $"{ward} {LoanStages.Label(stage)}", "Eastern", "Kasenengwa", ward, null, [member.Id]));
        }
        return members;
    }

    [Fact]
    public void All_four_criteria_must_be_met()
    {
        Assert.Empty(Eligible.Problems());
        Assert.Equal(4, new EligibilityDto(null, null, null, null).Problems().Count);
        Assert.Contains("bank account", Assert.Single((Eligible with { HasBankAccount = false }).Problems()));
        Assert.Contains("Zambians", Assert.Single((Eligible with { ZambianOwned = false }).Problems()));
        Assert.Single((Eligible with { RegistrationBody = "other" }).Problems());
        Assert.Single((Eligible with { LicenceType = "large_scale" }).Problems());
    }

    [Fact]
    public async Task The_estimate_is_equipment_hired_for_the_months_or_bought_plus_running_costs_and_prices_to_confirm_are_left_out()
    {
        var admin = await AdminAsync();
        var applicant = await app.CreateUserAsync("Estimating Applicant");

        // Prices are to be confirmed until an admin sets them.
        var tbc = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications", Complete(Unique("Ward"), "Mtenguleni"));
        Assert.Equal((0m, false), (tbc.Estimate.Total, tbc.Estimate.Complete));

        await admin.Http.PutAsJsonAsync($"{Base}/equipment/{GoldKacha}", new SaveEquipmentRequest("Gold kacha", "Concentrator", ClusterTypes.Processing, "MTP (TBC)", 2000m, 45000m, 500m, true, 3), ContractJson.Options);
        await admin.Http.PutAsJsonAsync($"{Base}/equipment/{JawCrusher}", new SaveEquipmentRequest("Jaw crusher", "Crushing", ClusterTypes.Processing, "MTP 1", 1500m, 30000m, 400m, true, 1), ContractJson.Options);

        var priced = await applicant.Http.PutAsJsonAsync($"{Base}/applications/{tbc.Id}", Complete(Unique("Ward"), "Mtenguleni"), ContractJson.Options);
        var a = (await priced.Content.ReadFromJsonAsync<LoanApplicationDto>(ContractJson.Options))!;
        // Jaw crusher bought (30,000), gold kacha hired 3 × 2,000, running costs (400 + 500) × 3.
        Assert.Equal(["Gold kacha hire × 3 months", "Jaw crusher (buy)", "Running costs × 3 months"], a.Estimate.Lines.Select(l => l.Label));
        Assert.Equal((30000m + 6000m + 2700m, true, "ZMW"), (a.Estimate.Total, a.Estimate.Complete, a.Estimate.Currency));

        // Only an admin sets prices.
        using var refused = await applicant.Http.PutAsJsonAsync($"{Base}/equipment/{GoldKacha}", new SaveEquipmentRequest("Gold kacha", "", ClusterTypes.Processing, null, 1m, 1m, 1m, true, 3), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Submitting_needs_eligibility_the_full_form_all_four_documents_and_the_declaration()
    {
        var applicant = await app.CreateUserAsync("Careful Applicant");
        var ward = Unique("Ward");

        var ineligible = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications", Complete(ward, "Chiparamba") with { Eligibility = Eligible with { HasBankAccount = false } });
        using (var r = await applicant.Http.PostAsJsonAsync($"{Base}/applications/{ineligible.Id}/submit", new SubmitLoanRequest(true), ContractJson.Options))
            Assert.Contains("bank account", await ProblemAsync(r));

        var draft = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications", Complete(ward, "Chiparamba"));
        Assert.Equal((LoanStatuses.Draft, true), (draft.Status, draft.CanEdit));
        Assert.StartsWith("GL-", draft.Reference);
        using (var r = await applicant.Http.PostAsJsonAsync($"{Base}/applications/{draft.Id}/submit", new SubmitLoanRequest(true), ContractJson.Options))
            Assert.Contains("registration certificate", await ProblemAsync(r));

        // Only photos and PDFs, by their content rather than their name.
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes("MZ not really a pdf")), "file", "licence.pdf" } })
        using (var r = await applicant.Http.PostAsync($"{Base}/applications/{draft.Id}/documents/{LoanDocumentKinds.Licence}", form))
            Assert.Contains("photo", await ProblemAsync(r));

        foreach (var (kind, _, _) in LoanDocumentKinds.All)
            draft = await UploadAsync(applicant, draft.Id, kind, Pdf, $"{kind}.pdf");
        Assert.All(draft.Documents, d => Assert.NotNull(d.FileName));

        using (var r = await applicant.Http.PostAsJsonAsync($"{Base}/applications/{draft.Id}/submit", new SubmitLoanRequest(false), ContractJson.Options))
            Assert.Contains("Confirm", await ProblemAsync(r));

        var submitted = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications/{draft.Id}/submit", new SubmitLoanRequest(true));
        Assert.Equal((LoanStatuses.InReview, LoanStages.Village, false), (submitted.Status, submitted.Stage, submitted.CanEdit));
        Assert.Equal("current", submitted.Stages[0].State);

        // Now it's with the committees: it can't be changed, and the group can't apply twice at once.
        using (var r = await applicant.Http.PutAsJsonAsync($"{Base}/applications/{draft.Id}", Complete(ward, "Chiparamba"), ContractJson.Options))
            Assert.Contains("can't be changed", await ProblemAsync(r));
        var again = await ReadyAsync(applicant, Complete(ward, "Chiparamba", submitted.RegistrationNumber));
        using (var r = await applicant.Http.PostAsJsonAsync($"{Base}/applications/{again.Id}/submit", new SubmitLoanRequest(true), ContractJson.Options))
            Assert.Contains("already has an application", await ProblemAsync(r));
    }

    [Fact]
    public async Task An_application_goes_through_each_committee_for_its_area_then_is_collected_at_Absa()
    {
        var admin = await AdminAsync();
        var ward = Unique("Ward");
        var members = await CommitteesAsync(admin, ward);
        var applicant = await app.CreateUserAsync("Mining Cooperative Chair");

        var route = await applicant.GetAsync<LoanRouteDto>($"{Base}/route?province=Eastern&constituency=Kasenengwa&ward={Uri.EscapeDataString(ward)}&village=Mtenguleni");
        Assert.Equal($"{ward} Village Productivity Committee", route.Stages[0].Committee);

        var a = await SubmittedAsync(applicant, Complete(ward, "Mtenguleni"));
        var outsider = await app.CreateUserAsync("Not On Any Committee");
        using (var hidden = await outsider.Http.GetAsync($"{Base}/applications/{a.Id}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        // Live: the applicant follows each decision.
        await using var hub = await app.ConnectAsync(applicant);
        await hub.InvokeAsync(ChatHubContract.Subscribe, FinanceContract.ApplicationTopic(a.Id));
        var approvedAtVillage = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage),
            m => m.Topic == FinanceContract.ApplicationTopic(a.Id) && m.Payload.GetProperty("stage").GetString() == LoanStages.Ward);

        foreach (var stage in LoanStages.Order.SkipLast(1))
        {
            // Only the committee for the current stage decides; the next one waits its turn.
            var next = LoanStages.Next(stage)!;
            Assert.Empty(await members[next].GetAsync<List<LoanSummaryDto>>($"{Base}/reviews"));
            using (var early = await members[next].Http.PostAsJsonAsync($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, null), ContractJson.Options))
                Assert.Contains("isn't waiting on you", await ProblemAsync(early));

            var queue = await members[stage].GetAsync<List<LoanSummaryDto>>($"{Base}/reviews");
            Assert.Equal(a.Id, Assert.Single(queue).Id);
            Assert.True((await members[stage].GetAsync<FinanceMeDto>($"{Base}/me")).ToReview >= 1);
            a = await members[stage].PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, "Looks good."));
            Assert.Equal(next, a.Stage);
        }
        Assert.Equal(LoanStages.Ward, ContractJson.Read<LoanChangedDto>((await approvedAtVillage).Payload)!.Stage);
        Assert.Equal(LoanStatuses.Approved, a.Status);

        // At Absa: collected, with Absa's reference.
        using (var noReference = await members[LoanStages.Absa].Http.PostAsJsonAsync($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, null), ContractJson.Options))
            Assert.Contains("reference", await ProblemAsync(noReference));
        a = await members[LoanStages.Absa].PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, null, "ABSA-LN-0042"));

        var seen = await applicant.GetAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}");
        Assert.Equal((LoanStatuses.Collected, "ABSA-LN-0042"), (seen.Status, seen.CollectionReference));
        Assert.All(seen.Stages, s => Assert.Equal("done", s.State));
        Assert.Equal(($"{ward} Ward Development Committee", "ward member", "Looks good."), (seen.Stages[1].Committee, seen.Stages[1].DecidedBy, seen.Stages[1].Comment));
    }

    [Fact]
    public async Task Sent_back_it_returns_to_the_same_committee_once_changed_and_declined_it_ends()
    {
        var admin = await AdminAsync();
        var ward = Unique("Ward");
        var members = await CommitteesAsync(admin, ward);
        var applicant = await app.CreateUserAsync("Returned Applicant");

        var a = await SubmittedAsync(applicant, Complete(ward, "Chimtende"));
        a = await members[LoanStages.Village].PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, null));
        using (var noReason = await members[LoanStages.Ward].Http.PostAsJsonAsync($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Return, null), ContractJson.Options))
            Assert.Contains("change", await ProblemAsync(noReason));
        await members[LoanStages.Ward].PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Return, "The proposal needs the licence number."));

        var mine = await applicant.GetAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}");
        Assert.Equal((LoanStatuses.Returned, LoanStages.Ward, true, "The proposal needs the licence number."), (mine.Status, mine.Stage, mine.CanEdit, mine.ReturnedComment));
        Assert.Equal(["done", "returned", "waiting"], mine.Stages.Take(3).Select(s => s.State));

        await UploadAsync(applicant, a.Id, LoanDocumentKinds.Proposal, Pdf, "proposal-v2.pdf");
        var resubmitted = await applicant.PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/submit", new SubmitLoanRequest(true));
        Assert.Equal((LoanStatuses.InReview, LoanStages.Ward), (resubmitted.Status, resubmitted.Stage));

        await members[LoanStages.Ward].PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Decline, "The licence has expired."));
        var declined = await applicant.GetAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}");
        Assert.Equal((LoanStatuses.Declined, "declined", "The licence has expired."), (declined.Status, declined.Stages[1].State, declined.Stages[1].Comment));
        Assert.Empty(await members[LoanStages.Ward].GetAsync<List<LoanSummaryDto>>($"{Base}/reviews"));
    }

    [Fact]
    public async Task The_most_specific_committee_handles_it_and_where_there_is_none_an_admin_can()
    {
        var admin = await AdminAsync("Area Admin");
        var ward = Unique("Ward");
        var provinceWide = await app.CreateUserAsync("Province-wide Member");
        var local = await app.CreateUserAsync("Village Member");
        await admin.PostAsync<LoanCommitteeDto>($"{Base}/committees", new SaveCommitteeRequest(LoanStages.Village, "Eastern catch-all", "Eastern", null, null, null, [provinceWide.Id]));
        await admin.PostAsync<LoanCommitteeDto>($"{Base}/committees", new SaveCommitteeRequest(LoanStages.Village, "Kalichero VPC", "Eastern", "Kasenengwa", ward, "kalichero", [local.Id]));

        var applicant = await app.CreateUserAsync("Kalichero Applicant");
        var a = await SubmittedAsync(applicant, Complete(ward, "Kalichero"));
        Assert.Equal("Kalichero VPC", a.Stages[0].Committee);
        Assert.DoesNotContain(await provinceWide.GetAsync<List<LoanSummaryDto>>($"{Base}/reviews"), s => s.Id == a.Id);
        a = await local.PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, null));

        // No ward committee for this area yet: it waits for an admin, who can act.
        Assert.Null(a.Stages[1].Committee);
        Assert.Contains(await admin.GetAsync<List<LoanSummaryDto>>($"{Base}/reviews"), s => s.Id == a.Id);
        a = await admin.PostAsync<LoanApplicationDto>($"{Base}/applications/{a.Id}/decisions", new LoanDecisionRequest(LoanDecisions.Approve, "No ward committee yet; approved centrally."));
        Assert.Equal(LoanStages.Chief, a.Stage);

        // Only admins manage committees.
        using var refused = await local.Http.PostAsJsonAsync($"{Base}/committees", new SaveCommitteeRequest(LoanStages.Ward, "Mine", "Eastern", null, null, null, []), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task Documents_open_through_short_lived_links_only_for_those_who_may_see_the_application()
    {
        var admin = await AdminAsync();
        var ward = Unique("Ward");
        var members = await CommitteesAsync(admin, ward);
        var applicant = await app.CreateUserAsync("Document Applicant");
        var a = await SubmittedAsync(applicant, Complete(ward, "Makungwa"));

        var link = await members[LoanStages.Village].PostAsync<LoanDocumentLinkDto>($"{Base}/applications/{a.Id}/documents/{LoanDocumentKinds.Licence}/link", new { });
        using (var file = await app.CreateClient().GetAsync(new Uri(link.Url).PathAndQuery))
        {
            Assert.Equal(HttpStatusCode.OK, file.StatusCode);
            Assert.Equal("application/pdf", file.Content.Headers.ContentType?.MediaType);
            Assert.Equal(Pdf, await file.Content.ReadAsByteArrayAsync());
        }

        using (var forged = await app.CreateClient().GetAsync($"{Base}/files/not-a-real-token"))
            Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
        var outsider = await app.CreateUserAsync("Nosy Neighbour");
        using (var refused = await outsider.Http.PostAsJsonAsync($"{Base}/applications/{a.Id}/documents/{LoanDocumentKinds.Licence}/link", new { }, ContractJson.Options))
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
    }
}
