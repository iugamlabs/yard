import { test, expect, type Page } from '@playwright/test';
const password = 'Browser-test-password-123';
async function login(
  page: Page,
  username = process.env.YARD_VERIFY_USERNAME ?? 'owner',
  pwd = process.env.YARD_VERIFY_PASSWORD ?? password,
) {
  await page.goto('/login');
  await page.getByLabel('用户名 / 邮箱 / 手机号').fill(username);
  await page.getByLabel('密码', { exact: true }).fill(pwd);
  await page.getByRole('button', { name: '登录', exact: true }).click();
  await expect(page.getByRole('link', { name: '账号设置' })).toBeVisible();
}
test('username registration, organization and settings', async ({ page }) => {
  const username = 'user' + Date.now();
  await page.goto('/register');
  await page.getByLabel('用户名', { exact: true }).fill(username);
  await page.getByLabel('密码', { exact: true }).fill(password);
  await page.getByRole('button', { name: '注册', exact: true }).click();
  await expect(page).toHaveURL(/login/);
  await login(page, username, password);
  await page.getByLabel('组织名称').fill('Browser organization');
  await page.getByRole('button', { name: '创建组织' }).click();
  await page.getByRole('link', { name: /Browser organization/ }).click();
  await expect(page.getByRole('heading', { name: 'Browser organization' })).toBeVisible();
  await page.getByRole('button', { name: '角色与权限' }).click();
  await expect(page.getByRole('heading', { name: '新增角色' })).toBeVisible();
  await page.screenshot({
    path: '.yard/browser-results/organization-' + test.info().project.name + '.png',
    fullPage: true,
  });
  await page.getByRole('link', { name: '账号设置' }).click();
  await expect(page.getByRole('heading', { name: '个人资料' })).toBeVisible();
});
test('administrator can create, inspect and revoke accounts', async ({ page }) => {
  await login(page);
  await page.getByRole('link', { name: '平台管理' }).click();
  await expect(page.getByRole('heading', { name: '平台管理' })).toBeVisible();
  const create = page
    .getByRole('heading', { name: '创建账号', exact: true })
    .locator('..')
    .locator('..');
  await create.getByLabel('用户名', { exact: true }).fill('managed' + Date.now());
  await create.getByLabel('临时密码').fill(password);
  await create.getByRole('button', { name: '创建账号', exact: true }).click();
  await expect(create.getByRole('status')).toContainText('操作已完成');
  await page.getByRole('button', { name: '操作记录', exact: true }).click();
  await expect(page.getByRole('heading', { name: '平台操作记录' })).toBeVisible();
  await page.screenshot({
    path: '.yard/browser-results/admin-' + test.info().project.name + '.png',
    fullPage: true,
  });
});
test('email verification sends a real development message and revokes sessions', async ({
  page,
  context,
}) => {
  await login(page);
  await page.getByRole('link', { name: '账号设置' }).click();
  const panel = page
    .getByRole('heading', { name: '邮箱', exact: true })
    .locator('..')
    .locator('..');
  const email = 'browser' + Date.now() + '@example.test';
  await panel.getByLabel('邮箱地址').fill(email);
  await panel.getByLabel('当前密码').fill(process.env.YARD_VERIFY_PASSWORD ?? password);
  await panel.getByRole('button', { name: '发送验证码' }).click();
  await expect(panel.getByLabel('六位邮箱验证码')).toBeVisible();
  const inbox = await context.newPage();
  await inbox.goto('/admin/');
  await inbox.getByRole('button', { name: '开发收件箱' }).click();
  const message = inbox.locator('article').filter({ hasText: email });
  await expect(message).toBeVisible();
  const code = (await message.locator('pre').innerText()).match(/\d{6}/)![0];
  await panel.getByLabel('六位邮箱验证码').fill(code);
  await panel.getByRole('button', { name: '确认验证码' }).click();
  await expect(page).toHaveURL(/login/);
  await inbox.close();
  await login(page, email, process.env.YARD_VERIFY_PASSWORD ?? password);
});
test('csrf rejects untrusted writes', async ({ request }) => {
  const r = await request.post('/api/auth/register', {
    data: { username: 'csrf' + Date.now(), password },
  });
  expect(r.status()).toBe(400);
});

test('temporary password change and administrator-confirmed phone login', async ({
  page,
  browser,
}) => {
  await login(page);
  await page.getByRole('link', { name: '平台管理' }).click();
  const username = 'phone' + Date.now();
  const phone = '138' + String(Date.now()).slice(-8);
  const create = page
    .getByRole('heading', { name: '创建账号', exact: true })
    .locator('..')
    .locator('..');
  await create.getByLabel('用户名', { exact: true }).fill(username);
  await create.getByLabel('临时密码').fill(password);
  await create.getByRole('button', { name: '创建账号', exact: true }).click();
  await expect(create.getByRole('status')).toBeVisible();
  await page.getByLabel('搜索账号').fill(username);
  await page
    .getByRole('row')
    .filter({ hasText: username })
    .getByRole('button', { name: '管理', exact: true })
    .click();
  const panel = page
    .getByRole('heading', { name: '管理 ' + username })
    .locator('..')
    .locator('..');
  const form = panel
    .locator('form')
    .filter({ has: page.getByRole('button', { name: '保存已核实手机号' }) });
  await form.getByLabel('手机号（留空清除）').fill(phone);
  await form.getByLabel('我已核实号码归属').check();
  await form.getByLabel('当前密码').fill(process.env.YARD_VERIFY_PASSWORD ?? password);
  await form.getByRole('button', { name: '保存已核实手机号' }).click();
  await expect(form.getByRole('status')).toBeVisible();
  const member = await browser.newPage();
  await login(member, username, password);
  await expect(member.getByRole('heading', { name: '请修改临时密码' })).toBeVisible();
  await member.getByLabel('当前密码').fill(password);
  await member.getByLabel('新密码').fill('Changed-browser-password-123');
  await member.getByRole('button', { name: '修改密码', exact: true }).click();
  await expect(member).toHaveURL(/login/);
  await login(member, phone, 'Changed-browser-password-123');
  await member.close();
});
test('verified email recovery invalidates existing sessions', async ({ page, browser }) => {
  const username = 'recover' + Date.now();
  const email = username + '@example.test';
  await page.goto('/register');
  await page.getByLabel('用户名', { exact: true }).fill(username);
  await page.getByLabel('密码', { exact: true }).fill(password);
  await page.getByRole('button', { name: '注册', exact: true }).click();
  await expect(page).toHaveURL(/login/);
  await login(page, username, password);
  await page.getByRole('link', { name: '账号设置' }).click();
  const panel = page
    .getByRole('heading', { name: '邮箱', exact: true })
    .locator('..')
    .locator('..');
  await panel.getByLabel('邮箱地址').fill(email);
  await panel.getByLabel('当前密码').fill(password);
  await panel.getByRole('button', { name: '发送验证码' }).click();
  await expect(panel.getByLabel('六位邮箱验证码')).toBeVisible();
  const admin = await browser.newPage();
  await login(admin);
  const inbox = await admin.request.get('/api/__yard/mail');
  const messages = await inbox.json();
  const code = messages
    .find((m: { recipient: string }) => m.recipient === email)
    .text.match(/\d{6}/)[0];
  await panel.getByLabel('六位邮箱验证码').fill(code);
  await panel.getByRole('button', { name: '确认验证码' }).click();
  await expect(page).toHaveURL(/login/);
  await page.goto('/recover');
  await page.getByLabel('邮箱地址').fill(email);
  await page.getByRole('button', { name: '发送验证码' }).click();
  await expect(page.getByLabel('六位邮箱验证码')).toBeVisible();
  const recoveryMail = await (await admin.request.get('/api/__yard/mail')).json();
  const recoveryCode = recoveryMail
    .find(
      (m: { recipient: string; text: string }) =>
        m.recipient === email && m.text.includes('recover-password'),
    )
    .text.match(/\d{6}/)[0];
  await page.getByLabel('六位邮箱验证码').fill(recoveryCode);
  await page.getByRole('button', { name: '确认验证码' }).click();
  await page.getByLabel('新密码').fill('Recovered-browser-password-123');
  await page.getByRole('button', { name: '设置新密码' }).click();
  await expect(page).toHaveURL(/login/);
  await login(page, email, 'Recovered-browser-password-123');
  await admin.close();
});
test('GitHub browser redirect and passwordless registration use real PKCE exchange', async ({
  page,
  request,
}) => {
  test.skip(!process.env.YARD_GITHUB_TEST_URL, 'Local protocol fixture is enabled by yard verify');
  const id = test.info().project.name === 'desktop' ? 12345 : 12346;
  const email = 'external-' + test.info().project.name + '@example.test';
  await request.post(process.env.YARD_GITHUB_TEST_URL + '/__test/account', {
    data: { id, email, verified: true },
  });
  await page.goto('/login');
  await page.getByRole('button', { name: '使用 GitHub 登录' }).click();
  await expect(page.getByRole('heading', { name: '完成 GitHub 注册' })).toBeVisible();
  await page.getByLabel('用户名', { exact: true }).fill('github' + Date.now());
  await expect(page.getByLabel('联系邮箱', { exact: true })).toHaveValue(email);
  await page.getByRole('button', { name: '完成注册' }).click();
  await expect(page.getByRole('heading', { name: '我的组织' })).toBeVisible();
  await page.getByRole('link', { name: '账号设置' }).click();
  await expect(page.getByRole('heading', { name: '验证身份' })).toBeVisible();
  const status = await (
    await request.get(process.env.YARD_GITHUB_TEST_URL + '/__test/status')
  ).json();
  expect(status.exchanges).toBeGreaterThan(0);
  await page.screenshot({
    path: '.yard/browser-results/github-' + test.info().project.name + '.png',
    fullPage: true,
  });
});

test('Vite HMR uses the same gateway without a page reload', async ({ page }) => {
  test.skip(
    process.env.YARD_VERIFY_ISOLATED !== '1',
    'Source edits only run in the isolated yard verify checkout',
  );
  const fs = await import('node:fs/promises');
  const path = 'packages/platform/src/index.tsx';
  const original = await fs.readFile(path, 'utf8');
  const marker = 'HMR-' + Date.now();
  await page.goto('/login');
  await expect(page.getByRole('heading', { name: '欢迎回来' })).toBeVisible();
  await page.evaluate(() => {
    (window as any).__yardHmrMarker = 'preserved';
  });
  try {
    await fs.writeFile(path, original.replace(' · Workspace', ' · ' + marker));
    await expect(page.locator('footer')).toContainText(marker);
    expect(await page.evaluate(() => (window as any).__yardHmrMarker)).toBe('preserved');
  } finally {
    await fs.writeFile(path, original);
  }
});
