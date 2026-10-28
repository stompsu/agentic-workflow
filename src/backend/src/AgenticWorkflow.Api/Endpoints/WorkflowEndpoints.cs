using AgenticWorkflow.Api.Contracts;
using AgenticWorkflow.Api.Guests;
using AgenticWorkflow.Core.Security;
using AgenticWorkflow.Core.Workflows;

namespace AgenticWorkflow.Api.Endpoints;

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
