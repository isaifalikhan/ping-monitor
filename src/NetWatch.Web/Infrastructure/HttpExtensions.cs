using System.Globalization;
using Microsoft.AspNetCore.Http;
using NetWatch.Data.Config;
using NetWatch.Web.Auth;

namespace NetWatch.Web.Infrastructure;

public static class HttpExtensions
{
    /// <summary>Audit actor for the current request.</summary>
    public static AuditActor Actor(this HttpContext http) =>
        new(http.User.Identity?.IsAuthenticated == true ? http.User.UserId() : null,
            http.User.Identity?.IsAuthenticated == true ? http.User.Username() : "anonymous",
            http.Connection.RemoteIpAddress?.ToString());

    public static Alerts.AlertActor AlertActor(this HttpContext http) => new(http.User.UserId(), http.User.Username());

    public static string Inv(this int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Inv(this long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>400 response with a single error message, in the shape the UI expects.</summary>
    public static IResult Error(string message, int status = StatusCodes.Status400BadRequest) =>
        Results.Json(new { error = message }, statusCode: status);
}

public static class RateLimits
{
    public const string Login = "login";
}
