import type { Message } from '../api/types';
import type { Segment } from '../chat/turnReducer';
import { Citations } from './Citations';
import { Markdown } from './Markdown';

export function AgentBadge({ name }: { name: string | null | undefined }) {
  if (!name) return null;
  // Plain text only (api-contract section 8).
  return <span className="agent-badge">{name}</span>;
}

export function UserBubble({ content, notSent }: { content: string; notSent?: boolean }) {
  return (
    <li className="message message-user">
      <div className="bubble">
        <p className="plain">{content}</p>
        {notSent && <p className="note">Not sent</p>}
      </div>
    </li>
  );
}

export function AssistantBubble({
  idPrefix,
  agentName,
  text,
  citations,
  note,
  clarification,
}: {
  idPrefix: string;
  agentName?: string | null;
  text: string;
  citations: Message['citations'];
  note?: string;
  clarification?: boolean;
}) {
  return (
    <li className="message message-assistant">
      <div className="bubble">
        <div className="bubble-head">
          <AgentBadge name={agentName} />
          {clarification && <span className="tag">Question</span>}
        </div>
        {text ? <Markdown text={text} citations={citations ?? []} idPrefix={idPrefix} /> : null}
        <Citations citations={citations ?? []} idPrefix={idPrefix} />
        {note && <p className="note">{note}</p>}
      </div>
    </li>
  );
}

export function PersistedMessage({ message }: { message: Message }) {
  if (message.role === 'user') return <UserBubble content={message.content} />;
  const note =
    message.status === 'cancelled'
      ? 'Stopped'
      : message.status === 'failed'
        ? 'This reply could not be completed.'
        : undefined;
  return (
    <AssistantBubble
      idPrefix={`m-${message.id}`}
      agentName={message.agentName}
      text={message.content}
      citations={message.citations}
      clarification={message.kind === 'clarification'}
      {...(note !== undefined && { note })}
    />
  );
}

export function StreamingSegment({ segment, index, stopped }: { segment: Segment; index: number; stopped: boolean }) {
  return (
    <AssistantBubble
      idPrefix={`live-${index}`}
      agentName={segment.agentName}
      text={segment.text}
      citations={segment.citations}
      {...(stopped && { note: 'Stopped' })}
    />
  );
}
