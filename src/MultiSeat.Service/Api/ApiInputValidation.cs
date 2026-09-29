using System.Text.RegularExpressions;

namespace MultiSeat.Service.Api;

/// <summary>
/// Input validation helpers for API endpoints.
/// Centralises rules so every endpoint uses the same constraints.
/// </summary>
internal static class ApiInputValidation
{
    // Windows usernames: start with alphanumeric, may contain . _ -
    // Max 20 chars to keep directory names sane.
    private static readonly Regex AccountNameRegex =
        new(@"^[a-zA-Z0-9][a-zA-Z0-9._\-]{0,19}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns true if the account name is safe to use in file paths and shell commands.
    /// Rejects traversal sequences, shell metacharacters, and names that are too long.
    /// </summary>
    public static bool IsValidAccountName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && AccountNameRegex.IsMatch(name);

    public static IResult AccountNameError() =>
        Results.BadRequest(new
        {
            error = "Invalid account name. Use 1–20 alphanumeric characters, dots, underscores, or hyphens. Must start with a letter or digit."
        });

    /// <summary>
    /// True when the password is present and not just whitespace. NetUserAdd accepts a blank
    /// password without complaint, so this is the only thing standing between a request and a
    /// seat account nobody can actually type a credential into (issue #76). Deliberately not a
    /// complexity check — length/character rules are a separate policy this codebase has never
    /// established, and adding one here would be out of scope for the bug being fixed.
    /// </summary>
    public static bool IsValidPassword(string? password) =>
        !string.IsNullOrWhiteSpace(password);

    public static IResult PasswordError() =>
        Results.BadRequest(new
        {
            error = "Password must not be empty or blank."
        });

    /// <summary>
    /// A 400 naming the allowed values when <paramref name="scale"/> is set and is not a scale
    /// factor RDP accepts; null when it is absent or allowed. An absent scale is valid — it
    /// means "no override".
    /// </summary>
    public static IResult? ScaleFactorError(int? scale) =>
        scale is { } value && !Sessions.RdpGeometry.IsAllowedScaleFactor(value)
            ? Results.BadRequest(new { error = Sessions.RdpGeometry.ScaleFactorError(value) })
            : null;
}
