namespace AssetInventory.Contracts.Auth;

public sealed record LoginRequest(string Login, string Password, string? Device);
public sealed record LoginStartRequest(string Login);
public sealed record LoginStartResponse(bool PasswordSetupRequired);
public sealed record SetInitialPasswordRequest(string Login, string Password, string? Device);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword, string? Device);
public sealed record RefreshRequest(string RefreshToken, string? Device);
public sealed record LogoutRequest(string RefreshToken);
public sealed record AuthUserResponse(Guid Id, string Login, string Role, Guid? EmployeeId);
public sealed record WebSessionResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, AuthUserResponse User);
public sealed record TokenResponse(string AccessToken, string RefreshToken, DateTimeOffset AccessTokenExpiresAt, DateTimeOffset RefreshTokenExpiresAt, AuthUserResponse User);
