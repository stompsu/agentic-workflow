// Response shapes per docs/api-contract.md section 2 (clients ignore unknown properties).

export interface Workflow {
  id: string;
  displayName: string;
  description: string;
}

export interface Session {
  id: string;
  workflowId: string;
  title: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface Citation {
  ref: string;
  title: string;
  docId: string;
  url: string | null;
}

export type MessageStatus = 'complete' | 'cancelled' | 'failed';

export interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  createdAt: string;
  status: MessageStatus;
  kind?: 'answer' | 'clarification';
  agentName?: string;
  citations?: Citation[];
  errorCode?: string;
}

export interface SessionDetail extends Session {
  hasMoreMessages: boolean;
  messages: Message[];
}

export interface SessionPage {
  items: Session[];
  continuationToken: string | null;
}

export interface WorkflowList {
  items: Workflow[];
}

// SSE events (section 4.2)
export type StreamEvent =
  | { type: 'session'; sessionId: string }
  | { type: 'agent'; name: string }
  | { type: 'token'; text: string }
  | { type: 'citation'; citation: Citation }
  | { type: 'done'; messageId: string }
  | { type: 'error'; code: string; message: string };
