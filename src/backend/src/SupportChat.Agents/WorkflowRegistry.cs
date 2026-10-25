using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using SupportChat.Core.Workflows;

namespace SupportChat.Agents;

/// <summary>
/// Built from every registered <see cref="IWorkflowDefinition"/> in registration order. Validates at construction:
/// ids unique, kebab-case, display name non-empty; otherwise startup fails (ADR 0002 section 2).
/// </summary>
public sealed partial class WorkflowRegistry : IWorkflowRegistry
{
    private readonly IReadOnlyList<IWorkflowDefinition> _definitions;
    private readonly Dictionary<string, IWorkflowDefinition> _byId;
    private readonly IReadOnlyList<WorkflowDescriptor> _descriptors;

    public WorkflowRegistry(IEnumerable<IWorkflowDefinition> definitions)
    {
        _definitions = definitions.ToList();
        _byId = new Dictionary<string, IWorkflowDefinition>(StringComparer.Ordinal);
        foreach (var definition in _definitions)
        {
            if (string.IsNullOrEmpty(definition.Id) || !KebabCase().IsMatch(definition.Id))
            {
                throw new InvalidOperationException(
                    $"Workflow id '{definition.Id}' ({definition.GetType().Name}) must be lowercase kebab-case.");
            }

            if (string.IsNullOrWhiteSpace(definition.DisplayName))
            {
                throw new InvalidOperationException($"Workflow '{definition.Id}' has an empty display name.");
            }

            if (!_byId.TryAdd(definition.Id, definition))
            {
                throw new InvalidOperationException($"Workflow id '{definition.Id}' is registered more than once.");
            }
        }

        _descriptors = _definitions.Select(d => new WorkflowDescriptor(d.Id, d.DisplayName, d.Description ?? string.Empty)).ToList();
    }

    public IReadOnlyList<WorkflowDescriptor> List() => _descriptors;

    public bool TryGet(string workflowId, [NotNullWhen(true)] out IWorkflowDefinition? workflow) =>
        _byId.TryGetValue(workflowId, out workflow);

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex KebabCase();
}
