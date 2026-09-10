namespace MultiSeat.Service;

/// <summary>
/// A request cannot currently be satisfied because server capacity is exhausted — the seat limit
/// is reached, or no port block is free. The API maps this to <b>503 Service Unavailable</b>: the
/// condition is temporary and the same request may succeed later, unlike a conflict with a
/// specific existing resource.
///
/// ⭐ All three types here derive from <see cref="InvalidOperationException"/> deliberately. Every
/// existing non-HTTP caller — worker autostart, the smoke script, tooling — already catches that,
/// and keeps catching these unchanged. The HTTP layer gets more precision without anything else
/// having to learn about it.
/// </summary>
internal sealed class CapacityExhaustedException : InvalidOperationException
{
    public CapacityExhaustedException(string message) : base(message) { }
}

/// <summary>
/// A request conflicts with current server state — the account already has a live seat, the
/// Windows account already exists, or the seat is in a status that forbids the operation. The API
/// maps this to <b>409 Conflict</b>: repeating the identical request cannot succeed until
/// something changes.
///
/// ⚠️ Distinct from <see cref="CapacityExhaustedException"/> on purpose. Both used to surface as
/// 400, which told a caller "your request was malformed" when the request was fine and the server
/// was full — and gave no way to tell a retryable condition from a permanent one.
/// </summary>
internal sealed class ResourceConflictException : InvalidOperationException
{
    public ResourceConflictException(string message) : base(message) { }
}

/// <summary>
/// The seat named by the request does not exist, or stopped existing while the operation waited
/// for the per-seat lifecycle gate. The API maps this to <b>404 Not Found</b>.
///
/// ⚠️ The second case is the one worth knowing about. After the PR C guards, a lifecycle
/// operation can be admitted through the gate only to find the seat torn down while it waited.
/// That is not a bad request and never was — the seat was real when the caller asked.
/// </summary>
internal sealed class SeatNotFoundException : InvalidOperationException
{
    public SeatNotFoundException(string message = "Seat not found.") : base(message) { }
}

/// <summary>
/// Maps the exception types above onto HTTP results, so every endpoint answers the same way and a
/// new endpoint cannot quietly disagree with the others.
/// </summary>
internal static class ApiErrors
{
    /// <summary>
    /// ⚠️ Order matters: all three types derive from <see cref="InvalidOperationException"/>, so
    /// the specific cases must be tested before the fallback. A plain InvalidOperationException
    /// still means 400 — a genuinely bad request, such as an unusable resolution.
    /// </summary>
    public static IResult ToResult(InvalidOperationException ex) => ex switch
    {
        SeatNotFoundException      => Results.NotFound(new { error = ex.Message }),
        ResourceConflictException  => Results.Conflict(new { error = ex.Message }),
        CapacityExhaustedException => Results.Json(new { error = ex.Message }, statusCode: 503),
        _                          => Results.BadRequest(new { error = ex.Message })
    };

    /// <summary>The status <see cref="ToResult"/> would produce. Exists so tests can assert the
    /// mapping without standing up the HTTP pipeline.</summary>
    public static int StatusFor(InvalidOperationException ex) => ex switch
    {
        SeatNotFoundException      => StatusCodes.Status404NotFound,
        ResourceConflictException  => StatusCodes.Status409Conflict,
        CapacityExhaustedException => StatusCodes.Status503ServiceUnavailable,
        _                          => StatusCodes.Status400BadRequest
    };
}
