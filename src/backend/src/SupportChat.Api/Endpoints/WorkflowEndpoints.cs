using SupportChat.Api.Contracts;
using SupportChat.Core.Workflows;

namespace SupportChat.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/workflows", (IWorkflowRegistry registry) =>
            Results.Ok(new ItemsDto<WorkflowDto>(registry.List().Select(w => w.ToDto()).ToList())));
    }
}
