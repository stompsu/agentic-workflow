import type { Citation, StreamEvent } from './types';

export interface SseFrame {
  /** `event:` field; defaults to "message" per the SSE spec. */
  event: string;
  data: string;
  id?: string;
}

/**
 * Incremental SSE frame parser (WHATWG EventSource framing, api-contract 4.1).
 * - Feed decoded text; chunks may split anywhere (inside a line, between CR and LF).
 * - Lines starting with ":" (heartbeats) are ignored; a frame without any data line is not emitted.
 * - Multiple `data:` lines are joined with "\n".
 * - An incomplete trailing frame at end of stream is discarded (spec behavior).
 */
export class SseParser {
  private buffer = '';
  private event = '';
  private data: string[] = [];
  private id: string | undefined;
  private hasData = false;
  private first = true;

  push(text: string): SseFrame[] {
    if (this.first && text.length > 0) {
      this.first = false;
      if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
    }
    this.buffer += text;
    const frames: SseFrame[] = [];

    for (;;) {
      const m = /\r\n|\n|\r/.exec(this.buffer);
      if (!m) break;
      // A trailing lone "\r" may be the first half of "\r\n": wait for more input.
      if (m[0] === '\r' && m.index + 1 === this.buffer.length) break;
      const line = this.buffer.slice(0, m.index);
      this.buffer = this.buffer.slice(m.index + m[0].length);
      const frame = this.processLine(line);
      if (frame) frames.push(frame);
    }
    return frames;
  }

  private processLine(line: string): SseFrame | undefined {
    if (line === '') {
      if (!this.hasData) {
        this.reset();
        return undefined;
      }
      const frame: SseFrame = { event: this.event || 'message', data: this.data.join('\n') };
      if (this.id !== undefined) frame.id = this.id;
      this.reset();
      return frame;
    }
    if (line.startsWith(':')) return undefined;

    const colon = line.indexOf(':');
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? '' : line.slice(colon + 1);
    if (value.startsWith(' ')) value = value.slice(1);

    switch (field) {
      case 'event':
        this.event = value;
        break;
      case 'data':
        this.data.push(value);
        this.hasData = true;
        break;
      case 'id':
        if (!value.includes('\0')) this.id = value;
        break;
      default:
        break; // retry and unknown fields: ignored (the client never reconnects)
    }
    return undefined;
  }

  private reset(): void {
    this.event = '';
    this.data = [];
    this.hasData = false;
    this.id = undefined;
  }
}

/** Reads a byte stream and yields SSE frames. UTF-8 sequences split across chunks are decoded correctly. */
export async function* readSseFrames(body: ReadableStream<Uint8Array>, signal?: AbortSignal): AsyncGenerator<SseFrame> {
  const reader = body.getReader();
  const decoder = new TextDecoder('utf-8');
  const parser = new SseParser();
  const onAbort = () => {
    void reader.cancel().catch(() => undefined);
  };
  signal?.addEventListener('abort', onAbort, { once: true });
  try {
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      for (const frame of parser.push(decoder.decode(value, { stream: true }))) yield frame;
    }
    for (const frame of parser.push(decoder.decode())) yield frame;
  } finally {
    signal?.removeEventListener('abort', onAbort);
    await reader.cancel().catch(() => undefined);
    reader.releaseLock();
  }
}

export class StreamProtocolError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'StreamProtocolError';
  }
}

function asRecord(v: unknown): Record<string, unknown> {
  if (typeof v !== 'object' || v === null || Array.isArray(v)) throw new StreamProtocolError('Event data is not an object');
  return v as Record<string, unknown>;
}

function reqStr(r: Record<string, unknown>, key: string): string {
  const v = r[key];
  if (typeof v !== 'string') throw new StreamProtocolError(`Event field "${key}" is missing`);
  return v;
}

/**
 * Converts a frame to a typed event. Unknown event types return undefined (forward compatible);
 * malformed data of a known event throws StreamProtocolError.
 */
export function decodeEvent(frame: SseFrame): StreamEvent | undefined {
  switch (frame.event) {
    case 'session':
    case 'agent':
    case 'token':
    case 'citation':
    case 'done':
    case 'error':
      break;
    default:
      return undefined;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(frame.data);
  } catch {
    throw new StreamProtocolError(`Invalid JSON in "${frame.event}" event`);
  }
  const d = asRecord(parsed);
  switch (frame.event) {
    case 'session':
      return { type: 'session', sessionId: reqStr(d, 'sessionId') };
    case 'agent':
      return { type: 'agent', name: reqStr(d, 'name') };
    case 'token':
      return { type: 'token', text: reqStr(d, 'text') };
    case 'citation': {
      const url = d.url;
      const citation: Citation = {
        ref: reqStr(d, 'ref'),
        title: reqStr(d, 'title'),
        docId: reqStr(d, 'docId'),
        url: typeof url === 'string' ? url : null,
      };
      return { type: 'citation', citation };
    }
    case 'done':
      return { type: 'done', messageId: reqStr(d, 'messageId') };
    default:
      return { type: 'error', code: reqStr(d, 'code'), message: reqStr(d, 'message') };
  }
}
