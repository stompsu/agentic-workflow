import { expect, test, type Route } from '@playwright/test';

const SESSION_ID = '0192f0c1-7a3e-7cc1-9a51-3f1e2b9d4c10';
const NOW = '2026-10-25T10:15:30.123Z';

const session = { id: SESSION_ID, workflowId: 'support-ticket', title: 'How long does a claim take?', createdAt: NOW, updatedAt: NOW };

const sse = [
  `id: 1\nevent: session\ndata: ${JSON.stringify({ sessionId: SESSION_ID })}\n\n`,
  `id: 2\nevent: agent\ndata: ${JSON.stringify({ name: 'Claims specialist' })}\n\n`,
  `id: 3\nevent: token\ndata: ${JSON.stringify({ text: 'Most claims are settled ' })}\n\n`,
  `: hb\n\n`,
  `id: 4\nevent: token\ndata: ${JSON.stringify({ text: 'within 10 working days [S1].' })}\n\n`,
  `id: 5\nevent: citation\ndata: ${JSON.stringify({ ref: 'S1', title: 'Claims handling times', docId: 'claims-handling-0003', url: 'https://kb.example.com/claims/handling' })}\n\n`,
  `id: 6\nevent: done\ndata: ${JSON.stringify({ messageId: 'msg-1' })}\n\n`,
].join('');

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

test('login -> pick workflow -> send -> streamed answer with citation -> appears in history', async ({ page }) => {
  let created = false;
  const authHeaders: (string | undefined)[] = [];

  // Match only the backend's /api/ prefix. A glob like '**/api/**' would also hijack Vite's dev-server module
  // requests (/src/api/client.ts, ...) and 404 the app's own source files.
  await page.route((url) => url.pathname.startsWith('/api/'), async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const key = `${req.method()} ${url.pathname}`;
    authHeaders.push(req.headers()['authorization']);

    switch (key) {
      case 'GET /api/workflows':
        return json(route, {
          items: [
            { id: 'support-ticket', displayName: 'Support ticket', description: 'Questions about policies, claims and health assessments.' },
            { id: 'echo', displayName: 'Echo (test)', description: 'Echoes your message.' },
          ],
        });
      case 'GET /api/sessions':
        return json(route, { items: created ? [session] : [], continuationToken: null });
      case 'POST /api/sessions':
        expect(req.postDataJSON()).toEqual({ workflowId: 'support-ticket' });
        created = true;
        return json(route, session, 201);
      case `POST /api/sessions/${SESSION_ID}/messages`:
        expect(req.postDataJSON()).toEqual({ content: 'How long does a claim take?' });
        return route.fulfill({
          status: 200,
          headers: { 'Content-Type': 'text/event-stream; charset=utf-8', 'Cache-Control': 'no-cache, no-transform' },
          body: sse,
        });
      case `GET /api/sessions/${SESSION_ID}`:
        return json(route, {
          ...session,
          hasMoreMessages: false,
          messages: [
            { id: 'u1', role: 'user', content: 'How long does a claim take?', createdAt: NOW, status: 'complete' },
            {
              id: 'msg-1',
              role: 'assistant',
              content: 'Most claims are settled within 10 working days [S1].',
              createdAt: NOW,
              status: 'complete',
              kind: 'answer',
              agentName: 'Claims specialist',
              citations: [{ ref: 'S1', title: 'Claims handling times', docId: 'claims-handling-0003', url: 'https://kb.example.com/claims/handling' }],
            },
          ],
        });
      default:
        return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404, code: 'not_found' }) });
    }
  });

  await page.goto('/');

  // Login page: provider buttons come from config.json
  await expect(page.getByRole('heading', { name: 'Assistant' })).toBeVisible();
  await page.getByRole('button', { name: 'Continue with Google' }).click();

  // Workflow dropdown comes from GET /api/workflows
  const select = page.getByRole('combobox', { name: 'Assistant' });
  await expect(select).toBeVisible();
  await expect(select.getByRole('option')).toHaveText(['Support ticket', 'Echo (test)']);
  await select.selectOption('support-ticket');

  await page.getByRole('textbox', { name: 'Message' }).fill('How long does a claim take?');
  await page.getByRole('button', { name: 'Send' }).click();

  // Streamed answer with agent badge and linked citation
  await expect(page.getByText('Claims specialist', { exact: true }).first()).toBeVisible();
  await expect(page.getByText(/Most claims are settled within 10 working days/)).toBeVisible();
  const source = page.getByRole('link', { name: 'Claims handling times' });
  await expect(source).toHaveAttribute('href', 'https://kb.example.com/claims/handling');
  await expect(source).toHaveAttribute('rel', 'noopener noreferrer');

  // Appears in the history sidebar
  const history = page.getByRole('navigation', { name: 'Conversation history' });
  await expect(history.getByText('How long does a claim take?')).toBeVisible();

  // Every API call carried the bearer token
  expect(authHeaders.length).toBeGreaterThan(0);
  expect(authHeaders.every((h) => h === 'Bearer e2e-test-token')).toBe(true);
});
