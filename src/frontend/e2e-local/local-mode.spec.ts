import { expect, test, type Page } from '@playwright/test';

// Real LocalHost + vite --mode localdev (see playwright.local.config.ts). The scripted model is deterministic.
// Each test uses a fresh browser context, so sessionStorage (the local token) is isolated per test.

async function signIn(page: Page, who: 'Alice' | 'Bob') {
  await page.goto('/');
  await expect(page.getByText('LOCAL MODE')).toBeVisible();
  await page.getByRole('button', { name: `Sign in as ${who} (local)` }).click();
  await expect(page.getByRole('combobox', { name: 'Assistant' })).toBeVisible();
}

async function ask(page: Page, text: string) {
  await page.getByRole('textbox', { name: 'Message' }).fill(text);
  await page.getByRole('button', { name: 'Send' }).click();
}

test('Alice asks a support question: streamed answer, agent badge, citation link', async ({ page }) => {
  await signIn(page, 'Alice');
  await page.getByRole('combobox', { name: 'Assistant' }).selectOption('support-ticket');
  await ask(page, 'How do I file a claim?');

  await expect(page.getByText('Claims specialist', { exact: true }).first()).toBeVisible({ timeout: 30_000 });
  await expect(page.getByText(/Scripted local answer/)).toBeVisible({ timeout: 30_000 });
  const history = page.getByRole('navigation', { name: 'Conversation history' });
  await expect(history.getByText('How do I file a claim?')).toBeVisible();
  // At least one citation (rendered as a link when the knowledge chunk has an http(s) sourceUrl).
  const sources = page.getByRole('region', { name: 'Sources' });
  await expect(sources.getByRole('listitem').first()).toBeVisible();
});

test('Stop ends a running turn', async ({ page }) => {
  await signIn(page, 'Alice');
  await page.getByRole('combobox', { name: 'Assistant' }).selectOption('support-ticket');
  await ask(page, 'How do I file a claim?');
  const stop = page.getByRole('button', { name: 'Stop' });
  await expect(stop).toBeVisible({ timeout: 30_000 });
  await stop.click();
  await expect(page.getByRole('button', { name: 'Send' })).toBeVisible();
});

test('Bob does not see Alice\'s chat; delete my data clears Alice\'s history', async ({ browser }) => {
  const title = 'What does my policy cover?';

  const alice = await browser.newContext();
  const a = await alice.newPage();
  await signIn(a, 'Alice');
  await a.getByRole('combobox', { name: 'Assistant' }).selectOption('support-ticket');
  await ask(a, title);
  const aliceHistory = a.getByRole('navigation', { name: 'Conversation history' });
  await expect(aliceHistory.getByText(title)).toBeVisible({ timeout: 30_000 });
  await expect(a.getByText(/Scripted local answer/)).toBeVisible({ timeout: 30_000 });

  const bob = await browser.newContext();
  const b = await bob.newPage();
  await signIn(b, 'Bob');
  await expect(b.getByRole('navigation', { name: 'Conversation history' }).getByText(title)).toHaveCount(0);
  await bob.close();

  // Delete my data (account menu -> confirmation dialog)
  await a.getByRole('button', { name: 'Account' }).click();
  await a.getByRole('button', { name: 'Delete my data' }).click();
  await a.getByRole('dialog').getByRole('button', { name: 'Delete my data' }).click();
  await expect(aliceHistory.getByText(title)).toHaveCount(0, { timeout: 30_000 });
  await alice.close();
});
