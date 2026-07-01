namespace MyExpenses.Api.Services;

public static class ApiTokenScopeEndpointConventionBuilderExtensions
{
    /// <summary>Adds API token scope metadata to an endpoint route builder.</summary>
    public static TBuilder RequireApiTokenScope<TBuilder>(this TBuilder builder, string requiredScope)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new ApiTokenScopeMetadata(requiredScope));
        return builder;
    }
}
