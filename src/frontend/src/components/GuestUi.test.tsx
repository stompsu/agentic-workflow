import { describe, expect, it, vi } from 'vitest';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { Workspace } from './Workspace';
import { LoginPage } from './LoginPage';
import { AuthContext } from '../auth/AuthContext';
import { ApiError } from '../api/errors';
import { WORKFLOWS, fakeAuth, fakeBackend, frame, json, makeClient, makeDetail, makeSession, openSseResponse, problem, renderWithApi, type Call } from '../test/helpers';

const GUEST = { mode: 'guest', userName: null } as const;
const LIMIT = 'You have reached the guest limit. Sign in to keep chatting.';
const PROVIDERS = [
  { id: 'google', label: 'Continue with Google', domainHint: 'google' },
  { id: 'facebook', label: 'Continue with Facebook', domainHint: 'facebook' },
];

function renderLogin(auth: Partial<Parameters<typeof fakeAuth>[0]> = {}, props: Partial<Parameters<typeof LoginPage>[0]> = {}) {
  return render(
    <AuthContext.Provider value={fakeAuth({ status: 'anonymous', ...auth })}>
      <LoginPage providers={PROVIDERS} guest={{ enabled: true, label: 'Continue as guest' }} {...props} />
    </AuthContext.Provider>,
  );
}

describe('LoginPage guest option', () => {
  it('has no guest button when auth.guest.enabled is false or absent', () => {
    const { unmount } = renderLogin({}, { guest: { enabled: false, label: 'Continue as guest' } });
    expect(screen.queryByRole('button', { name: 'Continue as guest' })).not.toBeInTheDocument();
    expect(screen.queryByText(/No account needed/)).not.toBeInTheDocument();
    unmount();
    renderLogin({}, { guest: undefined });
    expect(screen.queryByRole('button', { name: 'Continue as guest' })).not.toBeInTheDocument();
  });

  it('shows the third button below the providers with the caption built from config hours', () => {
    renderLogin({}, { info: { guestSessionHours: 6, guestRetentionHours: 8 } });
    expect(screen.getAllByRole('button').map((b) => b.textContent)).toEqual(['Continue with Google', 'Continue with Facebook', 'Continue as guest']);
    expect(screen.getByRole('button', { name: 'Continue as guest' })).toHaveAccessibleDescription(
      'No account needed. Your chats stay only in this browser for up to 6 hours and are deleted automatically within 8 hours after your last message.',
    );
  });

  it('uses the default 4 / 5 hours and a configured label', () => {
    renderLogin({}, { guest: { enabled: true, label: 'Try without account' } });
    expect(screen.getByRole('button', { name: 'Try without account' })).toHaveAccessibleDescription(/up to 4 hours.*within 5 hours/);
  });

  it('click starts the guest session; no social login is triggered', async () => {
    const user = userEvent.setup();
    const loginAsGuest = vi.fn(async () => undefined);
    const login = vi.fn(async () => undefined);
    renderLogin({ loginAsGuest, login });
    await user.click(screen.getByRole('button', { name: 'Continue as guest' }));
    expect(loginAsGuest).toHaveBeenCalledTimes(1);
    expect(login).not.toHaveBeenCalled();
  });

  it('429 rate_limited on creation honors Retry-After: message, countdown, button disabled', async () => {
    const user = userEvent.setup();
    const loginAsGuest = vi.fn(async () => {
      throw new ApiError({ status: 429, code: 'rate_limited', retryAfterSeconds: 30 });
    });
    renderLogin({ loginAsGuest });
    await user.click(screen.getByRole('button', { name: 'Continue as guest' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Too many guest sessions are being started right now. Try again in 30 seconds.');
    expect(screen.getByRole('button', { name: 'Continue as guest' })).toBeDisabled();
    expect(loginAsGuest).toHaveBeenCalledTimes(1);
  });

  it.each([
    ['guest_access_disabled', 404, 'Guest access is not available right now.'],
    ['bot_check_failed', 400, 'Guest sign-in could not be verified. Please try again.'],
    ['internal_error', 500, 'Something went wrong. Please try again.'],
  ])('error %s is shown and the button is usable again', async (code, status, text) => {
    const user = userEvent.setup();
    renderLogin({
      loginAsGuest: async () => {
        throw new ApiError({ status, code });
      },
    });
    await user.click(screen.getByRole('button', { name: 'Continue as guest' }));
    expect(await screen.findByRole('alert')).toHaveTextContent(text);
    expect(screen.getByRole('button', { name: 'Continue as guest' })).toBeEnabled();
  });

  it.each([
    ['expired', 'Your guest session has ended.'],
    ['deleted', 'Your guest session has ended and its chats were deleted.'],
    ['delete-failed', 'Your guest session has ended. Remaining chats will be deleted automatically within 7 hours.'],
  ] as const)('notice %s', (guestNotice, text) => {
    renderLogin({ guestNotice }, { info: { guestRetentionHours: 7 } });
    expect(screen.getByRole('status')).toHaveTextContent(text);
  });
});

function guestSetup(over: { delete?: (call: Call, attempt: number) => Response; sessionsPost?: () => Response; messagesPost?: () => Response } = {}) {
  let attempt = 0;
  const backend = fakeBackend({
    'GET /api/workflows': () => json({ items: WORKFLOWS }),
    'GET /api/sessions': () => json({ items: [makeSession()], continuationToken: null }),
    'GET /api/sessions/s-1': () => json(makeDetail({}, [])),
    'GET /api/sessions/s-9': () => json(makeDetail({ id: 's-9' }, [])),
    'DELETE /api/me/data': (call) => (over.delete ?? (() => new Response(null, { status: 204 })))(call, ++attempt),
    ...(over.sessionsPost && { 'POST /api/sessions': over.sessionsPost }),
    ...(over.messagesPost && { 'POST /api/sessions/s-9/messages': over.messagesPost }),
  });
  return { ...backend, api: makeClient(backend.fetchImpl) };
}

describe('Workspace in guest mode', () => {
  it('shows the Guest badge instead of the name, no Sign out, and the guest retention note', async () => {
    const { api } = guestSetup();
    renderWithApi(<Workspace info={{ retentionDays: 90, guestRetentionHours: 5 }} />, api, fakeAuth(GUEST));
    const badge = screen.getByText('Guest');
    expect(badge).toHaveAttribute('title', 'Guest chats are not saved to an account. Close the browser and they are gone.');
    expect(badge).toHaveAccessibleDescription('Guest chats are not saved to an account. Close the browser and they are gone.');
    expect(screen.queryByText('Test User')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Sign out' })).not.toBeInTheDocument();
    expect(await screen.findByText('Guest chats are deleted automatically within 5 hours after your last message.')).toBeInTheDocument();
    expect(screen.queryByText(/Chats are kept for/)).not.toBeInTheDocument();
  });

  it('account menu offers Sign in and End guest session, not Delete my data', async () => {
    const user = userEvent.setup();
    const { api } = guestSetup();
    renderWithApi(<Workspace />, api, fakeAuth(GUEST));
    await user.click(screen.getByRole('button', { name: 'Account' }));
    const menu = document.getElementById('account-menu')!;
    expect(within(menu).getAllByRole('button').map((b) => b.textContent)).toEqual(['Sign in', 'End guest session']);
  });

  it('social users see neither badge nor guest menu', async () => {
    const user = userEvent.setup();
    const { api } = guestSetup();
    renderWithApi(<Workspace />, api);
    expect(screen.queryByText('Guest')).not.toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Account' }));
    expect(screen.getByRole('button', { name: 'Delete my data' })).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'End guest session' })).not.toBeInTheDocument();
  });

  async function openEnd(user: ReturnType<typeof userEvent.setup>) {
    await user.click(screen.getByRole('button', { name: 'Account' }));
    await user.click(screen.getByRole('button', { name: 'End guest session' }));
    return screen.getByRole('dialog', { name: 'End guest session?' });
  }

  it('End guest session: confirm dialog, DELETE /api/me/data, then endGuest("deleted")', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const { api, calls } = guestSetup();
    renderWithApi(<Workspace />, api, fakeAuth({ ...GUEST, endGuest }));
    const dialog = await openEnd(user);
    expect(dialog).toHaveTextContent('This permanently deletes all your guest chats. This cannot be undone.');
    expect(within(dialog).getByRole('button', { name: 'Cancel' })).toHaveFocus();
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await user.click(within(dialog).getByRole('button', { name: 'End guest session' }));
    await waitFor(() => expect(endGuest).toHaveBeenCalledWith('deleted'));
    const del = calls.filter((c) => c.method === 'DELETE');
    expect(del).toHaveLength(1);
    expect(del[0]!.headers.get('Authorization')).toBe('Bearer test-token');
  });

  it('Cancel and Escape do nothing', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const { api, calls } = guestSetup();
    renderWithApi(<Workspace />, api, fakeAuth({ ...GUEST, endGuest }));
    await user.click(within(await openEnd(user)).getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    await openEnd(user);
    await user.keyboard('{Escape}');
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(endGuest).not.toHaveBeenCalled();
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
  });

  it('DELETE failure (500) still ends the guest locally with the delete-failed outcome', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const { api, calls } = guestSetup({ delete: () => problem(500, 'internal_error') });
    renderWithApi(<Workspace />, api, fakeAuth({ ...GUEST, endGuest }));
    await user.click(within(await openEnd(user)).getByRole('button', { name: 'End guest session' }));
    await waitFor(() => expect(endGuest).toHaveBeenCalledWith('delete-failed'));
    expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
  });

  it('503 deletion_incomplete is retried (max 5 attempts) before giving up with delete-failed', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const { api, calls } = guestSetup({ delete: () => problem(503, 'deletion_incomplete', { 'Retry-After': '1' }) });
    renderWithApi(<Workspace />, api, fakeAuth({ ...GUEST, endGuest }));
    await user.click(within(await openEnd(user)).getByRole('button', { name: 'End guest session' }));
    await waitFor(() => expect(endGuest).toHaveBeenCalledWith('delete-failed'));
    expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(5);
  });

  it('aborts the running stream before the DELETE is sent', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const backend = guestSetup();
    let streamSignal: AbortSignal | null | undefined;
    let abortedWhenDeleteSent: boolean | undefined;
    const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString();
      if (init?.method === 'POST' && url === '/api/sessions/s-1/messages') {
        streamSignal = init.signal;
        return openSseResponse([frame(1, 'session', { sessionId: 's-1' }), frame(2, 'agent', { name: 'Claims specialist' }), frame(3, 'token', { text: 'Working on' })], init.signal);
      }
      if (init?.method === 'DELETE' && url === '/api/me/data') abortedWhenDeleteSent = streamSignal?.aborted;
      return backend.fetchImpl(input, init);
    }) as typeof fetch;
    renderWithApi(<Workspace />, makeClient(fetchImpl), fakeAuth({ ...GUEST, endGuest }));
    await user.click(await screen.findByRole('button', { name: /^My claim from May/ }));
    await user.type(await screen.findByLabelText('Message'), 'Question{Enter}');
    await screen.findByText(/Working on/);
    await user.click(within(await openEnd(user)).getByRole('button', { name: 'End guest session' }));
    await waitFor(() => expect(endGuest).toHaveBeenCalledWith('deleted'));
    expect(streamSignal?.aborted).toBe(true);
    expect(abortedWhenDeleteSent).toBe(true);
  });

  it('Sign in warns that guest chats are not moved; confirming with a provider ends the guest silently, then logs in with its hint', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const login = vi.fn(async () => undefined);
    const { api, calls } = guestSetup();
    renderWithApi(<Workspace providers={PROVIDERS} />, api, fakeAuth({ ...GUEST, endGuest, login }));
    await user.click(screen.getByRole('button', { name: 'Account' }));
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    const dialog = screen.getByRole('dialog', { name: 'Sign in?' });
    expect(dialog).toHaveTextContent('Signing in starts a separate account. Your guest chats will not be moved to it and will be deleted.');
    expect(login).not.toHaveBeenCalled();
    await user.click(within(dialog).getByRole('button', { name: 'Continue with Facebook' }));
    await waitFor(() => expect(login).toHaveBeenCalledWith('facebook'));
    expect(endGuest).toHaveBeenCalledWith('silent');
    expect(calls.filter((c) => c.method === 'DELETE')).toHaveLength(1);
  });

  it('Sign in dialog Cancel keeps the guest', async () => {
    const user = userEvent.setup();
    const endGuest = vi.fn();
    const { api } = guestSetup();
    renderWithApi(<Workspace providers={PROVIDERS} />, api, fakeAuth({ ...GUEST, endGuest }));
    await user.click(screen.getByRole('button', { name: 'Account' }));
    await user.click(screen.getByRole('button', { name: 'Sign in' }));
    await user.click(screen.getByRole('button', { name: 'Cancel' }));
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();
    expect(endGuest).not.toHaveBeenCalled();
  });
});

describe('guest quotas', () => {
  it('turn quota (429 guest_quota_exceeded on POST messages) disables the composer with the sign-in text, no Retry-After wait', async () => {
    const user = userEvent.setup();
    const { api, calls } = guestSetup({
      sessionsPost: () => json(makeSession({ id: 's-9', title: null }), 201),
      messagesPost: () => problem(429, 'guest_quota_exceeded'),
    });
    renderWithApi(<Workspace />, api, fakeAuth(GUEST));
    await screen.findByRole('combobox', { name: 'Assistant' });
    await user.type(await screen.findByLabelText('Message'), 'one more{Enter}');
    await waitFor(() => expect(screen.getByLabelText('Message')).toBeDisabled());
    expect(screen.getByText(LIMIT)).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /Retry|Ask again/ })).not.toBeInTheDocument();
    expect(calls.filter((c) => c.url.endsWith('/messages'))).toHaveLength(1);
    // history stays readable
    await user.click(screen.getByRole('button', { name: /^My claim from May/ }));
    expect(screen.getByLabelText('Message')).toBeDisabled();
  });

  it('session quota (429 guest_quota_exceeded on POST sessions) disables New chat and the new-chat composer; draft is kept', async () => {
    const user = userEvent.setup();
    const { api, calls } = guestSetup({ sessionsPost: () => problem(429, 'guest_quota_exceeded') });
    renderWithApi(<Workspace />, api, fakeAuth(GUEST));
    await screen.findByRole('combobox', { name: 'Assistant' });
    await user.type(await screen.findByLabelText('Message'), 'my draft{Enter}');
    await waitFor(() => expect(screen.getByRole('button', { name: 'New chat' })).toBeDisabled());
    expect(screen.getAllByText(LIMIT).length).toBeGreaterThan(0);
    expect(screen.getByLabelText('Message')).toBeDisabled();
    expect(screen.getByLabelText('Message')).toHaveValue('my draft');
    expect(calls.filter((c) => c.method === 'POST')).toHaveLength(1);
    // an existing session is still usable (turn quota is separate)
    await user.click(screen.getByRole('button', { name: /^My claim from May/ }));
    expect(await screen.findByLabelText('Message')).toBeEnabled();
  });
});
