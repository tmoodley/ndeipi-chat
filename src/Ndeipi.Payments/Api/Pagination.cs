using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

namespace Ndeipi.Payments.Api;

/// <summary>A page of a list: newest first, with whether another page exists in that direction (FR-API-04).</summary>
public sealed record Page<T>(IReadOnlyList<T> Data, bool HasMore);

public sealed record PageRequest(int? Limit, string? StartingAfter, string? EndingBefore)
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 100;

    public static PageRequest From(HttpRequest request) => new(
        int.TryParse(request.Query["limit"], out var limit) ? limit : request.Query.ContainsKey("limit") ? -1 : null,
        NullIfEmpty(request.Query["starting_after"]),
        NullIfEmpty(request.Query["ending_before"]));

    static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}

public static class Pagination
{
    /// <summary>
    /// Pages a query by its ID. IDs are ULIDs, so ID order is creation order and the cursor is just
    /// an ID: <c>starting_after</c> gives older objects, <c>ending_before</c> newer ones.
    /// </summary>
    public static async Task<Page<TOut>> PageAsync<T, TOut>(
        this IQueryable<T> query, Expression<Func<T, string>> id, PageRequest page, Func<T, TOut> map, CancellationToken ct)
    {
        var limit = page.Limit ?? PageRequest.DefaultLimit;
        if (limit is < 1 or > PageRequest.MaxLimit)
            throw PaymentsException.BadRequest($"limit must be between 1 and {PageRequest.MaxLimit}.", "limit");
        if (page.StartingAfter is not null && page.EndingBefore is not null)
            throw PaymentsException.BadRequest("Send starting_after or ending_before, not both.", "ending_before");

        if (page.EndingBefore is { } before)
        {
            var newer = await query.Where(Compare(id, before, greater: true)).OrderBy(id).Take(limit + 1).ToListAsync(ct);
            return new Page<TOut>([.. newer.Take(limit).Reverse().Select(map)], newer.Count > limit);
        }

        if (page.StartingAfter is { } after)
            query = query.Where(Compare(id, after, greater: false));
        var rows = await query.OrderByDescending(id).Take(limit + 1).ToListAsync(ct);
        return new Page<TOut>([.. rows.Take(limit).Select(map)], rows.Count > limit);
    }

    /// <summary><c>x =&gt; string.Compare(id(x), cursor) &gt; 0</c> (or &lt; 0), which EF translates to a plain comparison.</summary>
    static Expression<Func<T, bool>> Compare<T>(Expression<Func<T, string>> id, string cursor, bool greater)
    {
        var compare = Expression.Call(typeof(string).GetMethod(nameof(string.Compare), [typeof(string), typeof(string)])!, id.Body, Expression.Constant(cursor));
        var zero = Expression.Constant(0);
        return Expression.Lambda<Func<T, bool>>(greater ? Expression.GreaterThan(compare, zero) : Expression.LessThan(compare, zero), id.Parameters);
    }
}
