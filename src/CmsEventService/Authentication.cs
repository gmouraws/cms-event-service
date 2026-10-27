using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace CmsEventService;

public sealed record Account(string Username, string Password, string Role);
public sealed class Accounts
{
    public IReadOnlyList<Account> Values { get; }
    public Accounts(IConfiguration config)
    {
        Values = new[] { "Cms", "Consumer", "Admin" }.Select(role => new Account(
            config[$"Auth:{role}:Username"] ?? "", config[$"Auth:{role}:Password"] ?? "", role)).ToArray();
        if (Values.Any(a => a.Username.Length == 0 || a.Username.Contains(':') || a.Username.Any(char.IsControl)
            || a.Password.Length < 16 || a.Password.Any(char.IsControl))
            || Values.Select(a => a.Username).Distinct(StringComparer.Ordinal).Count() != 3
            || Values.Select(a => a.Password).Distinct(StringComparer.Ordinal).Count() != 3
            || Values[0].Username.Length is < 10 or > 20 || !Guid.TryParseExact(Values[0].Password, "D", out var guid) || guid == Guid.Empty)
            throw new InvalidOperationException("Configure three distinct Auth accounts; CMS username must be 10-20 characters and password a nonempty GUID. Other passwords need at least 16 characters.");
    }
}

public sealed class BasicHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
    UrlEncoder encoder, Accounts accounts) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var raw = Request.Headers.Authorization;
        if (raw.Count == 0) return Task.FromResult(AuthenticateResult.NoResult());
        if (raw.Count != 1 || raw[0]!.Length > 2048 || !AuthenticationHeaderValue.TryParse(raw[0], out var header)
            || !header.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(header.Parameter))
            return Failure();
        string credentials;
        try { credentials = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(header.Parameter)); }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException) { return Failure(); }
        var separator = credentials.IndexOf(':');
        if (separator < 1 || credentials.Any(char.IsControl)) return Failure();
        var username = credentials[..separator];
        var password = credentials[(separator + 1)..];
        Account? match = null;
        foreach (var account in accounts.Values)
        {
            var userMatches = Equal(username, account.Username);
            var passwordMatches = Equal(password, account.Password);
            if (userMatches & passwordMatches) match = account;
        }
        if (match is null) return Failure();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, match.Username), new Claim(ClaimTypes.Role, match.Role)
        ], Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
    private static bool Equal(string left, string right) => CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(left)), SHA256.HashData(Encoding.UTF8.GetBytes(right)));
    private static Task<AuthenticateResult> Failure() => Task.FromResult(AuthenticateResult.Fail("Invalid credentials."));
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Basic realm=\"cms-event-service\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
