import { describe, expect, it, vi } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Workspace } from './Workspace';
import { LoginPage } from './LoginPage';
import {
  WORKFLOWS,
  answerStream,
  frame,
  fakeAuth,
  fakeBackend,
  json,
  makeClient,
  makeDetail,
  makeSession,
  problem,
  renderWithApi,
  sseResponse,
  type Call,
} from '../test/helpers';
import { AuthContext } from '../auth/AuthContext';
import { render } from '@testing-library/react';
import type { Message } from '../api/types';

const wfRoute = (items = WORKFLOWS) => ({ 'GET /api/workflows': () => json({ items }) });

const userMsg: Message = { id: 'u1', role: 'user', content: 'How long does a claim take?', createdAt: '2026-10-25T10:16:00.000Z', status: 'complete' };
const asstMsg: Message = {
  id: 'a1',
  role: 'assistant',
  content: 'Most claims are settled within 10 working days [S1].',
  createdAt: '2026-10-25T10:16:01.500Z',
  status: 'complete',
  kind: 'answer',
  agentName: 'Claims specialist',
  citations: [{ ref: 'S1', title: 'Claims handling times', docId: 'd', url: 'https://kb.example.com/claims/handling' }],
};

describe('workflow dropdown', () => {
  it('is populated from GET /api/workflows - a new workflow appears with zero code change', async () => {
    const { fetchImpl } = fakeBackend({
      ...wfRoute([
        ...WORKFLOWS,
        { id: 'brand-new-workflow', displayName: 'Brand new thing', description: 'Added on the server only.' },
      ]),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    const select = await screen.findByRole('combobox', { name: 'Assistant' });
    const options = within(select).getAllByRole('option').map((o) => [o.getAttribute('value'), o.textContent]);
    expect(options).toEqual([
      ['support-ticket', 'Support ticket'],
      ['brand-new-workflow', 'Brand new thing'],
    ]);
  });

  it('creates the session with the selected workflow id, then posts the message', async () => {
    const user = userEvent.setup();
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute([...WORKFLOWS, { id: 'brand-new-workflow', displayName: 'Brand new thing', description: '' }]),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
      'POST /api/sessions': () => json(makeSession({ id: 's-new', workflowId: 'brand-new-workflow', title: null }), 201),
      'POST /api/sessions/s-new/messages': () => sseResponse([answerStream('s-new')]),
      'GET /api/sessions/s-new': () => json(makeDetail({ id: 's-new', workflowId: 'brand-new-workflow', title: 'Hello' }, [userMsg, asstMsg])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.selectOptions(await screen.findByRole('combobox', { name: 'Assistant' }), 'brand-new-workflow');
    await user.type(screen.getByLabelText('Message'), 'Hello{Enter}');
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/sessions/s-new/messages')).toBe(true));
    expect(calls.find((c) => c.method === 'POST' && c.url === '/api/sessions')?.body).toEqual({ workflowId: 'brand-new-workflow' });
    // streamed answer lands, with the agent badge and linked citation
    expect(await screen.findByText('Claims specialist', { selector: '.agent-badge' })).toBeInTheDocument();
    const link = await screen.findByRole('link', { name: 'Claims handling times' });
    expect(link).toHaveAttribute('href', 'https://kb.example.com/claims/handling');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
  });
});

describe('history sidebar', () => {
  it('lists sessions (null title -> placeholder, plain text) and loads more pages', async () => {
    const user = userEvent.setup();
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': (c: Call) =>
        c.url.includes('continuationToken=tok2')
          ? json({ items: [makeSession({ id: 's-3', title: 'Third' })], continuationToken: null })
          : json({
              items: [makeSession({ id: 's-1', title: '<b>My claim</b>' }), makeSession({ id: 's-2', title: null })],
              continuationToken: 'tok2',
            }),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    const nav = await screen.findByRole('navigation', { name: 'Conversation history' });
    expect(await within(nav).findByText('<b>My claim</b>')).toBeInTheDocument(); // not interpreted as HTML
    expect(within(nav).getByText('New chat', { selector: '.session-title' })).toBeInTheDocument();
    await user.click(within(nav).getByRole('button', { name: 'Load more' }));
    expect(await within(nav).findByText('Third')).toBeInTheDocument();
    expect(calls.some((c) => c.url.includes('continuationToken=tok2'))).toBe(true);
    expect(within(nav).queryByRole('button', { name: 'Load more' })).toBeNull();
  });

  it('opens a session and shows its messages', async () => {
    const user = userEvent.setup();
    const { fetchImpl } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(makeDetail({}, [userMsg, asstMsg])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    expect(await screen.findByText('How long does a claim take?')).toBeInTheDocument();
    expect(screen.getByText(/Most claims are settled within 10 working days/)).toBeInTheDocument();
    expect(screen.queryByRole('combobox', { name: 'Assistant' })).toBeNull(); // bound to its workflow
  });

  it('deletes a session after confirmation', async () => {
    const user = userEvent.setup();
    let deleted = false;
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: deleted ? [] : [makeSession()], continuationToken: null }),
      'DELETE /api/sessions/s-1': () => {
        deleted = true;
        return new Response(null, { status: 204 });
      },
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: 'Delete conversation: My claim from May' }));
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await user.click(screen.getByRole('button', { name: 'Confirm delete' }));
    await waitFor(() => expect(calls.some((c) => c.method === 'DELETE')).toBe(true));
    expect(await screen.findByText('No conversations yet.')).toBeInTheDocument();
  });
});

describe('follow-ups in an existing session', () => {
  it('posts to the existing session (no new session is created) and refreshes history', async () => {
    const user = userEvent.setup();
    let detail = makeDetail({}, [userMsg, asstMsg]);
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(detail),
      'POST /api/sessions': () => json(makeSession({ id: 'WRONG' }), 201),
      'POST /api/sessions/s-1/messages': () => {
        detail = makeDetail({}, [
          userMsg,
          asstMsg,
          { ...userMsg, id: 'u2', content: 'And for health assessments?' },
          { ...asstMsg, id: 'a2', content: 'Health assessments take 3 days.', citations: [] },
        ]);
        return sseResponse([answerStream('s-1')]);
      },
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await screen.findByText('How long does a claim take?');
    await user.type(screen.getByLabelText('Message'), 'And for health assessments?{Enter}');
    expect(await screen.findByText('Health assessments take 3 days.')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'POST' && c.url === '/api/sessions')).toHaveLength(0);
    expect(calls.find((c) => c.url === '/api/sessions/s-1/messages')?.body).toEqual({ content: 'And for health assessments?' });
    // the user message is shown once (live copy replaced by the persisted one)
    expect(screen.getAllByText('And for health assessments?')).toHaveLength(1);
  });

  it('shows a read-only notice when the workflow is no longer registered', async () => {
    const user = userEvent.setup();
    const { fetchImpl } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession({ workflowId: 'retired' })], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(makeDetail({ workflowId: 'retired' }, [userMsg, asstMsg])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    expect(await screen.findByText(/no longer available/)).toBeInTheDocument();
    expect(screen.queryByLabelText('Message')).toBeNull();
    await user.click(screen.getByRole('button', { name: 'Start a new chat' }));
    expect(await screen.findByRole('combobox', { name: 'Assistant' })).toBeInTheDocument();
  });

  it('409 workflow_unavailable from the server switches the session to read-only', async () => {
    const user = userEvent.setup();
    const { fetchImpl } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(makeDetail({}, [userMsg, asstMsg])),
      'POST /api/sessions/s-1/messages': () => problem(409, 'workflow_unavailable'),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await user.type(await screen.findByLabelText('Message'), 'hello{Enter}');
    expect(await screen.findByText(/no longer available/)).toBeInTheDocument();
  });

  it('429 disables sending until Retry-After elapses', async () => {
    const user = userEvent.setup();
    const { fetchImpl } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(makeDetail({}, [userMsg, asstMsg])),
      'POST /api/sessions/s-1/messages': () => problem(429, 'rate_limited', { 'Retry-After': '30' }),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await user.type(await screen.findByLabelText('Message'), 'hello{Enter}');
    expect(await screen.findByText(/Sending is paused for \d+ s/)).toBeInTheDocument();
    expect(screen.getByLabelText('Message')).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('sending messages too quickly');
  });

  it('a failed turn offers Retry which re-sends the same text', async () => {
    const user = userEvent.setup();
    let n = 0;
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => json(makeDetail({}, [userMsg, asstMsg])),
      'POST /api/sessions/s-1/messages': () =>
        n++ === 0 ? new Response('<html>bad gateway</html>', { status: 502, headers: { 'Content-Type': 'text/html' } }) : sseResponse([answerStream('s-1')]),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await user.type(await screen.findByLabelText('Message'), 'please answer{Enter}');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Something went wrong');
    expect(screen.getByText('Not sent')).toBeInTheDocument();
    await user.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/sessions/s-1/messages')).toHaveLength(2));
    expect(calls.filter((c) => c.url === '/api/sessions/s-1/messages').every((c) => (c.body as { content: string }).content === 'please answer')).toBe(true);
  });
});

describe('retry after an accepted turn', () => {
  it.each([
    ['an error event after session', () => frame(1, 'session', { sessionId: 's-1' }) + frame(2, 'error', { code: 'upstream_throttled', message: 'The service is busy. Please try again in a moment.' })],
    ['a stream that ends without a terminal event', () => frame(1, 'session', { sessionId: 's-1' }) + frame(2, 'token', { text: 'partial' })],
  ])('offers "Ask again" with a history note, refetches history, and no "Not sent" (%s)', async (_name, body) => {
    const user = userEvent.setup();
    let n = 0;
    let gets = 0;
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
      'GET /api/sessions/s-1': () => {
        gets++;
        return json(makeDetail({}, [userMsg, asstMsg]));
      },
      'POST /api/sessions/s-1/messages': () => (n++ === 0 ? sseResponse([body()]) : sseResponse([answerStream('s-1')])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await user.type(await screen.findByLabelText('Message'), 'please answer{Enter}');
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('stays in the conversation history');
    expect(screen.queryByText('Not sent')).toBeNull();
    expect(within(alert).queryByRole('button', { name: 'Retry' })).toBeNull();
    const getsBefore = gets;
    expect(getsBefore).toBeGreaterThanOrEqual(2); // initial load + refetch after the accepted failure
    await user.click(within(alert).getByRole('button', { name: 'Ask again' }));
    await waitFor(() => expect(calls.filter((c) => c.url === '/api/sessions/s-1/messages')).toHaveLength(2));
    await waitFor(() => expect(gets).toBeGreaterThan(getsBefore));
  });
});

describe('session creation failures keep the message', () => {
  it.each([
    [429, () => problem(429, 'rate_limited', { 'Retry-After': '30' })],
    [500, () => problem(500, 'internal_error')],
  ])('POST /api/sessions %i: text stays in the composer and Retry works', async (_status, failure) => {
    const user = userEvent.setup();
    let n = 0;
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
      'POST /api/sessions': () => (n++ === 0 ? failure() : json(makeSession({ id: 's-9', title: null }), 201)),
      'POST /api/sessions/s-9/messages': () => sseResponse([answerStream('s-9')]),
      'GET /api/sessions/s-9': () => json(makeDetail({ id: 's-9' }, [userMsg, asstMsg])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await screen.findByRole('combobox', { name: 'Assistant' });
    await user.type(await screen.findByLabelText('Message'), 'my precious text{Enter}');
    const alert = await screen.findByRole('alert');
    expect(screen.getByLabelText('Message')).toHaveValue('my precious text');
    expect(calls.some((c) => c.url.endsWith('/messages'))).toBe(false);
    await user.click(within(alert).getByRole('button', { name: 'Retry' }));
    await waitFor(() => expect(calls.some((c) => c.url === '/api/sessions/s-9/messages')).toBe(true));
    expect(calls.find((c) => c.url === '/api/sessions/s-9/messages')?.body).toEqual({ content: 'my precious text' });
    await waitFor(() => expect(screen.getByLabelText('Message')).toHaveValue(''));
  });
});

describe('streamed text accessibility', () => {
  it('streamed answer sits in a polite live region marked busy while streaming', async () => {
    const user = userEvent.setup();
    const { fetchImpl } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
      'POST /api/sessions': () => json(makeSession({ id: 's-9', title: null }), 201),
      'POST /api/sessions/s-9/messages': () => sseResponse([answerStream('s-9')]),
      'GET /api/sessions/s-9': () => json(makeDetail({ id: 's-9' }, [userMsg, asstMsg])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await screen.findByRole('combobox', { name: 'Assistant' });
    await user.type(await screen.findByLabelText('Message'), 'q{Enter}');
    const live = await screen.findByRole('log');
    expect(live).toHaveAttribute('aria-live', 'polite');
    await waitFor(() => expect(live).toHaveAttribute('aria-busy', 'false'));
  });

  it('send is reachable and operable by keyboard (Tab + Enter)', async () => {
    const user = userEvent.setup();
    const { fetchImpl, calls } = fakeBackend({
      ...wfRoute(),
      'GET /api/sessions': () => json({ items: [], continuationToken: null }),
      'POST /api/sessions': () => json(makeSession({ id: 's-9', title: null }), 201),
      'POST /api/sessions/s-9/messages': () => sseResponse([answerStream('s-9')]),
      'GET /api/sessions/s-9': () => json(makeDetail({ id: 's-9' }, [])),
    });
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await screen.findByRole('combobox', { name: 'Assistant' });
    const input = await screen.findByLabelText('Message');
    await user.type(input, 'keyboard only');
    await user.tab();
    expect(screen.getByRole('button', { name: 'Send' })).toHaveFocus();
    await user.keyboard('{Enter}');
    await waitFor(() => expect(calls.some((c) => c.url === '/api/sessions/s-9/messages')).toBe(true));
  });
});

describe('LoginPage', () => {
  it('shows neutral default copy, or the configured title and tagline', () => {
    const { unmount } = render(
      <AuthContext.Provider value={fakeAuth({ status: 'anonymous' })}>
        <LoginPage providers={[]} />
      </AuthContext.Provider>,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Assistant');
    expect(screen.getByText('Sign in to continue.')).toBeInTheDocument();
    unmount();
    render(
      <AuthContext.Provider value={fakeAuth({ status: 'anonymous' })}>
        <LoginPage providers={[]} branding={{ title: 'Acme help', tagline: 'Welcome back.' }} />
      </AuthContext.Provider>,
    );
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Acme help');
    expect(screen.getByText('Welcome back.')).toBeInTheDocument();
  });

  it('renders one button per configured provider and passes its domain hint', async () => {
    const user = userEvent.setup();
    const login = vi.fn(async () => undefined);
    render(
      <AuthContext.Provider value={fakeAuth({ status: 'anonymous', login })}>
        <LoginPage
          providers={[
            { id: 'google', label: 'Continue with Google', domainHint: 'google' },
            { id: 'facebook', label: 'Continue with Facebook', domainHint: 'facebook' },
            { id: 'newidp', label: 'Continue with NewIdp', domainHint: 'newidp' },
          ]}
        />
      </AuthContext.Provider>,
    );
    expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Continue with Google', 'Continue with Facebook', 'Continue with NewIdp']);
    await user.click(screen.getByRole('button', { name: 'Continue with Facebook' }));
    expect(login).toHaveBeenCalledWith('facebook');
  });

  it('falls back to a generic sign-in (hosted chooser) when no providers are configured, and shows session expiry', async () => {
    const user = userEvent.setup();
    const login = vi.fn(async () => undefined);
    render(
      <AuthContext.Provider value={fakeAuth({ status: 'anonymous', login, sessionExpired: true })}>
        <LoginPage providers={[]} />
      </AuthContext.Provider>,
    );
    expect(screen.getByRole('alert')).toHaveTextContent('session has expired');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    expect(login).toHaveBeenCalledWith(undefined);
  });
});
