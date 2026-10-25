import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { test, expect } from '@playwright/test';
test('notes persist across reload and can be deleted', async ({ page }) => {
  await page.goto('/');
  const text = 'Browser note ' + Date.now();
  await page.getByRole('textbox', { name: 'Note text' }).fill(text);
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByText(text, { exact: true })).toBeVisible();
  await page.reload();
  await expect(page.getByText(text, { exact: true })).toBeVisible();
  await page
    .getByRole('listitem')
    .filter({ hasText: text })
    .getByRole('button', { name: 'Delete' })
    .click();
  await expect(page.getByText(text, { exact: true })).toHaveCount(0);
});
test('invalid notes show the server validation error', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Add note' }).click();
  await expect(page.getByRole('alert')).toContainText('Enter between 1 and 200 characters.');
});

test('frontend HMR preserves an unsaved draft', async ({ page }) => {
  test.skip(
    process.env.YARD_VERIFY_ISOLATED !== '1',
    'Source edits are restricted to the isolated verify fixture.',
  );
  const file = resolve('apps/web/src/App.tsx');
  const original = readFileSync(file, 'utf8');
  const label = 'Notes — a small example module you can replace.';
  const replacement = 'Frontend update applied';
  await page.goto('/');
  await page.getByRole('textbox', { name: 'Note text' }).fill('Unsaved draft');
  try {
    writeFileSync(file, original.replace(label, replacement));
    await expect(page.getByText(replacement, { exact: true })).toBeVisible();
    await expect(page.getByRole('textbox', { name: 'Note text' })).toHaveValue('Unsaved draft');
  } finally {
    writeFileSync(file, original);
  }
  await expect(page.getByText(label, { exact: true })).toBeVisible();
});
