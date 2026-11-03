using System.Net;
using System.Globalization;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// GET /api/agent/snapshot?bars=N : the read-only agent feed (see <see cref="AgentSnapshotService"/>).
/// <para><b>Loopback only.</b> The decision uses the TCP connection's remote address (<c>HttpContext.Connection.RemoteIpAddress</c>), never a header: the Dashboard is reached over Tailscale on port 5210, and a
/// Tailscale / LAN client has a non-loopback address however it labels itself. A request that carries any forwarding header (X-Forwarded-*, Forwarded, X-Real-IP) is refused too, because it came through a proxy and the
/// loopback address would then be the proxy's, not the caller's. The Host header must also name a loopback host, which closes DNS-rebinding from a browser on the same machine.</para>
/// <para><b>Authentication.</b> The Dashboard's cookie login is enforced per page (<c>[Authorize]</c> on the Razor pages / <c>AuthorizeRouteView</c>); there is no fallback policy, so this endpoint is deliberately NOT behind the login
/// (an external agent has no cookie) and says <c>AllowAnonymous</c> explicitly. The loopback check is its access control. It reads no cookie and never sets one; the payload carries no secret.</para>
/// </summary>
public static class AgentFeedEndpoint
{
    public const string Route = "/api/agent/snapshot";

    static readonly string[] ForwardingHeaders = ["Forwarded", "X-Forwarded-For", "X-Forwarded-Host", "X-Forwarded-Proto", "X-Real-IP"];
    static readonly string[] LoopbackHosts = ["localhost", "127.0.0.1", "[::1]", "::1"];

    /// <summary>Null when the request is a direct loopback request; otherwise a short reason (never echoed with detail to the caller).</summary>
    public static string? RejectReason(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is null)
        {
            return "remote address unknown";
        }

        if (remote.IsIPv4MappedToIPv6)
        {
            remote = remote.MapToIPv4();
        }

        if (!IPAddress.IsLoopback(remote))
        {
            return "not a loopback caller";
        }

        if (ForwardingHeaders.Any(h => context.Request.Headers.ContainsKey(h)))
        {
            return "proxied request";
        }

        var host = context.Request.Host.Host;
        if (!LoopbackHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
        {
            return "non-loopback Host header";
        }

        return null;
    }

    /// <summary>Parses the optional <c>bars</c> query value. Absent = <see cref="AgentSnapshotService.DefaultBars"/>; anything that is not an integer in 1..300 is an error (never clamped silently).</summary>
    public static bool TryParseBars(string? raw, out int bars)
    {
        if (string.IsNullOrEmpty(raw))
        {
            bars = AgentSnapshotService.DefaultBars;
            return true;
        }

        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out bars)
            && bars >= AgentSnapshotService.MinBars && bars <= AgentSnapshotService.MaxBars;
    }

    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet(Route, async (HttpContext context, AgentSnapshotService service) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (RejectReason(context) is not null)
            {
                return Results.Json(new { error = "forbidden", message = "The agent feed is available to loopback callers only." }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (!TryParseBars(context.Request.Query["bars"], out var bars))
            {
                return Results.Json(new { error = "bad_request", message = $"bars must be an integer from {AgentSnapshotService.MinBars} to {AgentSnapshotService.MaxBars}." },
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var snapshot = await service.SnapshotAsync(bars, context.RequestAborted);
            return Results.Json(snapshot, AgentFeedJson.Options);
        }).AllowAnonymous();
}
