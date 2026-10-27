using SupportChat.Api.Contracts;
using SupportChat.Api.Guests;
using SupportChat.Core.Security;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        // Guests see only the workflows enabled for guests (Guest:Workflows); the registry itself stays identity-agnostic.
        api.MapGet("/workflows", (IWorkflowRegistry registry, ICurrentUser user, GuestWorkflowPolicy guestWorkflows) =>
            Results.Ok(new ItemsDto<WorkflowDto>(
                registry.List().Where(w => !user.IsGuest || guestWorkflows.IsAllowed(w.Id)).Select(w => w.ToDto()).ToList())));
    }
}
