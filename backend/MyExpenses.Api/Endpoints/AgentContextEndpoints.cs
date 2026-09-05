using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class AgentContextEndpoints
{
    /// <summary>映射提供 AI agent 使用的唯讀日期與時區 context endpoint。</summary>
    public static void MapAgentContextEndpoints(this WebApplication app)
    {
        app.MapGet("/api/agent/context", (TimeZoneService timeZoneService) =>
            Results.Ok(new
            {
                currentDate = timeZoneService.GetLocalDate(),
                timeZoneId = timeZoneService.TimeZoneId,
            }))
            .RequireApiTokenScope(ApiTokenScopes.AgentContextRead);
    }
}
