namespace AgenticWorkflow.Agents.Workflows.SupportTicket;

/// <summary>Serialized camelCase ("policy", "claims", "healthAssessments", "unknown").</summary>
public enum TicketCategory
{
    Policy,
    Claims,
    HealthAssessments,
    Unknown,
}

/// <summary>Structured output of the orchestrator (ADR 0002 section 6).</summary>
public sealed record TicketClassification(
    TicketCategory Category,
    double Confidence,
    string? Rationale,
    string? ClarifyingQuestion);
