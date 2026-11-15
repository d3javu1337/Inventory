using AssetInventory.Contracts.Auth;

namespace AssetInventory.Application.Auth;

public interface IAuthService
{
    Task<LoginStartResponse> StartLoginAsync(LoginStartRequest request, CancellationToken cancellationToken);
    Task<TokenResponse> LoginAsync(LoginRequest request, CancellationToken cancellationToken);
    Task<TokenResponse> SetInitialPasswordAsync(SetInitialPasswordRequest request, CancellationToken cancellationToken);
    Task<TokenResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, CancellationToken cancellationToken, string? webRefreshToken = null);
    Task<TokenResponse> RefreshAsync(RefreshRequest request, CancellationToken cancellationToken, Guid? expectedUserId = null);
    Task<bool> LogoutAsync(string refreshToken, CancellationToken cancellationToken, Guid? expectedUserId = null);
}
