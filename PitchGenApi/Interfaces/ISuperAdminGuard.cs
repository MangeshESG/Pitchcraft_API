using System.Security.Claims;

namespace PitchGenApi.Interfaces
{
    /// <summary>
    /// The outcome of a super-admin check. <see cref="IsAllowed"/> is the only
    /// thing a caller should branch on; the other two fields are what a
    /// controller turns into a response when it is false.
    /// </summary>
    public sealed record SuperAdminCheck(
        bool IsAllowed,
        int CallerId,
        int StatusCode,
        string? Message);

    /// <summary>
    /// Gate for the handful of actions that hand out accounts and credits —
    /// creating a user, adding credits, removing credits.
    ///
    /// Being an admin is not enough for these. The caller has to be one of the
    /// client ids listed under <c>SuperAdmins:ClientIds</c> in configuration,
    /// so the blast radius of an ordinary admin account is unchanged even
    /// though the admin page now shows these panels.
    ///
    /// The caller is always identified from their own signed token, never from
    /// anything in the request, so posting someone else's id proves nothing.
    /// </summary>
    public interface ISuperAdminGuard
    {
        Task<SuperAdminCheck> AuthorizeAsync(
            ClaimsPrincipal user,
            CancellationToken cancellationToken = default);
    }
}
