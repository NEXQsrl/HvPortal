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
/** Local folder (not a system folder) with sample.txt, used to exercise the host file explorer against the real agent. */
const filesDir = arg('files-dir');
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
  // Navigator (v0.5.0): the main navigation is a row of icons (labels as tooltips) above the tree, on every page.
  const navIcons = page.locator('aside.sidebar nav.nav a');
  check('navigator: icon-only navigation above the tree on the dashboard', (await navIcons.count()) >= 6
    && (await navIcons.allInnerTexts()).every((x) => x.trim() === '') && (await page.locator('aside.sidebar [role=tree]').count()) === 1);
  await shot(page, '02-dashboard-empty');

  // Add a host -> enrollment dialog
  // Hosts, groups and VMs live in the Inventory only (no separate sidebar entries).
  const nav = page.getByRole('navigation', { name: 'Main' });
  check('sidebar has only Inventory for hosts/VMs', (await nav.getByRole('link', { name: 'Inventario', exact: true }).count()) === 1
    && (await nav.getByRole('link', { name: 'Host', exact: true }).count()) === 0 && (await nav.getByRole('link', { name: 'Macchine virtuali', exact: true }).count()) === 0);
  await nav.getByRole('link', { name: 'Inventario', exact: true }).click();
  await page.getByRole('heading', { name: 'Inventario' }).waitFor();
  check('inventory tree has Host, Gruppi and Tutte le VM sections', (await Promise.all(['Host', 'Gruppi', 'Tutte le VM'].map((n) => page.getByRole('treeitem', { name: new RegExp('^' + n) }).count()))).every((c) => c === 1));
  await page.getByRole('treeitem', { name: /^Host/ }).click({ button: 'right' });
  check('right click on Host section offers "Aggiungi host"', (await page.getByRole('menuitem', { name: 'Aggiungi host' }).count()) === 1);
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'Aggiungi host' }).first().click();
  await page.locator('#host-name').fill('hv-lab-01');
  await page.getByRole('button', { name: 'Crea e ottieni il token di registrazione' }).click();
  await page.getByRole('heading', { name: /Registra l'agente/ }).waitFor();
  const dialog = page.getByRole('dialog');
  // Guided setup (default when the gateway provides HvPortal.Agent.Setup.exe): download + one line of connection data.
  // A method is marked as chosen only once the gateway has said whether it provides the setup file.
  await dialog.locator('[role="radio"][aria-checked="true"]').waitFor();
  if ((await dialog.getByRole('radio', { name: /Setup guidato/ }).getAttribute('aria-checked')) === 'true') {
    const data = (await dialog.locator('pre.command').filter({ hasText: 'HVPORTAL-SETUP' }).innerText()).trim();
    check('guided setup: connection data line (gateway, CA fingerprint, token, role), never localhost',
      /^HVPORTAL-SETUP gateway=https:\/\/\S+:\d+ ca=[0-9A-F]{64} token=\S{30,} role=host$/.test(data) && !/localhost|127\.0\.0\.1/.test(data), data.slice(0, 70));
    const shownSetupHash = (await dialog.locator('.setup-hash').innerText()).trim();
    const [setupDownload] = await Promise.all([page.waitForEvent('download'), dialog.getByRole('link', { name: /Scarica il setup dell'agent/ }).click()]);
    const setupPath = join(out, 'downloaded-agent-setup.bin');
    await setupDownload.saveAs(setupPath);
    check('guided setup: the setup file downloads and matches the displayed SHA-256',
      setupDownload.suggestedFilename() === 'HvPortal.Agent.Setup.exe' && createHash('sha256').update(readFileSync(setupPath)).digest('hex').toUpperCase() === shownSetupHash);
    await shot(page, '03a-guided-setup');
    await dialog.getByRole('radio', { name: 'PowerShell' }).click();
  }
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
  // The tab bar must never show a scrollbar: nothing overflows it vertically (the underline of the active tab used to)
  // and, when the tabs do not fit, it scrolls sideways without a visible bar.
  const tabBar = await page.locator('.page-tabs').first().evaluate(el => {
    const style = getComputedStyle(el);
    return { overflowY: style.overflowY, scrollbarWidth: style.scrollbarWidth, tall: el.scrollHeight - el.clientHeight, wide: el.scrollWidth - el.clientWidth };
  });
  check('host page tab bar has no scrollbar', tabBar.overflowY === 'hidden' && tabBar.scrollbarWidth === 'none' && tabBar.tall <= 0 && tabBar.wide <= 0, JSON.stringify(tabBar));
  await shot(page, '05-host-detail');
  check('host page has the "Gruppi VM Hyper-V" tab', (await page.getByRole('tab', { name: 'Gruppi VM Hyper-V' }).count()) === 1);
  await page.getByRole('tab', { name: 'Impostazioni' }).click();
  await page.getByText('Libreria ISO').first().waitFor();
  check('host settings tab shows the ISO library editor', true);
  await shot(page, '05b-host-settings');
  // Templates (ADR-028): the host gets its template library folder from the settings; the page lists templates per host.
  await page.getByLabel('Libreria template').fill('D:/Templates');
  check('template library: a path that is not a local folder is refused before saving', (await page.getByText('Usa una cartella locale come').count()) === 1);
  await page.getByLabel('Libreria template').fill('D:' + String.fromCharCode(92) + 'Templates');
  await page.locator('form', { has: page.getByLabel('Libreria template') }).getByRole('button', { name: 'Salva' }).click();
  await page.getByText('Impostazioni salvate').first().waitFor();
  check('template library of the host saved from its settings', (await page.getByLabel('Libreria template').inputValue()) === 'D:' + String.fromCharCode(92) + 'Templates');
  await page.getByRole('link', { name: 'Template', exact: true }).click();
  await page.getByRole('heading', { name: /Template/ }).first().waitFor();
  await page.getByText('Nessun template. Prepara una VM').waitFor();
  check('templates page explains how to create the first template', (await page.getByText(/Sysprep/).count()) >= 1);
  await page.getByRole('treeitem', { name: /hv-lab-01/ }).click();
  await page.getByText('Nessun template su questo host.').waitFor();
  check('templates tree lists the hosts (filter from the tree)', page.url().includes('/templates?host='));
  await shot(page, '05c-templates');
  await page.getByRole('link', { name: 'Inventario', exact: true }).click();
  await page.getByRole('treeitem', { name: /hv-lab-01/ }).click();
  await page.getByRole('heading', { name: 'hv-lab-01' }).waitFor();

  // Groups (folders) in the tree
  await page.getByRole('button', { name: 'Nuovo gruppo' }).first().click();
  await page.locator('#group-name').fill('Produzione');
  // Advanced mode: limits of the group (quotas, hosts, networks, storage), with "?" help.
  await page.getByRole('dialog').getByText('Avanzate: limiti del gruppo').click();
  await page.locator('#lim-maxVms').waitFor();
  check('new group dialog has the advanced limits (quotas, hosts, networks, storage)', (await page.locator('#lim-maxProcessors').count()) === 1
    && (await page.getByRole('dialog').getByText('Limita le reti').count()) === 1 && (await page.getByRole('dialog').getByText('Limita lo storage').count()) === 1);
  await page.locator('#lim-maxVms').fill('5');
  await page.locator('#lim-maxMemoryGb').fill('16');
  await shot(page, '06a-new-group-advanced');
  await page.getByRole('dialog').getByRole('button', { name: 'Crea' }).click();
  await page.getByRole('heading', { name: 'Produzione' }).waitFor();
  check('group page shows limits and usage (0 / 5 VMs, 0 / 16 GB)', await page.getByText('0 / 5', { exact: true }).waitFor({ timeout: 10000 }).then(() => true, () => false)
    && (await page.getByText('0 / 16', { exact: true }).count()) === 1);
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

  await page.getByRole('treeitem', { name: /^Tutte le VM/ }).click();
  await page.getByRole('tab', { name: /Tutte le VM/, selected: true }).waitFor();
  check('"Tutte le VM" opens the all-VMs view of the inventory', true);
  await page.getByRole('tab', { name: /Gruppi/ }).click();
  await page.getByRole('button', { name: 'Aiuto' }).first().hover();
  check('groups view explains (in the "?" help) the mirroring to Hyper-V VM groups', await page.getByRole('tooltip').filter({ hasText: 'Get-VMGroup' }).first().waitFor({ timeout: 5000 }).then(() => true, () => false));
  await page.goto(new URL('/vms', url).href);
  check('old /vms link redirects to the inventory', await page.waitForURL(/\/inventory\?view=vms/, { timeout: 5000 }).then(() => true, () => false));
  await page.waitForTimeout(800);
  await shot(page, '07-vms');
  await page.getByRole('link', { name: 'Utenti e ruoli' }).click();
  await page.getByRole('heading', { name: 'Assegnazioni dei ruoli' }).waitFor();
  await shot(page, '08-users');
  // New user: type (Microsoft disabled until Entra is configured), role, MFA at first sign-in.
  await page.getByRole('button', { name: 'Aggiungi utente' }).click();
  const addDlg = page.getByRole('dialog');
  check('new user dialog asks type, role and MFA at first sign-in', (await addDlg.getByRole('radio', { name: /Microsoft/ }).isDisabled())
    && (await addDlg.getByLabel('Ruolo').count()) === 1 && (await addDlg.getByRole('checkbox', { name: /autenticazione al primo accesso/ }).isChecked()));
  await addDlg.locator('#nu-name').fill('mario.rossi');
  await addDlg.getByLabel('Ruolo').selectOption({ label: 'Read only' });
  await addDlg.getByRole('button', { name: 'Crea utente' }).click();
  const reauthDlg = page.getByRole('dialog', { name: 'Conferma la tua identità' });
  if (await reauthDlg.waitFor({ timeout: 3000 }).then(() => true, () => false)) {
    check('re-authentication says it needs YOUR credentials', (await reauthDlg.getByText(/TUO account/).count()) === 1);
    await reauthDlg.locator('#reauth-pw').fill(password);
    await reauthDlg.locator('#reauth-otp-0').fill(totp(secret));
    await reauthDlg.getByRole('button', { name: 'Conferma' }).click();
  }
  await page.getByText(/Al primo accesso l'utente imposta una nuova password e poi configura/).waitFor({ timeout: 15000 });
  check('user created with role and temporary password', (await page.locator('tr', { hasText: 'mario.rossi' }).count()) >= 2);
  await shot(page, '08b-user-created');
  // Delete the user (typed confirmation): the user and the role assignment disappear.
  await page.locator('tr', { hasText: 'mario.rossi' }).first().getByRole('button', { name: 'Elimina' }).click();
  const delDlg = page.getByRole('dialog');
  await delDlg.locator('#confirm-delete').fill('mario.rossi');
  await delDlg.getByRole('button', { name: 'Elimina' }).click();
  const reauthDel = page.getByRole('dialog', { name: 'Conferma la tua identità' });
  if (await reauthDel.waitFor({ timeout: 2500 }).then(() => true, () => false)) {
    await reauthDel.locator('#reauth-pw').fill(password);
    await reauthDel.locator('#reauth-otp-0').fill(totp(secret));
    await reauthDel.getByRole('button', { name: 'Conferma' }).click();
  }
  check('user deleted from Users & roles (row and assignment gone)', await page.locator('tr', { hasText: 'mario.rossi' }).first().waitFor({ state: 'detached', timeout: 15000 }).then(() => true, () => false));
  await page.getByRole('link', { name: 'Audit' }).click();
  check('audit is paginated', (await page.locator('.pager').count()) === 1);
  check('page itself does not scroll (only panels)', await page.evaluate(() => document.documentElement.scrollHeight <= window.innerHeight + 1));
  await page.getByRole('button', { name: 'Verifica integrità' }).click();
  await page.locator('.alert-ok').waitFor();
  check('audit integrity verified from UI', true);
  // Readable audit: actions in words, the name of the resource, translated outcomes; a click shows the details.
  await page.locator('tr.audit-row').first().waitFor();
  const deletedUser = page.locator('tr.audit-row', { hasText: 'Utente eliminato' }).first();
  check('audit describes actions in words with the name of the resource', (await deletedUser.count()) === 1 && (await deletedUser.innerText()).includes('mario.rossi'), (await page.locator('.audit-title').first().innerText()));
  check('audit shows who acted and how, and the outcome in the language of the page',
    (await deletedUser.innerText()).includes('admin') && /account locale/.test(await deletedUser.innerText()) && (await deletedUser.getByText('Riuscito').count()) === 1);
  check('audit rows never show raw JSON', !(await page.locator('tr.audit-row').allInnerTexts()).some((x) => x.includes('{"')));
  await deletedUser.click();
  await page.locator('.audit-details').waitFor();
  const auditDetails = await page.locator('.audit-details').innerText();
  check('audit entry expands to its details (action code, fields)', auditDetails.includes('users.delete') && auditDetails.includes('Codice azione') && auditDetails.includes('mario.rossi'));
  await shot(page, '09-audit');
  await page.getByLabel('Area').selectOption('groups');
  await deletedUser.waitFor({ state: 'detached' });
  await page.locator('tr.audit-row', { hasText: 'Gruppo creato' }).first().waitFor();
  check('audit filters by area', (await page.locator('.audit-title').allInnerTexts()).every((x) => /grupp/i.test(x)));
  await page.getByPlaceholder('Cerca (nome, utente, indirizzo, testo)…').fill('hv-lab-01');
  await page.getByLabel('Area').selectOption('');
  await page.getByRole('button', { name: 'Cerca', exact: true }).click();
  await page.waitForFunction(() => { const rows = [...document.querySelectorAll('tr.audit-row')]; return rows.length > 0 && rows.every((r) => r.innerText.includes('hv-lab-01')); }, null, { timeout: 15000 }).catch(() => {});
  await shot(page, '09b-audit-search');
  check('audit search finds entries by the name of the host', (await page.locator('tr.audit-row').count()) >= 1 && (await page.locator('tr.audit-row').allInnerTexts()).every((x) => x.includes('hv-lab-01')));
  await page.getByRole('link', { name: 'Impostazioni', exact: true }).click();
  await page.getByRole('heading', { name: 'Sicurezza' }).waitFor();
  check('settings are split in tabs', (await Promise.all(['Sicurezza', 'Microsoft Entra ID', 'Backup e ripristino', 'Sistema'].map((n) => page.getByRole('tab', { name: n }).count()))).every((c) => c === 1));
  const box = async (id) => page.locator('#' + id).boundingBox();
  const [idle, abs, pwmin] = [await box('s-idle'), await box('s-abs'), await box('s-pw')];
  check('inputs in the same row are aligned', !!idle && !!abs && !!pwmin && Math.abs(idle.y - abs.y) < 1 && Math.abs(abs.y - pwmin.y) < 1, `${idle?.y} ${abs?.y} ${pwmin?.y}`);
  await page.getByRole('tab', { name: 'Microsoft Entra ID' }).click();
  await page.getByRole('heading', { name: 'Microsoft Entra ID' }).waitFor();
  const [secretBox, cloudBox] = [await box('en-secret'), await box('en-cloud')];
  check('Entra secret and cloud fields are aligned', !!secretBox && !!cloudBox && Math.abs(secretBox.y - cloudBox.y) < 1, `${secretBox?.y} ${cloudBox?.y}`);
  check('settings have the Microsoft Entra ID section with the redirect URI to register',
    await page.getByText(/\/api\/v1\/auth\/entra\/callback$/).first().waitFor({ timeout: 10000 }).then(() => true, () => false));
  await shot(page, '10-settings');
  // Portal backup: sealed download, then restore preview (not applied).
  const maybeReauth = async () => {
    const dlg = page.getByRole('dialog', { name: 'Conferma la tua identità' });
    if (await dlg.waitFor({ timeout: 2500 }).then(() => true, () => false)) {
      await dlg.locator('#reauth-pw').fill(password);
      await dlg.locator('#reauth-otp-0').fill(totp(secret));
      await dlg.getByRole('button', { name: 'Conferma' }).click();
    }
  };
  const bkPass = 'e2e backup passphrase long enough 2026';
  await page.getByRole('tab', { name: 'Backup e ripristino' }).click();
  await page.locator('#bk-pass').waitFor();
  const [p1, p2] = [await box('bk-pass'), await box('bk-pass2')];
  check('backup passphrase fields are aligned', !!p1 && !!p2 && Math.abs(p1.y - p2.y) < 1, `${p1?.y} ${p2?.y}`);
  await page.locator('#bk-pass').fill(bkPass);
  await page.locator('#bk-pass2').fill(bkPass);
  const bkDownload = page.waitForEvent('download', { timeout: 30000 });
  await page.getByRole('button', { name: 'Scarica backup' }).click();
  await maybeReauth();
  const bk = await bkDownload;
  const bkPath = join(out, bk.suggestedFilename());
  await bk.saveAs(bkPath);
  const bkBytes = readFileSync(bkPath);
  check('backup downloads as a sealed .hvpbak (not a plain database)', bk.suggestedFilename().endsWith('.hvpbak') && bkBytes.length > 4096 && !bkBytes.includes(Buffer.from('SQLite format 3')));
  await page.locator('input[type=file][accept=".hvpbak"]').setInputFiles(bkPath);
  await page.locator('#bk-rpass').fill(bkPass);
  await page.getByRole('button', { name: 'Verifica backup' }).click();
  await maybeReauth();
  check('restore preview shows the backup contents', await page.getByText(/\d+ utenti, 1 host, \d+ VM, \d+ gruppi/).waitFor({ timeout: 15000 }).then(() => true, () => false));
  await shot(page, '10c-backup-preview');
  // Automatic database copies: "Copy now" writes a verified copy, listed with its date.
  await page.getByRole('button', { name: 'Copia adesso' }).click();
  await maybeReauth();
  check('automatic database copy created on demand and listed', await page.getByText(/^hvportal-auto-\d{8}-\d{6}\.db$/).first().waitFor({ timeout: 20000 }).then(() => true, () => false)
    && (await page.getByText(/ogni 24 ore, conservate per 7 giorni/).count()) === 1);
  await shot(page, '10d-auto-backup');
  // Networks: the navigator shows the hosts and their switches (creation needs a host with Hyper-V access).
  await page.getByRole('link', { name: 'Reti', exact: true }).click();
  await page.getByRole('heading', { name: 'Rete' }).waitFor();
  check('networks page: navigator tree with "Tutte le reti" and the host', (await page.getByRole('treeitem', { name: 'Tutte le reti' }).count()) === 1
    && (await page.getByRole('treeitem', { name: /hv-lab-01/ }).count()) === 1);
  await shot(page, '10e-networks');
  await page.getByRole('link', { name: 'Attività', exact: true }).click();
  await page.getByRole('treeitem', { name: 'Non riuscita' }).click();
  check('tasks page: status filter chosen from the navigator tree', await page.waitForURL(/\/jobs\?status=Failed/, { timeout: 5000 }).then(() => true, () => false));

  if (filesDir) {
    // Host file explorer (real agent, real file system of this machine).
    rmSync(join(filesDir, 'uploaded.bin'), { force: true });
    await page.getByRole('link', { name: 'Storage', exact: true }).click();
    await page.getByRole('tab', { name: 'File' }).click();
    await page.getByRole('button', { name: /C:/ }).first().click();
    await page.getByRole('row', { name: 'Apri ' + filesDir.split('\\')[1], exact: true }).waitFor();
    const rootRows = await Promise.all(['Windows', 'Users', 'Program Files', 'ProgramData', 'inetpub'].map((n) => page.getByRole('row', { name: 'Apri ' + n, exact: true }).count()));
    check('file explorer hides system folders (Windows, Users, Program Files, ProgramData, inetpub)', rootRows.every((c) => c === 0));
    for (const seg of filesDir.split('\\').slice(1)) await page.getByRole('row', { name: 'Apri ' + seg, exact: true }).click();
    await page.getByRole('link', { name: 'Scarica sample.txt' }).waitFor();
    const [dl] = await Promise.all([page.waitForEvent('download'), page.getByRole('link', { name: 'Scarica sample.txt' }).click()]);
    const dlPath = join(out, 'sample-downloaded.txt');
    await dl.saveAs(dlPath);
    check('file download matches the file on the host', readFileSync(dlPath).equals(readFileSync(join(filesDir, 'sample.txt'))));
    const payload = Buffer.alloc(5 * 1024 * 1024 + 123);
    for (let i = 0; i < payload.length; i++) payload[i] = (i * 31 + 7) & 255;
    await page.locator('input[type=file]').setInputFiles({ name: 'uploaded.bin', mimeType: 'application/octet-stream', buffer: payload });
    await page.getByText('uploaded.bin caricato e verificato.').waitFor({ timeout: 30000 });
    check('file upload (2 chunks, SHA-256 verified) lands on the host', readFileSync(join(filesDir, 'uploaded.bin')).equals(payload));
    await page.getByRole('link', { name: 'Scarica uploaded.bin' }).waitFor();
    await shot(page, '10b-storage-files');
    await page.locator('input[type=file]').setInputFiles({ name: 'uploaded.bin', mimeType: 'application/octet-stream', buffer: Buffer.from('x') });
    await page.getByRole('heading', { name: 'Il file esiste già' }).waitFor();
    check('uploading an existing file asks before replacing it', true);
    await page.getByRole('dialog').getByRole('button', { name: 'Annulla' }).click();
    check('declined replacement leaves the file unchanged', readFileSync(join(filesDir, 'uploaded.bin')).equals(payload));
    rmSync(join(filesDir, 'uploaded.bin'), { force: true });
  }

  await page.getByRole('link', { name: 'Guida', exact: true }).click();
  await page.getByRole('heading', { name: 'Guida', exact: true }).waitFor();
  const guideCards = ['Collegare un host Hyper-V', 'Aggiornare i servizi Windows', 'Rimuovere un host', 'Risoluzione dei problemi'];
  check('guide has separate pages', (await Promise.all(guideCards.map((n) => page.getByRole('link', { name: new RegExp(n) }).count()))).every((c) => c >= 1));
  await shot(page, '11-guide-home');
  await page.getByRole('link', { name: /Aggiornare i servizi Windows/ }).first().click();
  await page.getByRole('heading', { name: 'Aggiornare i servizi Windows' }).waitFor();
  await page.locator('tr', { hasText: 'hv-lab-01' }).first().waitFor();
  await page.getByText(/Pacchetto agente su questo gateway: \d+\.\d+\.\d+/).waitFor();
  check('update page: per-host agent version status', (await page.locator('tr', { hasText: 'hv-lab-01' }).getByText(/Aggiornato|Da aggiornare|Più recente/).count()) === 1);
  const agentCmd = await page.locator('pre.command').filter({ hasText: 'Install-Agent.ps1' }).first().innerText();
  check('update page: agent update command keeps the enrollment (no token)', /agent-package-\d+\.\d+\.\d+\\installer/.test(agentCmd) && /Install-Agent\.ps1 -Source \.\.\\agent\s*$/.test(agentCmd.split('\n')[1] ?? ''), agentCmd.replace(/\n/g, ' | '));
  check('update page: gateway section with rollback steps', (await page.locator('pre.command').filter({ hasText: 'Install-Gateway.ps1' }).count()) >= 2);
  check('update page: detects a gateway not running as a service', await page.getByText(/non è in esecuzione come servizio Windows/).waitFor({ timeout: 10000 }).then(() => true, () => false));
  await shot(page, '11b-guide-update');
  // The pages of the guide are listed in the navigator tree.
  await page.getByRole('treeitem', { name: 'Collegare un host Hyper-V' }).click();
  await page.getByRole('heading', { name: 'Collegare un host Hyper-V' }).waitFor();
  // Both installation methods are documented there; the PowerShell one keeps the commands.
  await page.locator('[role="radio"][aria-checked="true"]').waitFor();
  await page.getByRole('radio', { name: 'PowerShell' }).click();
  await page.locator('pre.command').filter({ hasText: 'Test-NetConnection' }).waitFor();
  check('guide page documents both installation methods', (await page.getByRole('radio', { name: /Setup guidato/ }).count()) === 1);
  await page.getByRole('treeitem', { name: 'Risoluzione dei problemi' }).click();
  await page.getByRole('heading', { name: 'Risoluzione dei problemi' }).first().waitFor();
  check('troubleshooting page with its sections', (await page.getByRole('heading', { level: 2 }).count()) >= 4);
  check('guide pages available in Italian', true);
  await shot(page, '11c-guide-trouble');

  // Theme + language from the account menu
  await page.locator('.account-button').click();
  check('account menu with language and theme', (await page.getByRole('menuitemradio', { name: 'Scuro' }).count()) === 1);
  await page.getByRole('menuitemradio', { name: 'English' }).click();
  await page.getByRole('menuitemradio', { name: 'Dark' }).click();
  await shot(page, '12-account-menu');
  await page.keyboard.press('Escape');
  check('dark theme applied', await page.evaluate(() => document.documentElement.classList.contains('dark')));
  await page.getByRole('link', { name: 'Inventory', exact: true }).click();
  await page.waitForTimeout(500);
  // Navigation icons in the dark theme: white, and only the current section is highlighted.
  await page.mouse.move(700, 500);
  await page.waitForTimeout(400);
  const darkNav = await page.locator('nav.nav a').evaluateAll((links) => links.map((a) => { const s = getComputedStyle(a); return { color: s.color, bg: s.backgroundColor, current: a.getAttribute('aria-current') === 'page', h: a.offsetHeight }; }));
  const transparent = (c) => c === 'rgba(0, 0, 0, 0)' || c === 'transparent';
  check('dark theme: navigation icons are white and only the current one is highlighted',
    darkNav.length >= 5 && darkNav.every((i) => i.color === 'rgb(255, 255, 255)' && i.h >= 36) && darkNav.filter((i) => !transparent(i.bg)).length === 1 && darkNav.every((i) => transparent(i.bg) !== i.current),
    JSON.stringify(darkNav.slice(0, 3)));
  await shot(page, '13-hosts-dark-en');
  check('language switch to English', (await page.getByRole('heading', { name: 'Inventory' }).count()) > 0 && (await page.getByRole('treeitem', { name: /^All VMs/ }).count()) === 1);
  await page.getByRole('link', { name: 'Dashboard', exact: true }).click();
  await page.waitForTimeout(500);
  await shot(page, '14-dashboard-dark');
  await page.getByRole('link', { name: 'Guide', exact: true }).click();
  await page.getByRole('heading', { name: 'Guide', exact: true }).waitFor();
  await page.getByRole('link', { name: /Update the Windows services/ }).first().click();
  await page.getByRole('heading', { name: 'Update the Windows services' }).waitFor();
  check('guide pages available in English', true);

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
