namespace MultiSeat.Service.Api;

/// <summary>
/// Whether API key authentication is on, and the key it enforces. Both are read by the auth
/// middleware on every request, so a dashboard toggle takes effect without a service restart.
///
/// ⛔ Authentication cannot be switched on without a key, and that is enforced here rather than
/// left to callers (issue #61). The middleware rejects a request when
/// <c>presented != ApiKey</c>, so enabling with an empty key rejects EVERYTHING — including the
/// POST that would turn it off again, which is itself gated. That is a hard lockout recoverable
/// only by editing a config file and restarting the service.
///
/// It was reachable: a host configured with <c>ApiKey = "disabled"</c> resolves to an empty key,
/// and the old <c>SetEnabled(true)</c> would happily enable authentication against it. So
/// <see cref="Enable"/> demands a key and sets it BEFORE flipping the flag, and there is no
/// longer a way to express "enabled with no key".
/// </summary>
public sealed class ApiAuthState
{
    private volatile bool _enabled;
    private volatile string _apiKey;

    public ApiAuthState(bool enabled, string apiKey)
    {
        _enabled = enabled;
        _apiKey = apiKey ?? string.Empty;
    }

    public bool IsEnabled => _enabled;

    /// <summary>The key the middleware enforces. Changes only through <see cref="Enable"/>.</summary>
    public string ApiKey => _apiKey;

    /// <summary>
    /// Turn authentication on with <paramref name="apiKey"/>. The key is stored first, so no
    /// request can ever be checked against an empty key.
    /// </summary>
    public void Enable(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new ArgumentException(
                "Enabling API authentication requires a key. Enabling without one locks out every " +
                "caller, including the request that would turn it off again.", nameof(apiKey));

        _apiKey = apiKey;
        _enabled = true;
    }

    /// <summary>
    /// Turn authentication off. The key is kept so that turning it back on in the same process
    /// reuses it rather than inventing a second one.
    /// </summary>
    public void Disable() => _enabled = false;
}
