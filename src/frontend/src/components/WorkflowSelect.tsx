import type { Workflow } from '../api/types';

interface Props {
  workflows: Workflow[];
  value: string;
  onChange: (id: string) => void;
  disabled?: boolean;
}

/** Options come only from GET /api/workflows; nothing is hardcoded. */
export function WorkflowSelect({ workflows, value, onChange, disabled }: Props) {
  const selected = workflows.find((w) => w.id === value);
  return (
    <div className="workflow-select">
      <label htmlFor="workflow-select">Assistant</label>
      <select id="workflow-select" value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>
        {workflows.map((w) => (
          <option key={w.id} value={w.id}>
            {w.displayName}
          </option>
        ))}
      </select>
      {selected?.description && <p className="hint">{selected.description}</p>}
    </div>
  );
}
