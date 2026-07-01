using Microsoft.AspNetCore.Authorization;

namespace MyExpenses.Api.Services;

public class ApiTokenScopeMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Initializes the middleware with the next request delegate.</summary>
    public ApiTokenScopeMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Rejects API token requests that lack required endpoint scope metadata or scope values.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (!ApiTokenAuthenticationFeature.IsAuthenticated(context))
        {
            await _next(context);
            return;
        }

        var endpoint = context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            await _next(context);
            return;
        }

        var scopeMetadata = endpoint.Metadata.GetMetadata<ApiTokenScopeMetadata>();
        if (scopeMetadata is null || !ApiTokenAuthenticationFeature.HasScope(context, scopeMetadata.RequiredScope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("API token scope is not permitted for this endpoint");
            return;
        }

        await _next(context);
    }
}
