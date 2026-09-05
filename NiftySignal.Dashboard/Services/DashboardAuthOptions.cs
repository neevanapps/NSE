using System.Security.Cryptography;
using System.Text;

namespace NiftySignal.Dashboard.Services;

/// <summary>
/// Single-user dashboard credentials (2026-09-05). Deliberately not a user store, not OAuth,
/// not multi-user -- this is one person's own tool, and the gate exists because the dashboard
/// became reachable from phone and desktop over Tailscale while carrying the kill switch and
/// trading controls. Tailscale already restricts *who can reach it*; this is the second layer
/// for a lost or shared device.
///
/// <see cref="PasswordSha256"/> is a hex SHA-256 of the password, never the password itself:
/// even a hardcoded single-user credential has no business sitting in cleartext in a config
/// file that gets copied between machines. Generate one with:
/// <c>[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("your-password")))</c>
/// </summary>
public sealed class DashboardAuthOptions
{
    public const string SectionName = "DashboardAuth";

    public string Username { get; set; } = "admin";

    public string PasswordSha256 { get; set; } = string.Empty;

    /// <summary>
    /// False when no password hash is configured at all -- in that case the app refuses to
    /// authenticate anyone rather than falling open, which is the failure mode that actually
    /// matters here.
    /// </summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(PasswordSha256);

    public bool Matches(string? username, string? password)
    {
        if (!IsConfigured || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return false;
        }

        var suppliedHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password)));

        // Fixed-time comparison on both fields so neither the username nor the password can be
        // probed a character at a time by timing the response.
        var usernameMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(username), Encoding.UTF8.GetBytes(Username));
        var passwordMatches = CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(suppliedHash), Encoding.UTF8.GetBytes(PasswordSha256.ToUpperInvariant()));

        return usernameMatches && passwordMatches;
    }
}
