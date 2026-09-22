// End-to-end check against a running container: sign up, then use controls that need the live
// (interactive) connection. Usage: node interactivity.mjs http://localhost:8080
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:8080';
const log = [];
const note = (line) => { log.push(line); console.log(line); };

const browser = await chromium.launch();
const page = await browser.newPage({ colorScheme: 'dark' });
page.on('console', (m) => { if (m.type() === 'error' || m.type() === 'warning') note(`console.${m.type()}: ${m.text()}`); });
page.on('pageerror', (e) => note(`pageerror: ${e.message}`));
page.on('requestfailed', (r) => note(`requestfailed: ${r.url()} ${r.failure()?.errorText}`));
page.on('response', (r) => { if (r.status() >= 400) note(`http ${r.status()}: ${r.url()}`); });
page.on('websocket', (ws) => {
  note(`websocket open: ${ws.url()}`);
  ws.on('close', () => note('websocket closed'));
  ws.on('socketerror', (e) => note(`websocket error: ${e}`));
});

// After a full page load the button renders before the interactive connection is ready, so a
// click can be lost. Click again until the dialog opens.
async function openDialog(buttonText) {
  for (let attempt = 0; attempt < 10; attempt++) {
    await page.click(`text=${buttonText}`);
    if (await page.waitForSelector('.mud-dialog input', { timeout: 3000 }).then(() => true, () => false)) return;
  }
  throw new Error(`"${buttonText}" did not open its dialog`);
}

let failed = false;
try {
  await page.goto(`${base}/Account/Register`);
  await page.fill('[id="Input.Email"]', 'e2e@example.com');
  await page.fill('[id="Input.Password"]', 'E2e!Password1');
  await page.fill('[id="Input.ConfirmPassword"]', 'E2e!Password1');
  await page.click('button[type=submit]');
  await page.waitForSelector('text=Getting started', { timeout: 30000 });
  await page.waitForTimeout(5000);

  const bg = () => page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  const before = await bg();
  note(`background before toggle: ${before} (browser prefers dark)`);

  await page.click('button[aria-label="Toggle dark mode"]');
  await page.waitForTimeout(2000);
  const after = await bg();
  note(`background after toggle: ${after}`);
  if (before === after) { note('FAIL: theme toggle did nothing'); failed = true; }

  await page.click('.avatar-button');
  const menuOpened = await page.waitForSelector('text=Sign out', { timeout: 5000 }).then(() => true, () => false);
  note(`account menu opened: ${menuOpened}`);
  if (!menuOpened) { note('FAIL: account menu did not open'); failed = true; }
  await page.keyboard.press('Escape');

  // Categories: add one through the dialog and see it listed.
  await page.goto(`${base}/categories`);
  await openDialog('Add category');
  await page.fill('.mud-dialog input', 'E2E Groceries');
  await page.click('.mud-dialog button:has-text("Save")');
  const categoryListed = await page.waitForSelector('.mud-table-body >> text=E2E Groceries', { timeout: 5000 }).then(() => true, () => false);
  note(`category added and listed: ${categoryListed}`);
  if (!categoryListed) { note('FAIL: new category did not appear'); failed = true; }

  // Payees: add one with a website and see its link listed.
  await page.goto(`${base}/payees`);
  await openDialog('Add payee');
  await page.fill('.mud-dialog input >> nth=0', 'E2E Power Co');
  await page.fill('.mud-dialog input[type=url]', 'power.example.com');
  await page.click('.mud-dialog button:has-text("Save")');
  const payeeLink = await page.waitForSelector('.mud-table-body a[href="https://power.example.com/"]', { timeout: 5000 }).then(() => true, () => false);
  note(`payee added with website link: ${payeeLink}`);
  if (!payeeLink) { note('FAIL: new payee or its website link did not appear'); failed = true; }

  // Transfers: two accounts, then a recurring transfer between them, listed with both ends.
  await page.goto(`${base}/accounts`);
  for (const name of ['E2E Checking', 'E2E Savings']) {
    await openDialog('Add account');
    await page.fill('.mud-dialog input >> nth=0', name);
    await page.click('.mud-dialog button:has-text("Save")');
    await page.waitForSelector(`.mud-table-body >> text=${name}`, { timeout: 5000 });
  }

  await page.goto(`${base}/transfers`);
  await openDialog('Add transfer');
  await page.fill('.mud-dialog input >> nth=0', 'E2E Savings Move');
  await page.fill('.mud-dialog input >> nth=1', '125');
  // The accounts default to the two above; the first date has to be picked (the picker is editable).
  await page.getByLabel('First date').fill(new Date().toLocaleDateString('en-US'));
  await page.keyboard.press('Enter');
  await page.click('.mud-dialog button:has-text("Save")');
  const transferRow = await page.waitForSelector('.mud-table-body tr:has-text("E2E Savings Move")', { timeout: 5000 }).then((h) => h, () => null);
  const transferEnds = transferRow === null ? '' : await transferRow.innerText();
  const transferListed = transferEnds.includes('E2E Checking') && transferEnds.includes('E2E Savings');
  note(`transfer added and listed with both accounts: ${transferListed}`);
  if (!transferListed) { note(`FAIL: new transfer did not appear: ${transferEnds || await page.locator('.mud-dialog').allInnerTexts()}`); failed = true; }

  // Backup & restore: download a backup, add a category afterwards, restore, and check the data is back to the backup's.
  const download = await page.request.get(`${base}/Account/Manage/Admin/Backup/Download`);
  note(`backup download: ${download.status()} ${download.headers()['content-type']}`);
  if (!download.ok()) throw new Error('backup download failed');
  const fs = await import('node:fs');
  fs.writeFileSync('backup.zip', await download.body());

  await page.goto(`${base}/categories`);
  await openDialog('Add category');
  await page.fill('.mud-dialog input', 'E2E After Backup');
  await page.click('.mud-dialog button:has-text("Save")');
  await page.waitForSelector('.mud-table-body >> text=E2E After Backup', { timeout: 5000 });

  await page.goto(`${base}/Account/Manage/Admin/Backup`);
  await page.setInputFiles('#backup-file', 'backup.zip');
  await page.check('input[name=confirm]');
  await page.click('button:has-text("Restore backup")');
  const restored = await page.waitForSelector('text=Restored the backup', { timeout: 30000 }).then(() => true, () => false);
  note(`backup restored: ${restored}`);
  if (!restored) throw new Error(`restore did not finish: ${await page.locator('.acct-alert').allInnerTexts()}`);

  await page.fill('[id="Input.Email"]', 'e2e@example.com');
  await page.fill('[id="Input.Password"]', 'E2e!Password1');
  await page.click('button[type=submit]');
  await page.waitForURL((url) => !url.pathname.includes('/Account/Login'), { timeout: 30000 });
  await page.goto(`${base}/categories`);
  await page.waitForSelector('.mud-table-body >> text=E2E Groceries', { timeout: 10000 });
  const afterBackupGone = await page.locator('.mud-table-body >> text=E2E After Backup').count() === 0;
  note(`data matches the backup after restore: ${afterBackupGone}`);
  if (!afterBackupGone) { note('FAIL: the category added after the backup is still there'); failed = true; }
} catch (e) {
  note(`FAIL: ${e.message}`);
  failed = true;
} finally {
  await page.screenshot({ path: 'interactivity.png', fullPage: true });
  await browser.close();
}

process.exit(failed ? 1 : 0);
