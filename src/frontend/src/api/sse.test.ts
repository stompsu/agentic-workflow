import { describe, expect, it } from 'vitest';
import { SseParser, StreamProtocolError, decodeEvent, readSseFrames, type SseFrame } from './sse';
import type { StreamEvent } from './types';
import { golden } from '../test/golden';

const enc = new TextEncoder();

function streamOf(chunks: Uint8Array[]): ReadableStream<Uint8Array> {
  return new ReadableStream({
    start(c) {
      chunks.forEach((x) => c.enqueue(x));
      c.close();
    },
  });
}

async function collect(chunks: Uint8Array[]): Promise<SseFrame[]> {
  const out: SseFrame[] = [];
  for await (const f of readSseFrames(streamOf(chunks))) out.push(f);
  return out;
}

function events(frames: SseFrame[]): StreamEvent[] {
  return frames.map(decodeEvent).filter((e): e is StreamEvent => e !== undefined);
}

function slices(bytes: Uint8Array, size: number): Uint8Array[] {
  const out: Uint8Array[] = [];
  for (let i = 0; i < bytes.length; i += size) out.push(bytes.slice(i, i + size));
  return out;
}

describe('SseParser', () => {
  it('parses id/event/data frames', () => {
    const frames = new SseParser().push('id: 1\nevent: token\ndata: {"text":"hi"}\n\n');
    expect(frames).toEqual([{ id: '1', event: 'token', data: '{"text":"hi"}' }]);
  });

  it('ignores comment heartbeats and does not emit data-less frames', () => {
    const frames = new SseParser().push(': hb\n\nid: 2\nevent: token\ndata: {"text":"a"}\n\n: hb\n\n');
    expect(frames).toHaveLength(1);
    expect(frames[0]?.event).toBe('token');
  });

  it('handles a frame split across arbitrary chunk boundaries', () => {
    const p = new SseParser();
    const input = 'id: 3\nevent: token\ndata: {"text":"split"}\n\n';
    const frames: SseFrame[] = [];
    for (const ch of input) frames.push(...p.push(ch));
    expect(frames).toHaveLength(1);
    expect(frames[0]?.data).toBe('{"text":"split"}');
  });

  it('joins multiple data lines with a newline', () => {
    const frames = new SseParser().push('event: x\ndata: line1\ndata: line2\n\n');
    expect(frames[0]?.data).toBe('line1\nline2');
  });

  it('supports CRLF and CR line endings, including CR/LF split between chunks', () => {
    const p = new SseParser();
    const a = p.push('event: token\r');
    const b = p.push('\ndata: {"text":"x"}\r\n\r');
    const c = p.push('\n');
    expect([...a, ...b, ...c]).toHaveLength(1);
    expect(new SseParser().push('event: a\rdata: 1\r\rid: 2')).toHaveLength(1);
  });

  it('strips a leading BOM and only one leading space of the value', () => {
    const frames = new SseParser().push('﻿event: token\ndata:  two spaces\n\n');
    expect(frames[0]).toEqual({ event: 'token', data: ' two spaces' });
  });

  it('defaults the event name to "message" and ignores retry/unknown fields', () => {
    const frames = new SseParser().push('retry: 100\nfoo: bar\ndata: x\n\n');
    expect(frames).toEqual([{ event: 'message', data: 'x' }]);
  });

  it('discards an incomplete trailing frame', () => {
    expect(new SseParser().push('event: token\ndata: {"text":"x"}\n')).toEqual([]);
  });
});

describe('readSseFrames against the backend golden files', () => {
  it('answer.sse -> session, agent, token, token, citation, done', async () => {
    const evs = events(await collect([enc.encode(golden('answer'))]));
    expect(evs.map((e) => e.type)).toEqual(['session', 'agent', 'token', 'token', 'citation', 'done']);
    expect(evs[0]).toEqual({ type: 'session', sessionId: '0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10' });
    expect(evs[1]).toEqual({ type: 'agent', name: 'Claims specialist' });
    expect(evs[4]).toEqual({
      type: 'citation',
      citation: { ref: 'S1', title: 'Claims handling times', docId: 'claims-handling-0003', url: 'https://kb.example.com/claims/handling' },
    });
  });

  it('clarification.sse keeps JSON-escaped newline, non-ASCII text, raw title and null url', async () => {
    const evs = events(await collect([enc.encode(golden('clarification'))]));
    const token = evs.find((e) => e.type === 'token');
    expect(token).toEqual({
      type: 'token',
      text: 'Is your question about policy coverage,\nan existing claim, or a health assessment? Zażółć <b>',
    });
    const citation = evs.find((e) => e.type === 'citation');
    expect(citation).toEqual({ type: 'citation', citation: { ref: 'S1', title: 'Raw <title> & more', docId: 'd1', url: null } });
  });

  it('error-midstream.sse ends with an error event', async () => {
    const evs = events(await collect([enc.encode(golden('error-midstream'))]));
    expect(evs.at(-1)).toEqual({ type: 'error', code: 'upstream_throttled', message: 'The service is busy. Please try again in a moment.' });
  });

  it.each(['answer', 'clarification', 'error-midstream'] as const)('%s.sse parses identically for 1, 2, 3 and 7 byte chunks (UTF-8 split across chunks)', async (name) => {
    const bytes = enc.encode(golden(name));
    const whole = events(await collect([bytes]));
    for (const size of [1, 2, 3, 7]) {
      expect(events(await collect(slices(bytes, size)))).toEqual(whole);
    }
  });
});

describe('decodeEvent', () => {
  it('ignores unknown event types (forward compatible)', () => {
    expect(decodeEvent({ event: 'future', data: '{}' })).toBeUndefined();
  });

  it('throws on malformed JSON or missing fields of a known event', () => {
    expect(() => decodeEvent({ event: 'token', data: '{nope' })).toThrow(StreamProtocolError);
    expect(() => decodeEvent({ event: 'token', data: '{}' })).toThrow(StreamProtocolError);
    expect(() => decodeEvent({ event: 'done', data: '[]' })).toThrow(StreamProtocolError);
  });

  it('treats a non-string citation url as null and ignores unknown properties', () => {
    const e = decodeEvent({ event: 'citation', data: '{"ref":"S2","title":"t","docId":"d","extra":1}' });
    expect(e).toEqual({ type: 'citation', citation: { ref: 'S2', title: 't', docId: 'd', url: null } });
  });
});
