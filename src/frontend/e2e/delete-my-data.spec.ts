import { expect, test, type Route } from '@playwright/test';

const NOW = '2026-10-25T10:15:30.123Z';
const session = { id: 's-1', workflowId: 'support-ticket', title: 'My claim from May', createdAt: NOW, updatedAt: NOW };

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

test('account menu -> Delete my data -> confirm -> 503 retried -> 204 -> empty history, notice, still signed in', async ({ page }) => {
  let deleted = false;
  let deleteAttempts = 0;

  // Runtime config with the optional fields (the placeholder public/config.json has neither).
  await page.route('**/config.json', async (route) => {
    const res = await route.fetch();
    const config = (await res.json()) as { app: Record<string, unknown> };
    config.app = { ...config.app, retentionDays: 90, supportContact: 'help@example.com' };
    await json(route, config);
  });

  await page.route((url) => url.pathname.startsWith('/api/'), async (route) => {
    const req = route.request();
    const key = `${req.method()} ${new URL(req.url()).pathname}`;
    switch (key) {
      case 'GET /api/workflows':
        return json(route, { items: [{ id: 'support-ticket', displayName: 'Support ticket', description: 'Help.' }] });
      case 'GET /api/sessions':
        return json(route, { items: deleted ? [] : [session], continuationToken: null });
      case 'DELETE /api/me/data':
        deleteAttempts += 1;
        expect(req.headers()['authorization']).toBe('Bearer e2e-test-token');
        if (deleteAttempts === 1) {
          return route.fulfill({
            status: 503,
            headers: { 'Retry-After': '1' },
            contentType: 'application/problem+json',
            body: JSON.stringify({ status: 503, code: 'deletion_incomplete', title: 'x' }),
          });
        }
        deleted = true;
        return route.fulfill({ status: 204 });
      default:
        return route.fulfill({ status: 404, contentType: 'application/problem+json', body: JSON.stringify({ status: 404, code: 'not_found' }) });
    }
  });

  await page.goto('/');
  await page.getByRole('button', { name: 'Continue with Google' }).click();

  const history = page.getByRole('navigation', { name: 'Conversation history' });
  await expect(history.getByText('My claim from May')).toBeVisible();
  await expect(history.getByText('Chats are kept for 90 days after last activity.')).toBeVisible();

  // Keyboard: open the menu and the dialog; Cancel has the initial focus and does nothing.
  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('button', { name: 'Delete my data' }).click();
  const dialog = page.getByRole('dialog', { name: 'Delete my data?' });
  await expect(dialog).toBeVisible();
  await expect(dialog).toContainText('permanently deleted');
  await expect(dialog).toContainText('sign-in account is not deleted');
  await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  expect(deleteAttempts).toBe(0);

  // Confirm
  await page.getByRole('button', { name: 'Account' }).click();
  await page.getByRole('button', { name: 'Delete my data' }).click();
  await dialog.getByRole('button', { name: 'Delete my data' }).click();

  await expect(dialog).toBeHidden({ timeout: 10_000 });
  expect(deleteAttempts).toBe(2);
  await expect(page.getByRole('status').filter({ hasText: 'Your chat history was deleted. Your sign-in account was not deleted.' })).toContainText(
    'contact help@example.com',
  );
  await expect(history.getByText('No conversations yet.')).toBeVisible();
  await expect(page.getByRole('combobox', { name: 'Assistant' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Sign out' })).toBeVisible();
});
