import { test, expect } from '@playwright/test';

/**
 * Tests E2E básicos de smoke test.
 * Los flujos completos (login, CRUD, etc.) se implementan en Fase 12.
 */
test('la aplicación carga correctamente', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/Asistente Jurídico/);
});

test('redirige a dashboard por defecto', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveURL(/dashboard/);
});
