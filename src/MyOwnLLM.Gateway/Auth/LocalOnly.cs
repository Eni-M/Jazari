namespace MyOwnLLM.Gateway.Auth;

/// <summary>
/// Turns away web pages. A page can point its own name at 127.0.0.1 (DNS rebinding) and reach this port, but then its
/// Host is that name; and a browser always says which page sent a request in Origin. The CLI, the eval scripts and IDE
/// extensions send a local Host and no Origin, or a local one.
/// </summary>
public static class LocalOnly
{
    public static bool Allows(HttpRequest request) =>
        IsLocal(request.Host.Host)
        && (request.Headers.Origin.FirstOrDefault() is not { } origin || Uri.TryCreate(origin, UriKind.Absolute, out var from) && IsLocal(from.Host));

    static bool IsLocal(string host) => host is "localhost" or "127.0.0.1" or "[::1]";
}
