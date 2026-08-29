using System.Security.Claims;
using Guyabano.WorkflowWorker;

namespace Guyabano.WebTerminal.Services;

internal sealed class HttpContextApprovalActorProvider(
    IHttpContextAccessor httpContextAccessor) : IApprovalActorProvider
{
    public ApprovalActor GetRequiredActor()
    {
        var principal = httpContextAccessor.HttpContext?.User ??
            throw new UnauthorizedAccessException(
                "Restart approval requires an active authenticated request.");
        var identity = principal.Identity;
        if (identity?.IsAuthenticated != true)
            throw new UnauthorizedAccessException(
                "Restart approval requires an authenticated user.");
        var subject = principal.FindFirstValue(ClaimTypes.NameIdentifier) ??
            principal.FindFirstValue("sub") ??
            identity.Name;
        if (string.IsNullOrWhiteSpace(subject))
            throw new UnauthorizedAccessException(
                "The authenticated user does not have a stable subject identifier.");
        return new ApprovalActor(
            subject,
            identity.Name ?? subject,
            identity.AuthenticationType ?? "authenticated-host");
    }
}
