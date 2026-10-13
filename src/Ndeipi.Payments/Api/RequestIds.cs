namespace Ndeipi.Payments.Api;

/// <summary>
/// Every response carries <c>Request-Id</c>, the same ID the audit log and ledger postings record,
/// so an integrator's report can be traced to one server record (NFR-OBS-01, SRV-LED-06).
/// </summary>
public static class RequestIds
{
    public const string Header = "Request-Id";
    const string Key = "payments.request_id";

    public static string Get(HttpContext http) => http.Items[Key] as string ?? "";

    public static void UseRequestIds(this WebApplication app) => app.Use((http, next) =>
    {
        var id = Ids.New(Ids.Request, http.RequestServices.GetRequiredService<TimeProvider>());
        http.Items[Key] = id;
        http.Response.Headers[Header] = id;
        return next(http);
    });
}
