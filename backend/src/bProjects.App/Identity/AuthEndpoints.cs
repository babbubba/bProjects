using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using bProjects.App.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace bProjects.App.Identity;

public sealed class AuthOptions
{
    public string SigningKey { get; set; } = string.Empty;
    public string Issuer { get; set; } = "bProjects";
    public string Audience { get; set; } = "bProjects";
    public int AccessTokenMinutes { get; set; } = 10;
    public int RefreshTokenDays { get; set; } = 14;
}

/// <summary>
/// Sessione locale: access token JWT breve (tenuto in memoria dal client) e refresh token ruotato
/// in cookie HttpOnly/Secure/SameSite=Strict. Refresh e logout sono protetti da CSRF double-submit
/// (cookie XSRF-TOKEN leggibile + header X-XSRF-TOKEN).
/// </summary>
public static class AuthEndpoints
{
    private const string RefreshCookie = "bp_refresh";
    private const string RefreshCookiePath = "/api/auth";
    private const string XsrfCookie = "XSRF-TOKEN";
    private const string XsrfHeader = "X-XSRF-TOKEN";

    public sealed record LoginRequest(string? Username, string? Password);

    public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").AllowAnonymous();
        group.MapPost("/login", Login);
        group.MapPost("/refresh", Refresh);
        group.MapPost("/logout", Logout);
        return app;
    }

    private static async Task<IResult> Login(
        LoginRequest request, HttpContext http, UserManager<IdentityUser<Guid>> users,
        AppDbContext db, IOptions<AuthOptions> options, TimeProvider time)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrEmpty(request.Password))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["credentials"] = ["Username e password sono obbligatori."] });
        }

        var user = await users.FindByNameAsync(request.Username);
        if (user is null || await users.IsLockedOutAsync(user))
        {
            return Results.Unauthorized();
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return Results.Unauthorized();
        }

        await users.ResetAccessFailedCountAsync(user);
        if (!await users.IsInRoleAsync(user, AccountBootstrap.PmRole))
        {
            return Results.Forbid();
        }

        return await IssueAsync(http, user, users, db, options.Value, time);
    }

    private static async Task<IResult> Refresh(
        HttpContext http, UserManager<IdentityUser<Guid>> users, AppDbContext db,
        IOptions<AuthOptions> options, TimeProvider time)
    {
        if (!IsCsrfValid(http))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!http.Request.Cookies.TryGetValue(RefreshCookie, out var raw) || string.IsNullOrEmpty(raw))
        {
            return Results.Unauthorized();
        }

        var now = time.GetUtcNow();
        var current = await db.RefreshSessions.AsNoTracking().SingleOrDefaultAsync(s => s.TokenHash == Hash(raw));
        if (current is null || current.RevokedAt is not null || current.ExpiresAt <= now)
        {
            ClearCookies(http);
            return Results.Unauthorized();
        }

        var user = await users.FindByIdAsync(current.UserId.ToString());
        if (user is null || await users.IsLockedOutAsync(user))
        {
            ClearCookies(http);
            return Results.Unauthorized();
        }

        // Rotazione atomica: la nuova sessione e la revoca della precedente sono un'unica transazione;
        // se un'altra richiesta ha già ruotato lo stesso token, la revoca condizionata non trova la riga.
        await using var transaction = await db.Database.BeginTransactionAsync();
        var (newRaw, next) = NewSession(user.Id, options.Value, now);
        db.RefreshSessions.Add(next);
        await db.SaveChangesAsync();

        var revoked = await db.RefreshSessions
            .Where(s => s.Id == current.Id && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.ReplacedById, next.Id));
        if (revoked != 1)
        {
            await transaction.RollbackAsync();
            ClearCookies(http);
            return Results.Unauthorized();
        }

        await transaction.CommitAsync();
        return await RespondAsync(http, user, users, options.Value, now, newRaw, next.ExpiresAt);
    }

    private static async Task<IResult> Logout(HttpContext http, AppDbContext db, TimeProvider time)
    {
        if (!IsCsrfValid(http))
        {
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        }

        if (http.Request.Cookies.TryGetValue(RefreshCookie, out var raw) && !string.IsNullOrEmpty(raw))
        {
            var hash = Hash(raw);
            var now = time.GetUtcNow();
            await db.RefreshSessions
                .Where(s => s.TokenHash == hash && s.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now));
        }

        ClearCookies(http);
        return Results.NoContent();
    }

    private static async Task<IResult> IssueAsync(
        HttpContext http, IdentityUser<Guid> user, UserManager<IdentityUser<Guid>> users,
        AppDbContext db, AuthOptions options, TimeProvider time)
    {
        var now = time.GetUtcNow();
        var (raw, session) = NewSession(user.Id, options, now);
        db.RefreshSessions.Add(session);
        await db.SaveChangesAsync();
        return await RespondAsync(http, user, users, options, now, raw, session.ExpiresAt);
    }

    private static async Task<IResult> RespondAsync(
        HttpContext http, IdentityUser<Guid> user, UserManager<IdentityUser<Guid>> users,
        AuthOptions options, DateTimeOffset now, string rawRefresh, DateTimeOffset refreshExpiresAt)
    {
        var expiresAt = now.AddMinutes(options.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new("name", user.UserName ?? string.Empty),
        };
        claims.AddRange((await users.GetRolesAsync(user)).Select(role => new Claim("role", role)));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = options.Issuer,
            Audience = options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expiresAt.UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256),
        });

        http.Response.Cookies.Append(RefreshCookie, rawRefresh, CookieOptions(refreshExpiresAt, httpOnly: true, RefreshCookiePath));
        http.Response.Cookies.Append(XsrfCookie, RandomToken(), CookieOptions(refreshExpiresAt, httpOnly: false, "/"));
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(new TokenResponse(token, expiresAt));
    }

    private static (string Raw, RefreshSession Session) NewSession(Guid userId, AuthOptions options, DateTimeOffset now)
    {
        var raw = RandomToken();
        return (raw, new RefreshSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = Hash(raw),
            ExpiresAt = now.AddDays(options.RefreshTokenDays),
        });
    }

    private static bool IsCsrfValid(HttpContext http)
    {
        if (!http.Request.Cookies.TryGetValue(XsrfCookie, out var cookie) || string.IsNullOrEmpty(cookie) ||
            !http.Request.Headers.TryGetValue(XsrfHeader, out var header) || string.IsNullOrEmpty(header.ToString()))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(cookie), Encoding.UTF8.GetBytes(header.ToString()));
    }

    private static void ClearCookies(HttpContext http)
    {
        http.Response.Cookies.Delete(RefreshCookie, CookieOptions(null, httpOnly: true, RefreshCookiePath));
        http.Response.Cookies.Delete(XsrfCookie, CookieOptions(null, httpOnly: false, "/"));
    }

    private static CookieOptions CookieOptions(DateTimeOffset? expires, bool httpOnly, string path) => new()
    {
        HttpOnly = httpOnly,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        Path = path,
        Expires = expires,
        IsEssential = true,
    };

    private static string RandomToken() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
