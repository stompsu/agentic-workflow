import { describe, expect, it } from 'vitest';
import { screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Workspace } from './Workspace';
import { WORKFLOWS, fakeBackend, frame, json, makeClient, makeDetail, makeSession, openSseResponse, problem, renderWithApi, type Call } from '../test/helpers';
import { queryKeys } from '../api/queryKeys';
import { DELETE_MY_DATA_MAX_ATTEMPTS } from '../api/client';

const DELETED = 'Your chat history was deleted. Your sign-in account was not deleted.';

/** Backend with one session until DELETE /api/me/data answers 204; `deleteHandler` decides each attempt. */
function setup(deleteHandler: (call: Call, attempt: number) => Response, extraRoutes: Parameters<typeof fakeBackend>[0] = {}) {
  let deleted = false;
  let attempt = 0;
  const backend = fakeBackend({
    'GET /api/workflows': () => json({ items: WORKFLOWS }),
    'GET /api/sessions': () => json({ items: deleted ? [] : [makeSession()], continuationToken: null }),
    'GET /api/sessions/s-1': () =>
      json(
        makeDetail({}, [
          {
            id: 'u1',
            role: 'user',
            content: 'Hi there',
            createdAt: '2026-10-25T10:16:00.000Z',
            status: 'complete',
          },
        ]),
      ),
    'DELETE /api/me/data': (call) => {
      const res = deleteHandler(call, ++attempt);
      if (res.status === 204) deleted = true;
      return res;
    },
    ...extraRoutes,
  });
  const sleeps: number[] = [];
  const api = makeClient(backend.fetchImpl, {
    sleep: async (ms) => {
      sleeps.push(ms);
    },
  });
  return { ...backend, api, sleeps };
}

const deleteCalls = (calls: Call[]) => calls.filter((c) => c.method === 'DELETE' && c.url === '/api/me/data');

async function openDialog(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByRole('button', { name: 'Account' }));
  await user.click(screen.getByRole('button', { name: 'Delete my data' }));
  return screen.getByRole('dialog', { name: 'Delete my data?' });
}

describe('delete my data', () => {
  it('confirmation dialog explains what is deleted; 204 clears history, shows the notice and contact, stays signed in', async () => {
    const user = userEvent.setup();
    const { api, calls } = setup(() => new Response(null, { status: 204 }));
    const { queryClient } = renderWithApi(<Workspace info={{ supportContact: 'help@example.com' }} />, api);
    await screen.findByText('My claim from May');
    // open a session so its detail is cached
    await user.click(screen.getByRole('button', { name: /^My claim from May/ }));
    await screen.findByText('Hi there');
    expect(queryClient.getQueryData(queryKeys.session('s-1'))).toBeDefined();

    const dialog = await openDialog(user);
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(dialog).toHaveTextContent(/permanently deleted/);
    expect(dialog).toHaveTextContent(/cannot be undone/);
    expect(dialog).toHaveTextContent(/sign-in account is not deleted/);
    expect(deleteCalls(calls)).toHaveLength(0);
    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus();

    await user.click(within(dialog).getByRole('button', { name: 'Delete my data' }));

    await waitFor(() => expect(screen.queryByRole('dialog')).not.toBeInTheDocument());
    const [call] = deleteCalls(calls);
    expect(call!.headers.get('Authorization')).toBe('Bearer test-token');
    expect(deleteCalls(calls)).toHaveLength(1);

    // notice + contact, new-chat view, cache cleared, empty history
    const notice = await screen.findByText(new RegExp(DELETED));
    expect(notice).toHaveTextContent('contact help@example.com');
    expect(queryClient.getQueryData(queryKeys.session('s-1'))).toBeUndefined();
    expect(await screen.findByText('No conversations yet.')).toBeInTheDocument();
    expect(screen.queryByText('My claim from May')).not.toBeInTheDocument();
    expect(screen.queryByText('Hi there')).not.toBeInTheDocument();
    expect(await screen.findByRole('combobox', { name: 'Assistant' })).toBeInTheDocument();
    // still signed in
    expect(screen.getByRole('button', { name: 'Sign out' })).toBeInTheDocument();
    expect(screen.getByText('Test User')).toBeInTheDocument();
  });

  it('notice without a configured contact has no contact text', async () => {
    const user = userEvent.setup();
    const { api } = setup(() => new Response(null, { status: 204 }));
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    const notice = await screen.findByText(new RegExp(DELETED));
    expect(notice.textContent).toBe(DELETED);
  });

  it('cancel (button and Escape) does nothing', async () => {
    const user = userEvent.setup();
    const { api, calls } = setup(() => new Response(null, { status: 204 }));
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    const dialog = await openDialog(user);
    await user.click(within(dialog).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Account' })).toHaveFocus();
    await openDialog(user);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(deleteCalls(calls)).toHaveLength(0);
    expect(screen.queryByText(new RegExp(DELETED))).not.toBeInTheDocument();
    expect(screen.getByText('My claim from May')).toBeInTheDocument();
  });

  it('503 deletion_incomplete: waits Retry-After and repeats, then 204', async () => {
    const user = userEvent.setup();
    const { api, calls, sleeps } = setup((_c, n) =>
      n < 3 ? problem(503, 'deletion_incomplete', { 'Retry-After': '2' }) : new Response(null, { status: 204 }),
    );
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    expect(await screen.findByText(new RegExp(DELETED))).toBeInTheDocument();
    expect(deleteCalls(calls)).toHaveLength(3);
    expect(sleeps).toEqual([2000, 2000]);
  });

  it('5 x 503: gives up after 5 attempts with the taking-longer message, nothing is cleared, can try again', async () => {
    const user = userEvent.setup();
    let failing = true;
    const { api, calls, sleeps } = setup(() => (failing ? problem(503, 'deletion_incomplete', { 'Retry-After': '1' }) : new Response(null, { status: 204 })));
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Deleting your data is taking longer than expected. Please try again in a minute.');
    expect(deleteCalls(calls)).toHaveLength(DELETE_MY_DATA_MAX_ATTEMPTS);
    expect(screen.getByRole('button', { name: 'Try again' })).toHaveFocus();
    expect(sleeps).toHaveLength(DELETE_MY_DATA_MAX_ATTEMPTS - 1);
    expect(screen.queryByText(new RegExp(DELETED))).not.toBeInTheDocument();
    // dialog is still there; trying again later works
    failing = false;
    await user.click(screen.getByRole('button', { name: 'Try again' }));
    expect(await screen.findByText(new RegExp(DELETED))).toBeInTheDocument();
  });

  it('notice live region exists (empty) before deletion and holds the text after', async () => {
    const user = userEvent.setup();
    const { api } = setup(() => new Response(null, { status: 204 }));
    const { container } = renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    const region = container.querySelector<HTMLElement>('.notice-region')!;
    expect(region).toBeEmptyDOMElement();
    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    await screen.findByText(new RegExp(DELETED));
    expect(region).toHaveTextContent(DELETED);
    expect(region).toHaveFocus();
  });

  it('background is inert while the dialog is open and again interactive after closing', async () => {
    const user = userEvent.setup();
    const { api } = setup(() => new Response(null, { status: 204 }));
    const { container } = renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    const app = container.querySelector('.app')!;
    expect(app).not.toHaveAttribute('inert');
    const dialog = await openDialog(user);
    expect(app).toHaveAttribute('inert');
    expect(app).not.toContainElement(dialog);
    await user.keyboard('{Escape}');
    expect(app).not.toHaveAttribute('inert');
  });

  it('keeps focus in the dialog while deleting: Tab cycles, Escape is ignored', async () => {
    const user = userEvent.setup();
    let release!: () => void;
    const gate = new Promise<void>((r) => (release = r));
    const backend = setup(() => new Response(null, { status: 204 }));
    const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
      if (init?.method === 'DELETE') await gate;
      return backend.fetchImpl(input, init);
    }) as typeof fetch;
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await screen.findByText('My claim from May');
    const dialog = await openDialog(user);
    await user.click(within(dialog).getByRole('button', { name: 'Delete my data' }));
    await within(dialog).findByText('Deleting your data...');
    const cancel = within(dialog).getByRole('button', { name: 'Close' });
    const del = within(dialog).getByRole('button', { name: 'Delete my data' });
    expect(del).toHaveAttribute('aria-disabled', 'true');
    expect(dialog.contains(document.activeElement)).toBe(true);
    for (let i = 0; i < 5; i++) {
      await user.tab();
      expect(dialog.contains(document.activeElement)).toBe(true);
    }
    await user.tab({ shift: true });
    expect(dialog.contains(document.activeElement)).toBe(true);
    await user.keyboard('{Escape}');
    await user.click(cancel);
    expect(screen.getByRole('dialog')).toBeInTheDocument();
    expect(dialog.contains(document.activeElement)).toBe(true);
    release();
    await screen.findByText(new RegExp(DELETED));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
  });

  it('other errors use the ProblemDetails mapping', async () => {
    const user = userEvent.setup();
    const { api, calls } = setup(() => problem(500, 'internal_error'));
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong. Please try again.');
    expect(deleteCalls(calls)).toHaveLength(1);
  });

  it('aborts the in-flight stream before the DELETE request is sent', async () => {
    const user = userEvent.setup();
    let streamSignal: AbortSignal | null | undefined;
    let abortedWhenDeleteSent: boolean | undefined;
    const backend = setup(() => new Response(null, { status: 204 }));
    // Custom fetch: the message stream stays open until its signal aborts.
    const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (init?.method === 'POST' && url === '/api/sessions/s-1/messages') {
        streamSignal = init.signal;
        return openSseResponse(
          [frame(1, 'session', { sessionId: 's-1' }), frame(2, 'agent', { name: 'Claims specialist' }), frame(3, 'token', { text: 'Working on' })],
          init.signal,
        );
      }
      if (init?.method === 'DELETE' && url === '/api/me/data') abortedWhenDeleteSent = streamSignal?.aborted;
      return backend.fetchImpl(input, init);
    }) as typeof fetch;
    renderWithApi(<Workspace />, makeClient(fetchImpl, { sleep: async () => undefined }));

    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await screen.findByText('Hi there');
    await user.type(screen.getByLabelText('Message'), 'Question{Enter}');
    await screen.findByText(/Working on/);
    expect(streamSignal?.aborted).toBe(false);

    await user.click(
      within(await openDialog(user)).getByRole('button', {
        name: 'Delete my data',
      }),
    );
    expect(await screen.findByText(new RegExp(DELETED))).toBeInTheDocument();
    expect(streamSignal?.aborted).toBe(true);
    expect(abortedWhenDeleteSent).toBe(true);
    expect(screen.queryByText(/Working on/)).not.toBeInTheDocument();
  });

  it('does not abort the stream when the user cancels', async () => {
    const user = userEvent.setup();
    let streamSignal: AbortSignal | null | undefined;
    const backend = setup(() => new Response(null, { status: 204 }));
    const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (init?.method === 'POST' && url === '/api/sessions/s-1/messages') {
        streamSignal = init.signal;
        return openSseResponse(
          [frame(1, 'session', { sessionId: 's-1' }), frame(2, 'agent', { name: 'Claims specialist' }), frame(3, 'token', { text: 'Working on' })],
          init.signal,
        );
      }
      return backend.fetchImpl(input, init);
    }) as typeof fetch;
    renderWithApi(<Workspace />, makeClient(fetchImpl));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await screen.findByText('Hi there');
    await user.type(screen.getByLabelText('Message'), 'Question{Enter}');
    await screen.findByText(/Working on/);
    await user.click(within(await openDialog(user)).getByRole('button', { name: 'Cancel' }));
    expect(streamSignal?.aborted).toBe(false);
    expect(screen.getByText(/Working on/)).toBeInTheDocument();
  });

  it('shows retention info only when configured', async () => {
    const { api } = setup(() => new Response(null, { status: 204 }));
    const { unmount } = renderWithApi(<Workspace info={{ retentionDays: 90 }} />, api);
    expect(await screen.findByText('Chats are kept for 90 days after last activity.')).toBeInTheDocument();
    unmount();
    renderWithApi(<Workspace />, api);
    await screen.findByText('My claim from May');
    expect(screen.queryByText(/Chats are kept/)).not.toBeInTheDocument();
  });
});
