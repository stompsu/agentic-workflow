using Microsoft.Extensions.AI;

namespace AgenticWorkflow.Core.Models;

/// <summary>
/// The only way agents obtain a model client (ADR 0002 section 4). Implemented in Infrastructure over
/// AIProjectClient with a TokenCredential; there is no key path for models.
/// </summary>
public interface IChatClientFactory
{
    IChatClient Create(string deploymentName);
}
