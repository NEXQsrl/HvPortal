// UI end-to-end smoke test driving the locally installed Microsoft Edge (playwright-core, channel "msedge").
// Requires a freshly started gateway (setup not completed) and a built agent.
// Usage: node e2e/ui-smoke.mjs --url https://localhost:18443 --data <gateway data dir> --agent-data <dir> --agent-exe <path> --out <screenshot dir>
import { chromium } from 'playwright-core';
import { createHmac } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const url = arg('url', 'https://localhost:18443');
const dataDir = arg('data');
const agentData = arg('agent-data');
const agentExe = arg('agent-exe');
const agentPort = arg('agent-port', '18444');
const out = arg('out', 'e2e-out');
mkdirSync(out, { recursive: true });

function totp(base32) {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = 0, value = 0; const bytes = [];
  for (const c of base32.replace(/\s/g, '')) { value = (value << 5) | alphabet.indexOf(c); bits += 5; if (bits >= 8) { bytes.push((value >>> (bits - 8)) & 255); bits -= 8; } }
  const counter = Buffer.alloc(8); counter.writeBigUInt64BE(BigInt(Math.floor(Date.now() / 30000)));
  const h = createHmac('sha1', Buffer.from(bytes)).update(counter).digest();
  const o = h[19] & 15;
  return String((((h[o] & 127) << 24) | (h[o + 1] << 16) | (h[o + 2] << 8) | h[o + 3]) % 1e6).padStart(6, '0');
}

const results = [];
const check = (name, ok, detail = '') => { results.push({ name, ok, detail }); console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  — ' + detail : ''}`); };
const shot = (page, name) => page.screenshot({ path: join(out, name + '.png'), fullPage: true });

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--ignore-certificate-errors'] });
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1280, height: 860 }, locale: 'it-IT', colorScheme: 'light' });
const page = await context.newPage();
const cspViolations = [];
page.on('console', (m) => { if (/Content Security Policy/i.test(m.text())) cspViolations.push(m.text()); });
let agent;

try {
  await page.goto(url);
  await page.getByRole('heading', { name: 'Configurazione iniziale' }).waitFor();
  const token = readFileSync(join(dataDir, 'bootstrap', 'setup-token.txt'), 'utf8').split(/\r?\n/)[1].trim();
  const password = 'violet harbor lantern orbit 2026';
  await page.getByLabel('Token di setup').fill(token);
  await page.getByLabel('Nome utente').fill('admin');
  await page.getByLabel('Nome visualizzato (facoltativo)').fill('Amministratore');
  await page.locator('#pw-new').fill(password);
  await page.locator('#pw-confirm').fill(password);
  await page.getByRole('button', { name: 'Crea amministratore' }).click();

  await page.getByRole('heading', { name: 'Configura la verifica in due passaggi' }).waitFor();
  check('MFA enrollment forced after setup', true);
  await page.getByRole('button', { name: 'Avvia configurazione' }).click();
  await page.locator('img.qr').waitFor();
  const secret = (await page.locator('.secret').first().innerText()).replace(/\s/g, '');
  await shot(page, '01-mfa-enrollment');
  await page.getByLabel('Inserisci il primo codice per confermare').fill(totp(secret));
  await page.getByRole('button', { name: 'Attiva' }).click();
  await page.getByRole('heading', { name: 'Codici di recupero' }).waitFor();
  check('recovery codes shown once', (await page.locator('.codes span').count()) === 10);
  await page.getByRole('button', { name: 'Ho conservato i codici' }).click();

  await page.getByRole('heading', { name: 'Dashboard' }).waitFor();
  check('dashboard reachable after MFA', true);
  await shot(page, '02-dashboard-empty');

  // Add a host -> enrollment dialog
  await page.getByRole('link', { name: 'Host', exact: true }).click();
  await page.getByRole('button', { name: 'Aggiungi host' }).click();
  await page.locator('#host-name').fill('hv-lab-01');
  await page.getByRole('button', { name: 'Crea e ottieni il token di registrazione' }).click();
  await page.getByRole('heading', { name: "Registra l'agente" }).waitFor();
  const enrollToken = (await page.locator('dialog[open] .secret').nth(0).innerText()).trim();
  const fingerprint = (await page.locator('dialog[open] .secret').nth(1).innerText()).trim();
  await shot(page, '03-enrollment-token');
  check('enrollment token and CA fingerprint displayed', enrollToken.length > 30 && /^[0-9A-F]{64}$/.test(fingerprint));
  await page.getByRole('button', { name: 'Chiudi' }).click();

  // Real agent: enroll + run
  rmSync(agentData, { recursive: true, force: true });
  const env = { ...process.env, HVP_AGENT_DATA_DIR: agentData, HVP_ENROLLMENT_TOKEN: enrollToken };
  execFileSync(agentExe, ['enroll', '--gateway', `https://localhost:${agentPort}`, '--ca-sha256', fingerprint], { env, stdio: 'pipe' });
  agent = spawn(agentExe, ['run'], { env: { ...env, HVP_ENROLLMENT_TOKEN: '' }, stdio: 'ignore' });

  // Live update: the host row should turn online without reloading the page.
  await page.getByText('online', { exact: true }).waitFor({ timeout: 30000 });
  check('host shows online via live update (SignalR)', true);
  await page.waitForTimeout(1500);
  await shot(page, '04-hosts');

  await page.getByRole('link', { name: 'hv-lab-01' }).click();
  await page.getByRole('heading', { name: 'hv-lab-01' }).waitFor();
  await page.waitForTimeout(1000);
  const accessBanner = await page.locator('.alert.danger').first().innerText().catch(() => '');
  check('host detail reports real Hyper-V access status', /AccessDenied|NotInstalled|ServiceStopped/.test(accessBanner) || (await page.getByText('Ok').count()) > 0, accessBanner.slice(0, 80));
  await shot(page, '05-host-detail');

  await page.getByRole('link', { name: 'Macchine virtuali' }).click();
  await page.waitForTimeout(800);
  await shot(page, '06-vms');
  await page.getByRole('link', { name: 'Utenti e ruoli' }).click();
  await page.getByRole('heading', { name: 'Assegnazioni dei ruoli' }).waitFor();
  await shot(page, '07-users');
  await page.getByRole('link', { name: 'Audit' }).click();
  await page.getByRole('button', { name: 'Verifica integrità' }).click();
  await page.locator('.alert.ok').waitFor();
  check('audit integrity verified from UI', true);
  await shot(page, '08-audit');
  await page.getByRole('link', { name: 'Impostazioni' }).click();
  await page.getByRole('heading', { name: 'Sicurezza' }).waitFor();
  await shot(page, '09-settings');

  // Theme + language toggles
  await page.getByRole('button', { name: 'English' }).click();
  await page.getByRole('button', { name: 'Toggle light/dark theme' }).click();
  await page.getByRole('link', { name: 'Hosts' }).click();
  await page.waitForTimeout(500);
  await shot(page, '10-hosts-dark-en');
  check('language switch to English', (await page.getByRole('heading', { name: 'Hosts' }).count()) > 0);

  // Mobile layout
  await page.setViewportSize({ width: 390, height: 800 });
  await page.waitForTimeout(300);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
  check('no horizontal page scroll at phone width', !overflow);
  await shot(page, '11-mobile');

  check('no CSP violations in console', cspViolations.length === 0, cspViolations.join(' | ').slice(0, 200));
} catch (e) {
  check('UI flow completed', false, String(e).slice(0, 300));
  await shot(page, 'zz-failure').catch(() => {});
} finally {
  agent?.kill();
  await browser.close();
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
