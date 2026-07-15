using MyExpenses.Api.Data;
using MyExpenses.Api.Models.Requests;
using MyExpenses.Api.Services;

namespace MyExpenses.Api.Endpoints;

public static class SettingsEndpoints
{
    /// <summary>Maps authenticated system settings endpoints.</summary>
    public static void MapSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/settings").RequireAuthorization();

        group.MapGet("/timezone", (TimeZoneService timeZoneService) =>
            Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId }));
        group.MapGet("/time-zone", (TimeZoneService timeZoneService) =>
            Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId }));

        group.MapPut("/timezone", UpdateTimeZoneAsync);
        group.MapPut("/time-zone", UpdateTimeZoneAsync);
    }

    /// <summary>Validates, persists, and activates a new system time zone.</summary>
    private static async Task<IResult> UpdateTimeZoneAsync(
        UpdateTimeZoneRequest request,
        AppDbContext db,
        TimeZoneService timeZoneService,
        CancellationToken cancellationToken)
    {
        var updated = await timeZoneService.UpdateAsync(db, request.TimeZoneId, cancellationToken);
        return updated
            ? Results.Ok(new { timeZoneId = timeZoneService.TimeZoneId })
            : Results.BadRequest(new { message = "Unsupported system time zone" });
    }
}
