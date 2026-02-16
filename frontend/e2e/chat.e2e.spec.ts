import { expect, test, type Page } from '@playwright/test';

const agentMappings = [
  {
    value: 'default',
    label: 'Default Area',
    agentId: 'agent-e2e',
  },
];

const agentMetadata = {
  id: 'agent-e2e',
  object: 'agent',
  createdAt: Math.floor(Date.now() / 1000),
  name: 'E2E Agent',
  version: '1.0',
  description: 'Mocked agent for Playwright tests',
  model: 'gpt-4o-mini',
  metadata: {
    welcomeMessage: 'E2E Agent',
  },
};

async function mockAppBootstrap(page: Page): Promise<void> {
  await page.route('**/api/auth/token', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ accessToken: 'mock-token' }),
    });
  });

  await page.route('**/api/agents', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(agentMappings),
    });
  });

  await page.route('**/api/agent**', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(agentMetadata),
    });
  });
}

async function mockChatStreamSuccess(page: Page): Promise<void> {
  await page.route('**/api/chat/stream**', async (route) => {
    const body = [
      'data: {"type":"conversationId","conversationId":"conv-e2e"}\n',
      'data: {"type":"chunk","content":"Hello from mocked assistant."}\n',
      'data: {"type":"usage","promptTokens":10,"completionTokens":6,"totalTokens":16,"duration":220}\n',
      'data: {"type":"done"}\n',
    ].join('');

    await route.fulfill({
      status: 200,
      contentType: 'text/event-stream',
      body,
      headers: {
        'cache-control': 'no-cache',
        connection: 'keep-alive',
      },
    });
  });
}

test.beforeEach(async ({ page }) => {
  await mockAppBootstrap(page);
  await mockChatStreamSuccess(page);
});

test('loads chat shell and input', async ({ page }) => {
  await page.goto('/');

  await expect(page.getByRole('textbox', { name: /chat input/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /new chat/i })).toBeVisible();
  await expect(page.getByRole('button', { name: /attach files/i })).toBeVisible();
});

test('submits a message and renders mocked streamed response', async ({ page }) => {
  await page.goto('/');

  const chatInput = page.getByRole('textbox', { name: /chat input/i });
  await chatInput.fill('Playwright E2E message');
  await page.getByRole('button', { name: /send/i }).click();

  await expect(page.getByText('Playwright E2E message')).toBeVisible();
  await expect(page.getByText('Hello from mocked assistant.', { exact: true })).toBeVisible();
});

test('shows recoverable error UI when chat endpoint fails', async ({ page }) => {
  await page.unroute('**/api/chat/stream**');
  await page.route('**/api/chat/stream**', async (route) => {
    await route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({
        title: 'Server Error',
        detail: 'Simulated API failure',
      }),
    });
  });

  await page.goto('/');

  const chatInput = page.getByRole('textbox', { name: /chat input/i });
  await chatInput.fill('Trigger error');
  await page.getByRole('button', { name: /send/i }).click();

  await expect(page.getByText(/server encountered an unexpected error/i)).toBeVisible();
  await expect(page.getByRole('button', { name: /dismiss error/i })).toBeVisible();
});
