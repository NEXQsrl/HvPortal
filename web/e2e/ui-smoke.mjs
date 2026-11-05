// UI end-to-end smoke test driving the locally installed Microsoft Edge (playwright-core, channel "msedge").
// Requires a freshly started gateway (setup not completed) and a built agent.
// Usage: node e2e/ui-smoke.mjs --url https://localhost:28443 --data <gateway data dir> --agent-data <dir> --agent-exe <path> --agent-port 28444 --out <screenshot dir>
import { chromium } from 'playwright-core';
import { createHash, createHmac } from 'node:crypto';
import { execFileSync, spawn } from 'node:child_process';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const arg = (n, d) => { const i = process.argv.indexOf('--' + n); return i > 0 ? process.argv[i + 1] : d; };
const url = arg('url', 'https://localhost:28443');
const dataDir = arg('data');
const agentData = arg('agent-data');
const agentExe = arg('agent-exe');
const agentPort = arg('agent-port', '28444');
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
const shot = (page, name) => page.screenshot({ path: join(out, name + '.png') });

const browser = await chromium.launch({ channel: 'msedge', headless: true, args: ['--ignore-certificate-errors'] });
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, locale: 'it-IT', colorScheme: 'light' });
const page = await context.newPage();
const cspViolations = [];
page.on('console', (m) => { if (/Content Security Policy|Refused to (apply|load|execute)/i.test(m.text())) cspViolations.push(m.text()); });
page.on('pageerror', (e) => { console.log('PAGE ERROR: ' + e.message); cspViolations.push('pageerror: ' + e.message); });
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
  await shot(page, '00-setup');
  await page.getByRole('button', { name: 'Crea amministratore' }).click();

  await page.getByRole('heading', { name: 'Configura la verifica in due passaggi' }).waitFor();
  check('MFA enrollment forced after setup', true);
  await page.getByRole('button', { name: 'Avvia configurazione' }).click();
  await page.locator('img.qr').waitFor();
  const secret = (await page.locator('.secret').first().innerText()).replace(/\s/g, '');
  await shot(page, '01-mfa-enrollment');
  await page.locator('#mfa-otp-0').fill(totp(secret));
  check('MFA code uses 6 separate boxes', (await page.locator('.otp-box').count()) === 6);
  await page.getByRole('button', { name: 'Attiva' }).click();
  await page.getByRole('heading', { name: 'Codici di recupero' }).waitFor();
  check('recovery codes shown once', (await page.locator('.codes span').count()) === 10);
  await page.getByRole('button', { name: 'Ho conservato i codici' }).click();

  await page.getByRole('heading', { name: 'Dashboard' }).waitFor();
  check('dashboard reachable after MFA', true);
  check('header, sidebar and recent tasks panel present', (await page.locator('header.topbar').count()) === 1 && (await page.locator('.tasks-panel').count()) === 1);
  await shot(page, '02-dashboard-empty');

  // Add a host -> enrollment dialog
  await page.getByRole('link', { name: 'Host', exact: true }).click();
  await page.getByRole('button', { name: 'Aggiungi host' }).click();
  await page.locator('#host-name').fill('hv-lab-01');
  await page.getByRole('button', { name: 'Crea e ottieni il token di registrazione' }).click();
  await page.getByRole('heading', { name: /Registra l'agente/ }).waitFor();
  const dialog = page.getByRole('dialog');
  const installCmd = await dialog.locator('pre.command').filter({ hasText: 'HVP_ENROLLMENT_TOKEN' }).innerText();
  const enrollToken = /HVP_ENROLLMENT_TOKEN = '([^']+)'/.exec(installCmd)[1];
  const fingerprint = /-CaSha256 ([0-9A-F]{64})/.exec(installCmd)[1];
  const gatewayArg = /-Gateway (\S+)/.exec(installCmd)[1];
  check('guide shows ready-to-paste install command', enrollToken.length > 30 && /^[0-9A-F]{64}$/.test(fingerprint), gatewayArg);
  check('install command never points to localhost', !/localhost|127\.0\.0\.1/.test(gatewayArg), gatewayArg);
  check('token countdown visible', (await dialog.getByText(/Token valido ancora per/).count()) === 1);

  // Download from the portal and compare with the SHA-256 shown in the guide.
  await dialog.locator('.pkg-hash').waitFor();
  const shownHash = (await dialog.locator('.pkg-hash').innerText()).trim();
  const [download] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: /Scarica il pacchetto agente/ }).click()]);
  const zipPath = join(out, 'downloaded-agent.zip');
  await download.saveAs(zipPath);
  const actualHash = createHash('sha256').update(readFileSync(zipPath)).digest('hex').toUpperCase();
  check('agent package downloads and matches displayed SHA-256', actualHash === shownHash, actualHash.slice(0, 16));
  await shot(page, '03-enrollment-guide');

  // Real agent: enroll + run (against loopback: the e2e gateway listens locally only)
  rmSync(agentData, { recursive: true, force: true });
  const env = { ...process.env, HVP_AGENT_DATA_DIR: agentData, HVP_ENROLLMENT_TOKEN: enrollToken };
  execFileSync(agentExe, ['enroll', '--gateway', `https://localhost:${agentPort}`, '--ca-sha256', fingerprint], { env, stdio: 'pipe' });
  agent = spawn(agentExe, ['run'], { env: { ...env, HVP_ENROLLMENT_TOKEN: '' }, stdio: 'ignore' });
  await dialog.getByText(/Agente collegato/).waitFor({ timeout: 30000 });
  check('guide shows live "agent connected" status', true);
  await shot(page, '03b-enrollment-guide-connected');
  await dialog.getByRole('button', { name: 'Chiudi', exact: true }).click();
  // Live update: the host row turns online without reloading the page.
  await page.locator('tr.clickable', { hasText: 'hv-lab-01' }).getByText('online', { exact: true }).waitFor({ timeout: 30000 });
  check('host shows online via live update (SignalR)', true);
  await page.waitForTimeout(1200);
  await shot(page, '04-hosts');

  // Inventory tree beside the list
  check('inventory tree shows the host', (await page.getByRole('treeitem', { name: /hv-lab-01/ }).count()) === 1);

  // Row menu (right click) and footer
  await page.locator('tr.clickable', { hasText: 'hv-lab-01' }).click({ button: 'right' });
  check('right click opens row menu with remove action', (await page.getByRole('menuitem', { name: 'Rimuovi dal portale' }).count()) === 1);
  await page.keyboard.press('Escape');
  check('sidebar footer shows NEXQ S.r.l. and site', (await page.locator('.sidebar-footer').first().innerText()).includes('NEXQ S.r.l.') && (await page.locator('.sidebar-footer a[href="https://www.nexq.it"]').count()) === 1);
  await page.locator('tr.clickable', { hasText: 'hv-lab-01' }).click();
  await page.getByRole('heading', { name: 'hv-lab-01' }).waitFor();
  await page.waitForTimeout(1000);
  const accessBanner = await page.locator('.alert-danger').first().innerText().catch(() => '');
  check('host detail reports real Hyper-V access status', /AccessDenied|NotInstalled|ServiceStopped/.test(accessBanner) || (await page.getByText('Hyper-V: Ok').count()) > 0, accessBanner.slice(0, 80));
  check('host page shows volumes and network adapters', (await page.getByRole('heading', { name: 'Volumi' }).count()) === 1 && (await page.getByRole('heading', { name: 'Schede di rete' }).count()) === 1);
  check('host page has vCenter-like tabs', (await page.getByRole('tab').count()) >= 6);
  await shot(page, '05-host-detail');
  await page.getByRole('tab', { name: 'Impostazioni' }).click();
  await page.getByText('Libreria ISO').first().waitFor();
  check('host settings tab shows the ISO library editor', true);
  await shot(page, '05b-host-settings');

  // Groups (folders) in the tree
  await page.getByRole('radio', { name: 'Gruppi di VM (cartelle)' }).click();
  await page.getByRole('button', { name: 'Nuovo gruppo' }).first().click();
  await page.locator('#group-name').fill('Produzione');
  await page.getByRole('dialog').getByRole('button', { name: 'Crea' }).click();
  await page.getByRole('heading', { name: 'Produzione' }).waitFor();
  check('group created from the tree and opened', (await page.getByRole('treeitem', { name: /Produzione/ }).count()) === 1);
  await page.getByRole('treeitem', { name: /Produzione/ }).click({ button: 'right' });
  check('group context menu offers subgroup and move', (await page.getByRole('menuitem', { name: /Nuovo sottogruppo/ }).count()) === 1 && (await page.getByRole('menuitem', { name: /Sposta in/ }).count()) === 1);
  await page.keyboard.press('Escape');
  await shot(page, '06-group');

  // Command palette (Ctrl+K)
  await page.keyboard.press('Control+k');
  await page.getByPlaceholder(/Scrivi il nome di una VM/).fill('hv-lab');
  await page.keyboard.press('Enter');
  await page.getByRole('heading', { name: 'hv-lab-01' }).waitFor();
  check('command palette jumps to a host', true);

  await page.getByRole('link', { name: 'Macchine virtuali', exact: true }).click();
  await page.waitForTimeout(800);
  await shot(page, '07-vms');
  await page.getByRole('link', { name: 'Utenti e ruoli' }).click();
  await page.getByRole('heading', { name: 'Assegnazioni dei ruoli' }).waitFor();
  await shot(page, '08-users');
  await page.getByRole('link', { name: 'Audit' }).click();
  check('audit is paginated', (await page.locator('.pager').count()) === 1);
  check('page itself does not scroll (only panels)', await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight + 1));
  await page.getByRole('button', { name: 'Verifica integrità' }).click();
  await page.locator('.alert-ok').waitFor();
  check('audit integrity verified from UI', true);
  await shot(page, '09-audit');
  await page.getByRole('link', { name: 'Impostazioni', exact: true }).click();
  await page.getByRole('heading', { name: 'Sicurezza' }).waitFor();
  await shot(page, '10-settings');

  await page.getByRole('link', { name: 'Guida' }).click();
  await page.getByRole('heading', { name: 'Guida: collegare un host Hyper-V' }).waitFor();
  await page.locator('summary', { hasText: 'Risoluzione dei problemi' }).waitFor();
  check('guide page available in Italian', true);
  await shot(page, '11-guide');

  // Theme + language from the account menu
  await page.locator('.account-button').click();
  check('account menu with language and theme', (await page.getByRole('menuitemradio', { name: 'Scuro' }).count()) === 1);
  await page.getByRole('menuitemradio', { name: 'English' }).click();
  await page.getByRole('menuitemradio', { name: 'Dark' }).click();
  await shot(page, '12-account-menu');
  await page.keyboard.press('Escape');
  check('dark theme applied', await page.evaluate(() => document.documentElement.classList.contains('dark')));
  await page.getByRole('link', { name: 'Hosts', exact: true }).click();
  await page.waitForTimeout(500);
  await shot(page, '13-hosts-dark-en');
  check('language switch to English', (await page.getByRole('heading', { name: 'Hosts' }).count()) > 0);
  await page.getByRole('link', { name: 'Dashboard', exact: true }).click();
  await page.waitForTimeout(500);
  await shot(page, '14-dashboard-dark');
  await page.getByRole('link', { name: 'Guide', exact: true }).click();
  await page.getByRole('heading', { name: 'Guide: connect a Hyper-V host' }).waitFor();
  await page.locator('summary', { hasText: 'Troubleshooting' }).waitFor();
  check('guide page available in English', true);

  // Phone width
  await page.setViewportSize({ width: 390, height: 800 });
  await page.waitForTimeout(400);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth + 1);
  check('no horizontal page scroll at phone width', !overflow);
  await shot(page, '15-mobile');

  check('no CSP violations or page errors', cspViolations.length === 0, cspViolations.join(' | ').slice(0, 300));
} catch (e) {
  check('UI flow completed', false, String(e).slice(0, 400));
  await shot(page, 'zz-failure').catch(() => {});
} finally {
  agent?.kill();
  await browser.close();
}
const failed = results.filter((r) => !r.ok).length;
console.log(`${results.length - failed} passed, ${failed} failed`);
process.exit(failed ? 1 : 0);
