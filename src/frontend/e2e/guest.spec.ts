import { expect, test, type Page, type Route } from '@playwright/test';

const SESSION_ID = '0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10';
const NOW = '2026-10-25T10:15:30.123Z';
const GUEST_TOKEN = 'guest-e2e-token';
const session = { id: SESSION_ID, workflowId: 'support-ticket', title: 'How long does a claim take?', createdAt: NOW, updatedAt: NOW };

const sse = [
  `id: 1\nevent: session\ndata: ${JSON.stringify({ sessionId: SESSION_ID })}\n\n`,
  `id: 2\nevent: agent\ndata: ${JSON.stringify({ name: 'Claims specialist' })}\n\n`,
  `id: 3\nevent: token\ndata: ${JSON.stringify({ text: 'Most claims are settled within 10 working days.' })}\n\n`,
  `id: 4\nevent: done\ndata: ${JSON.stringify({ messageId: 'msg-1' })}\n\n`,
].join('');

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

/** Runtime config with guest access on (the placeholder public/config.json has it off). */
async function enableGuest(page: Page) {
  await page.route('**/config.json', async (route) => {
    const res = await route.fetch();
    const config = (await res.json()) as { auth: Record<string, unknown>; app: Record<string, unknown> };
    config.auth = { ...config.auth, guest: { enabled: true, label: 'Continue as guest' } };
    config.app = { ...config.app, guestSessionHours: 4, guestRetentionHours: 5 };
    await json(route, config);
  });
}

test('guest: Continue as guest -> badge -> send -> streamed answer -> End guest session -> login notice', async ({ page }) => {
  let created = false;
  let ended = false;
  let tokenRequests = 0;
  const authHeaders: (string | undefined)[] = [];
  await enableGuest(page);

  await page.route((url) => url.pathname.startsWith('/api/'), async (route) => {
    const req = route.request();
    const key = `${req.method()} ${new URL(req.url()).pathname}`;
    if (key === 'POST /api/guest/token') {
      tokenRequests += 1;
      expect(req.headers()['authorization']).toBeUndefined();
      expect(req.postDataJSON()).toEqual({});
      return json(route, { accessToken: GUEST_TOKEN, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 4 * 3_600_000).toISOString() });
    }
    authHeaders.push(req.headers()['authorization']);
    switch (key) {
      case 'GET /api/workflows':
        return json(route, { items: [{ id: 'support-ticket', displayName: 'Support ticket', description: 'Help.' }] });
      case 'GET /api/sessions':
        return json(route, { items: created && !ended ? [session] : [], continuationToken: null });
      case 'POST /api/sessions':
        created = true;
        return json(route, session, 201);
      case `POST /api/sessions/${SESSION_ID}/messages`:
        return route.fulfill({ status: 200, headers: { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache, no-transform' }, body: sse });
      case `GET /api/sessions/${SESSION_ID}`:
        return json(route, {
          ...session,
          hasMoreMessages: false,
          messages: [
            { id: 'u1', role: 'user', content: 'How long does a claim take?', createdAt: NOW, status: 'complete' },
            { id: 'msg-1', role: 'assistant', content: 'Most claims are settled within 10 working days.', createdAt: NOW, status: 'complete', kind: 'answer', agentName: 'Claims specialist' },
          ],
        });
      case 'DELETE /api/me/data':
        ended = true;
        return route.fulfill({ status: 204 });
      default:
        return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404, code: 'not_found' }) });
    }
  });

  await page.goto('/');
  await expect(page.getByText(/No account needed. Your chats stay only in this browser for up to 4 hours/)).toBeVisible();
  await page.getByRole('button', { name: 'Continue as guest' }).click();

  // Guest badge instead of a name; no Sign out
  await expect(page.getByText('Guest', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign out' })).toHaveCount(0);
  expect(tokenRequests).toBe(1);
  const stored = await page.evaluate(() => sessionStorage.getItem('supportchat.guest'));
  expect(JSON.parse(stored!).accessToken).toBe(GUEST_TOKEN);
  expect(await page.evaluate(() => localStorage.getItem('supportchat.guest'))).toBeNull();

  await page.getByRole('textbox', { name: 'Message' }).fill('How long does a claim take?');
  await page.getByRole('button', { name: 'Send' }).click();
  await expect(page.getByText(/Most claims are settled within 10 working days/)).toBeVisible();
  const history = page.getByRole('navigation', { name: 'Conversation history' });
  await expect(history.getByText('How long does a claim take?')).toBeVisible();
  await expect(history.getByText('Guest chats are deleted automatically within 5 hours after your last message.')).toBeVisible();

  // Reload: the token survives in sessionStorage (same browser session), still a guest
  await page.reload();
  await expect(page.getByText('Guest', { exact: true })).toBeVisible();

  // End guest session: keyboard-operable dialog
  await page.getByRole('button', { name: 'Account' }).click();
  await expect(page.getByRole('button', { name: 'Delete my data' })).toHaveCount(0);
  await page.getByRole('button', { name: 'End guest session' }).click();
  const dialog = page.getByRole('dialog', { name: 'End guest session?' });
  await expect(dialog).toContainText('permanently deletes all your guest chats');
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await dialog.getByRole('button', { name: 'End guest session' }).click();

  await expect(page.getByText('Your guest session has ended and its chats were deleted.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Continue as guest' })).toBeVisible();
  expect(ended).toBe(true);
  expect(await page.evaluate(() => sessionStorage.getItem('supportchat.guest'))).toBeNull();
  expect(authHeaders.length).toBeGreaterThan(0);
  expect(authHeaders.every((h) => h === `Bearer ${GUEST_TOKEN}`)).toBe(true);
});

test('guest: 401 guest_expired is not retried and returns to the login page with a notice', async ({ page }) => {
  let workflowCalls = 0;
  await enableGuest(page);
  await page.route((url) => url.pathname.startsWith('/api/'), async (route) => {
    const key = `${route.request().method()} ${new URL(route.request().url()).pathname}`;
    if (key === 'POST /api/guest/token') {
      return json(route, { accessToken: GUEST_TOKEN, tokenType: 'Bearer', expiresAt: new Date(Date.now() + 4 * 3_600_000).toISOString() });
    }
    if (key === 'GET /api/workflows') workflowCalls += 1;
    return route.fulfill({ status: 401, contentType: 'application/problem+json', body: JSON.stringify({ status: 401, code: 'guest_expired' }) });
  });
  await page.goto('/');
  await page.getByRole('button', { name: 'Continue as guest' }).click();
  await expect(page.getByText('Your guest session has ended.')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Continue as guest' })).toBeVisible();
  expect(workflowCalls).toBe(1);
  expect(await page.evaluate(() => sessionStorage.getItem('supportchat.guest'))).toBeNull();
});

test('login page has no guest button when guest access is disabled in config', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('button', { name: 'Continue with Google' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Continue as guest' })).toHaveCount(0);
});
