namespace MyOwnLLM.Gateway.Auth;

/// <summary>Accepts only <c>Authorization: Bearer &lt;key&gt;</c> with a known key.</summary>
public sealed class ApiKeyFilter(ApiKeyStore store) : IEndpointFilter
{
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var header = context.HttpContext.Request.Headers.Authorization.ToString();
        var key = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : null;
        return store.IsValid(key) ? next(context) : ValueTask.FromResult<object?>(Results.Unauthorized());
    }
}
