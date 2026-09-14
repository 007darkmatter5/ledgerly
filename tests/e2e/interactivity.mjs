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
} catch (e) {
  note(`FAIL: ${e.message}`);
  failed = true;
} finally {
  await page.screenshot({ path: 'interactivity.png', fullPage: true });
  await browser.close();
}

process.exit(failed ? 1 : 0);
