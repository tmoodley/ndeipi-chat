using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;

namespace Ndeipi.Payments.Api;

/// <summary>
/// The contract's error body (FR-API-06): a stable code, a message, the offending field and the
/// request ID. Codes are the table on <c>Error.code</c> in openapi.yaml. Messages never carry the
/// API key or personal data (FR-API-07, NFR-SEC-03).
/// </summary>
public sealed record ApiError(
    string Code,
    string Message,
    string? Field = null,
    IReadOnlyList<ApiErrorDetail>? Details = null,
    string? ExistingId = null)
{
    public string RequestId { get; init; } = "";
}

public sealed record ApiErrorDetail(string Field, string Code, string Message);

/// <summary>Thrown anywhere in a request; <see cref="ApiErrors"/> turns it into the error body.</summary>
public sealed class PaymentsException(int status, ApiError error) : Exception(error.Message)
{
    public int Status { get; } = status;
    public ApiError Error { get; } = error;

    public static PaymentsException BadRequest(string message, string? field = null) => new(400, new("invalid_request", message, field));
    public static PaymentsException NotFound(string what) => new(404, new("not_found", $"No such {what}."));
    public static PaymentsException Conflict(string code, string message, string? existingId = null, string? field = null) =>
        new(409, new(code, message, field, ExistingId: existingId));
    public static PaymentsException Unprocessable(string code, string message, string? field = null) => new(422, new(code, message, field));
    public static PaymentsException Validation(IReadOnlyList<ApiErrorDetail> details) =>
        new(422, new("validation_failed", "One or more fields are invalid.", details.Count == 1 ? details[0].Field : null, details));
    public static PaymentsException Forbidden(string code, string message) => new(403, new(code, message));

    /// <summary>Operations mapped from the contract but not built yet; the milestone that builds them (SRS §10.1).</summary>
    public static PaymentsException NotImplemented(string milestone) =>
        new(501, new("not_implemented", $"This operation is part of milestone {milestone} and is not built yet."));
}

public static class ApiErrors
{
    public static Task WriteAsync(HttpContext http, int status, ApiError error)
    {
        http.Response.StatusCode = status;
        return http.Response.WriteAsJsonAsync(error with { RequestId = RequestIds.Get(http) }, PaymentsJson.Options, http.RequestAborted);
    }

    public static IResult Result(HttpContext http, int status, ApiError error) =>
        Results.Json(error with { RequestId = RequestIds.Get(http) }, PaymentsJson.Options, statusCode: status);

    /// <summary>
    /// Every exception leaves as the contract's error body: known ones with their status and code,
    /// malformed JSON as 400, anything else as 500 <c>internal_error</c> with no detail.
    /// </summary>
    public static void UsePaymentsErrors(this WebApplication app)
    {
        app.UseExceptionHandler(errors => errors.Run(http =>
        {
            var thrown = http.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, error) = thrown switch
            {
                PaymentsException e => (e.Status, e.Error),
                BadHttpRequestException { InnerException: JsonException } or JsonException =>
                    (400, new ApiError("invalid_request", "The request body is not valid JSON for this operation.")),
                BadHttpRequestException e => (e.StatusCode, new ApiError("invalid_request", "The request is malformed.")),
                _ => (500, new ApiError("internal_error", "Something went wrong on our side. Retry with the same idempotency key."))
            };
            return WriteAsync(http, status, error);
        }));

        // Unknown routes and methods get the same body as everything else.
        app.UseStatusCodePages(context =>
        {
            var http = context.HttpContext;
            if (http.Response.HasStarted || http.Response.ContentLength > 0 || http.Response.ContentType is not null)
                return Task.CompletedTask;
            return http.Response.StatusCode switch
            {
                404 => WriteAsync(http, 404, new("not_found", "No such resource.")),
                405 => WriteAsync(http, 405, new("invalid_request", "This resource does not support that method.")),
                _ => Task.CompletedTask
            };
        });
    }
}
