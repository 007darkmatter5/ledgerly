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
  const dialog = page.locator('.mud-dialog');
  await dialog.getByLabel('Name').fill('E2E Savings Move');
  await dialog.getByLabel('Amount').fill('125');
  // The accounts default to the two above. Typing the first date also opens the picker's calendar,
  // which then covers the dialog: clicking today in it both sets the date and closes it, because
  // DateOnlyPicker sets AutoClose. If it isn't open there is nothing to close, so don't insist.
  await dialog.getByLabel('First date').fill(new Date().toLocaleDateString('en-US'));
  await page.locator('.mud-picker-calendar .mud-day.mud-current:not(.mud-adjacent-month)')
    .click({ timeout: 5000 }).catch(() => {});
  await page.click('.mud-dialog button:has-text("Save")');
  const transferRow = await page.waitForSelector('.mud-table-body tr:has-text("E2E Savings Move")', { timeout: 5000 }).then((h) => h, () => null);
  const transferEnds = transferRow === null ? '' : await transferRow.innerText();
  const transferListed = transferEnds.includes('E2E Checking') && transferEnds.includes('E2E Savings');
  note(`transfer added and listed with both accounts: ${transferListed}`);
  if (!transferListed) { note(`FAIL: new transfer did not appear: ${transferEnds || await page.locator('.mud-dialog').allInnerTexts()}`); failed = true; }

  // Transactions: quick-add one from the app bar (account and date default to the last-used/checking account and today),
  // then see it on the Transactions page and in the Payments history.
  await page.goto(`${base}/transactions`);
  let quickAddOpened = false;
  for (let attempt = 0; attempt < 10 && !quickAddOpened; attempt++) {
    await page.click('button[aria-label="Add a transaction"]');
    quickAddOpened = await page.waitForSelector('.mud-dialog input', { timeout: 3000 }).then(() => true, () => false);
  }
  if (!quickAddOpened) throw new Error('the app bar button did not open the transaction dialog');
  const transactionDialog = page.locator('.mud-dialog');
  await transactionDialog.getByLabel('Amount').fill('62.40');
  await transactionDialog.getByLabel('What was it for?').fill('E2E Dinner out');
  // Exactly "Save": "Save & add another" comes first and would keep the dialog open.
  await transactionDialog.getByRole('button', { name: 'Save', exact: true }).click();
  const transactionRow = await page.waitForSelector('.mud-table-body tr:has-text("E2E Dinner out")', { timeout: 5000 }).then((h) => h, () => null);
  const transactionText = transactionRow === null ? '' : await transactionRow.innerText();
  const transactionListed = transactionText.includes('62.40') && transactionText.includes('E2E Checking');
  note(`transaction quick-added and listed: ${transactionListed}`);
  if (!transactionListed) { note(`FAIL: new transaction did not appear: ${transactionText || await page.locator('.mud-dialog').allInnerTexts()}`); failed = true; }

  await page.goto(`${base}/payments`);
  const inPayments = await page.waitForSelector('.mud-table-body tr:has-text("E2E Dinner out")', { timeout: 10000 }).then(() => true, () => false);
  note(`transaction shown in payment history: ${inPayments}`);
  if (!inPayments) { note('FAIL: the transaction is missing from the Payments history'); failed = true; }

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

  // Sharing: a second user signs up; the first shares their ledger. The invitation pops up live in the second
  // user's open tab; once accepted, the first user's transaction shows for them tagged and read-only, and switching
  // the ledger off in the Ledgers menu hides it again.
  const other = await browser.newPage();
  other.on('pageerror', (e) => note(`other pageerror: ${e.message}`));
  await other.goto(`${base}/Account/Register`);
  await other.fill('[id="Input.Email"]', 'e2e-partner@example.com');
  await other.fill('[id="Input.Password"]', 'E2e!Password1');
  await other.fill('[id="Input.ConfirmPassword"]', 'E2e!Password1');
  await other.click('button[type=submit]');
  await other.waitForSelector('text=Getting started', { timeout: 30000 });
  await other.waitForTimeout(3000);

  await page.goto(`${base}/sharing`);
  const email = page.getByLabel('Their email address');
  await email.waitFor({ timeout: 10000 });
  await page.waitForTimeout(2000);
  await email.fill('e2e-partner@example.com');
  await page.getByRole('button', { name: 'Share', exact: true }).click();
  const invited = await page.waitForSelector('text=waiting for them to accept', { timeout: 10000 }).then(() => true, () => false);
  note(`ledger shared (invitation sent): ${invited}`);
  if (!invited) { note('FAIL: the invitation was not listed'); failed = true; }

  const livePrompt = await other.waitForSelector('text=shared a ledger with you', { timeout: 15000 }).then(() => true, () => false);
  note(`invitation popped up live for the other user: ${livePrompt}`);
  if (!livePrompt) {
    note('FAIL: the invitation did not appear without a reload');
    failed = true;
    await other.reload();
    await other.waitForSelector('text=shared a ledger with you', { timeout: 15000 });
  }
  await other.getByRole('button', { name: 'Accept', exact: true }).click();
  await other.waitForTimeout(2000);

  await other.goto(`${base}/transactions`);
  const sharedRow = await other.waitForSelector('.mud-table-body tr:has-text("E2E Dinner out")', { timeout: 10000 }).then((h) => h, () => null);
  const sharedText = sharedRow === null ? '' : await sharedRow.innerText();
  const tagged = sharedText.includes("e2e@example.com's ledger");
  note(`shared transaction shown with its ledger tag: ${tagged}`);
  if (!tagged) { note(`FAIL: shared transaction missing or untagged: ${sharedText}`); failed = true; }
  if (sharedRow !== null) {
    // A locator, not the handle: the table re-renders once the page's live connection starts.
    await other.locator('.mud-table-body tr', { hasText: 'E2E Dinner out' }).click();
    const opened = await other.waitForSelector('.mud-dialog', { timeout: 2000 }).then(() => true, () => false);
    note(`shared transaction is read-only (no edit dialog): ${!opened}`);
    if (opened) { note('FAIL: a shared transaction opened for editing'); failed = true; }
  }

  await other.click('button[aria-label="Ledgers"]');
  await other.click(".ledgers-menu >> text=e2e@example.com's ledger");
  const hidden = await other.waitForSelector('.mud-table-body tr:has-text("E2E Dinner out")', { state: 'detached', timeout: 10000 }).then(() => true, () => false);
  note(`switching the shared ledger off hides its data: ${hidden}`);
  if (!hidden) { note('FAIL: the shared transaction is still shown after switching the ledger off'); failed = true; }
  await other.close();
} catch (e) {
  note(`FAIL: ${e.message}`);
  failed = true;
} finally {
  await page.screenshot({ path: 'interactivity.png', fullPage: true });
  await browser.close();
}

process.exit(failed ? 1 : 0);
