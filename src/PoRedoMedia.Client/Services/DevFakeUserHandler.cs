namespace PoRedoMedia.Client.Services;

/// <summary>
/// Attaches <c>X-Fake-User</c> (and optional <c>X-Fake-Roles</c>) to outgoing API calls when
/// configured in non-production environments for local dev switching.
/// </summary>
public sealed class DevFakeUserHandler(Func<(string? User, string? Roles)> getPersona) : DelegatingHandler
{
    public const string UserHeader = "X-Fake-User";
    public const string RolesHeader = "X-Fake-Roles";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var (user, roles) = getPersona();
        if (!string.IsNullOrWhiteSpace(user))
        {
            request.Headers.Remove(UserHeader);
            request.Headers.TryAddWithoutValidation(UserHeader, user);

            if (!string.IsNullOrWhiteSpace(roles))
            {
                request.Headers.Remove(RolesHeader);
                request.Headers.TryAddWithoutValidation(RolesHeader, roles);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
