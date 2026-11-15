using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AssetInventory.Application.Auth;
using AssetInventory.Application.Common;
using AssetInventory.Contracts.Auth;
using AssetInventory.Domain;
using AssetInventory.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AssetInventory.Infrastructure.Security;

internal sealed class AuthService(
    AppDbContext db,
    IPasswordHasher<UserAccount> passwordHasher,
    IOptions<JwtOptions> options) : IAuthService
{
    private readonly JwtOptions _options = options.Value;

    public async Task<LoginStartResponse> StartLoginAsync(LoginStartRequest request, CancellationToken cancellationToken)
    {
        var user = await FindActiveUserAsync(request.Login, cancellationToken);
        return new LoginStartResponse(string.IsNullOrEmpty(user.PasswordHash));
    }

    public async Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.Password) || request.Password.Length > 256) throw new UnauthorizedException();
        var user = await FindActiveUserAsync(request.Login, cancellationToken);
        if (string.IsNullOrEmpty(user.PasswordHash)) throw new PasswordSetupRequiredException();

        var passwordResult = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (passwordResult == PasswordVerificationResult.Failed)
        {
            throw new UnauthorizedException();
        }

        return await CreateTokenPairAsync(user, request.Device, cancellationToken);
    }

    public async Task<TokenResponse> SetInitialPasswordAsync(SetInitialPasswordRequest request, CancellationToken cancellationToken)
    {
        ValidatePassword(request.Password);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var user = await FindActiveUserAsync(request.Login, cancellationToken);
        if (!string.IsNullOrEmpty(user.PasswordHash))
        {
            throw new ConflictException("Пароль для учетной записи уже установлен.");
        }

        var passwordHash = passwordHasher.HashPassword(user, request.Password);
        var updated = await db.Users
            .Where(x => x.Id == user.Id && x.PasswordHash == string.Empty)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.PasswordHash, passwordHash), cancellationToken);
        if (updated != 1)
        {
            throw new ConflictException("Пароль для учетной записи уже установлен.");
        }

        // The conditional update holds the user row until commit. Recheck access
        // in case a block/dismissal committed after the initial login lookup.
        if (!await UserAccess.IsActiveAsync(db, user.Id, cancellationToken, user.SecurityVersion)) throw new UnauthorizedException();

        user.PasswordHash = passwordHash;
        var tokens = await CreateTokenPairAsync(user, request.Device, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return tokens;
    }

    public async Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken, Guid? expectedUserId = null)
    {
        if (string.IsNullOrWhiteSpace(request.RefreshToken) || request.RefreshToken.Length > 1024) throw new UnauthorizedException("Refresh token недействителен.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tokenHash = Hash(request.RefreshToken);
        var locked = await db.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken) ?? throw new UnauthorizedException("Refresh token недействителен.");
        if (expectedUserId is Guid expected && locked.UserId != expected)
            throw new UnauthorizedException("Открыта сессия другой учетной записи.", "session_account_mismatch");
        var current = TrackLockedToken(locked);

        if (current.RevokedAt is not null)
        {
            await RevokeReplacementChainAsync(current, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new UnauthorizedException("Обнаружено повторное использование refresh token. Цепочка сессии отозвана.");
        }
        if (current.ExpiresAt <= DateTimeOffset.UtcNow || !await UserAccess.IsActiveAsync(db, current.UserId, cancellationToken, current.SecurityVersion))
            throw new UnauthorizedException("Refresh token истек или отозван.");

        current.RevokedAt = DateTimeOffset.UtcNow;
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == current.UserId, cancellationToken);
        if (user.SecurityVersion != current.SecurityVersion) throw new UnauthorizedException("Сессия отозвана после смены пароля.");
        var response = await CreateTokenPairAsync(user, request.Device ?? current.Device, cancellationToken);
        current.ReplacedById = await db.RefreshTokens
            .Where(x => x.TokenHash == Hash(response.RefreshToken))
            .Select(x => (Guid?)x.Id)
            .SingleAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private async Task RevokeReplacementChainAsync(RefreshToken reused, CancellationToken cancellationToken)
    {
        var nextId = reused.ReplacedById;
        var visited = new HashSet<Guid> { reused.Id };
        while (nextId.HasValue && visited.Add(nextId.Value))
        {
            // Lock before reading ReplacedById: a concurrent rotation may be adding
            // another descendant. Its committed replacement must also be revoked.
            var locked = await db.RefreshTokens
                .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE id = {nextId.Value} FOR UPDATE")
                .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
            if (locked is null) break;
            var next = TrackLockedToken(locked);
            next.RevokedAt ??= DateTimeOffset.UtcNow;
            nextId = next.ReplacedById;
        }
    }

    public async Task<bool> LogoutAsync(string refreshToken, CancellationToken cancellationToken, Guid? expectedUserId = null)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || refreshToken.Length > 1024) return true;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var tokenHash = Hash(refreshToken);
        var locked = await db.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (locked is not null)
        {
            if (expectedUserId is Guid expected && locked.UserId != expected) return false;
            var token = TrackLockedToken(locked);
            token.RevokedAt ??= DateTimeOffset.UtcNow;
            await RevokeReplacementChainAsync(token, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<TokenResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken ct, string? webRefreshToken = null)
    {
        ValidatePassword(request.NewPassword);
        if (string.IsNullOrEmpty(request.CurrentPassword)) throw new RequestValidationException("Введите текущий пароль.");
        if (request.CurrentPassword == request.NewPassword) throw new RequestValidationException("Новый пароль должен отличаться от текущего.");
        if (!string.IsNullOrEmpty(webRefreshToken))
        {
            var tokenHash = Hash(webRefreshToken);
            if (await db.RefreshTokens.AnyAsync(x => x.TokenHash == tokenHash && x.UserId != userId, ct))
                throw new ConflictException("В браузере открыта сессия другой учетной записи.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Serialize password changes while allowing refresh/login token inserts
        // to take the foreign-key KEY SHARE lock on this user.
        var user = await db.Users.FromSqlInterpolated($"SELECT * FROM user_accounts WHERE id = {userId} FOR NO KEY UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new UnauthorizedException();
        if (!await UserAccess.IsActiveAsync(db, userId, ct) || string.IsNullOrEmpty(user.PasswordHash)
            || passwordHasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
            throw new UnauthorizedException("Неверный текущий пароль.");
        var tracked = db.Users.Local.SingleOrDefault(x => x.Id == userId);
        if (tracked is not null) db.Entry(tracked).State = EntityState.Detached;
        db.Users.Attach(user);
        user.PasswordHash = passwordHasher.HashPassword(user, request.NewPassword);
        user.SecurityVersion++;
        var now = DateTimeOffset.UtcNow;
        await db.RefreshTokens.Where(x => x.UserId == userId && x.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.RevokedAt, now), ct);
        db.AuditLogs.Add(new AuditLog { UserId = userId, Action = "user.password-changed", EntityType = nameof(UserAccount), EntityId = userId.ToString() });
        var tokens = await CreateTokenPairAsync(user, request.Device, ct);
        await transaction.CommitAsync(ct);
        return tokens;
    }

    private RefreshToken TrackLockedToken(RefreshToken locked)
    {
        var tracked = db.RefreshTokens.Local.SingleOrDefault(x => x.Id == locked.Id);
        if (tracked is null) { db.RefreshTokens.Attach(locked); return locked; }
        // A long-lived scope may hold values from before the competing transaction.
        db.Entry(tracked).CurrentValues.SetValues(locked);
        db.Entry(tracked).OriginalValues.SetValues(locked);
        return tracked;
    }

    private async Task<TokenResponse> CreateTokenPairAsync(
        UserAccount user,
        string? device,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var accessExpiresAt = now.AddMinutes(_options.AccessTokenMinutes);
        var refreshExpiresAt = now.AddDays(_options.RefreshTokenDays);
        var rawRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            SecurityVersion = user.SecurityVersion,
            TokenHash = Hash(rawRefreshToken),
            Device = NormalizeDevice(device),
            ExpiresAt = refreshExpiresAt
        });
        await db.SaveChangesAsync(cancellationToken);

        var claims = new Claim[]
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new("sv", user.SecurityVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.Login),
            new(ClaimTypes.Role, user.Role.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            expires: accessExpiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new TokenResponse(
            new JwtSecurityTokenHandler().WriteToken(token),
            rawRefreshToken,
            accessExpiresAt,
            refreshExpiresAt,
            new AuthUserResponse(user.Id, user.Login, user.Role.ToString(), user.EmployeeId));
    }

    private static string Hash(string token)
    {
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    private static string NormalizeDevice(string? device)
    {
        var value = string.IsNullOrWhiteSpace(device) ? "unknown" : device.Trim();
        if (value.Length <= 300) return value;
        // Device metadata must fit the column without cutting a UTF-16 pair.
        return value[..(char.IsHighSurrogate(value[299]) ? 299 : 300)];
    }

    private async Task<UserAccount> FindActiveUserAsync(string login, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(login)) throw new UnauthorizedException();
        var normalizedLogin = login.Trim().ToUpperInvariant();
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.NormalizedLogin == normalizedLogin,
            cancellationToken) ?? throw new UnauthorizedException();
        return await UserAccess.IsActiveAsync(db, user.Id, cancellationToken) ? user : throw new UnauthorizedException();
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new RequestValidationException("Пароль должен содержать минимум 8 символов.");
        }
        if (password.Length > 256) throw new RequestValidationException("Пароль должен содержать не более 256 символов.");
    }
}
