/* Decode real videos from each topic; never targets the production server. */
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const live = path.join(__dirname, '../artifacts/live');
const s = JSON.parse(fs.readFileSync(path.join(live, 'session.json'), 'utf8'));
const examples = [
  ['music', 'ncDkosGdvRw'], ['gaming', 'aSZpOOysMuU'], ['sports', 'ePqxn6WvS94'],
  ['travel', 'IZ06d_YZ2Yw'], ['technology', '-sodko1R1Wc'], ['science', 'n7lneYDAB9I'], ['food', 'EFibBc-pUtI']
];
const headers = { 'X-Emby-Token': s['tube-alice'].token, 'Content-Type': 'application/json' };
async function api(route, method = 'GET', body) {
  const r = await fetch(s.base + route, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(20000) });
  assert.ok(r.ok, `${route.split('?')[0]}: ${r.status}`); return r.status === 204 ? null : r.json();
}
(async () => {
  assert.match(s.base, /^http:\/\/127\.0\.0\.1:/);
  assert.equal((await api('/System/Info/Public')).Id, s.serverId);
  const settings = await api('/JellyTube/Me/Settings');
  const browser = await chromium.launch({ headless: true }); const checks = [];
  try {
    await api('/JellyTube/Me/Settings', 'PUT', { ...settings, maxHeight: 1080 });
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } }); page.setDefaultTimeout(20000);
    await page.goto(s.base + '/web/');
    await page.locator('#txtManualName').fill('tube-alice'); await page.locator('#txtManualPassword').fill(s.password);
    await page.locator('#txtManualPassword').press('Enter'); await page.locator('#jt-launch').click();
    await page.locator('.jt-nav').getByRole('button', { name: 'Search', exact: true }).click();
    for (const [topic, id] of examples) {
      await page.getByLabel('Search videos or paste a YouTube URL').fill(id);
      const search = page.waitForResponse(r => r.url().includes('/JellyTube/Search?') && r.request().method() === 'GET');
      await page.locator('.jt-form').getByRole('button', { name: 'Search', exact: true }).click(); await search;
      const card = page.locator('.jt-card').filter({ has: page.locator(`img[src*="${id}"]`) }).first();
      const start = performance.now(); await card.getByRole('button', { name: '▶ Play', exact: true }).click();
      const dialog = page.locator('.jt-direct-player[open]');
      await dialog.getByText(/Direct playback ·/).waitFor();
      const video = await dialog.locator('video').evaluate(v => ({ width: v.videoWidth, height: v.videoHeight, paused: v.paused }));
      assert.ok(video.width > 0 && video.height > 0 && !video.paused);
      checks.push({ topic, id, firstFrameMs: Math.round(performance.now() - start), height: video.height, passed: true });
      await dialog.getByRole('button', { name: 'Close video', exact: true }).click();
      await dialog.waitFor({ state: 'detached' });
      assert.ok(await page.locator('video').evaluateAll(videos => videos.every(v => v.paused || v.ended)));
      console.log(JSON.stringify(checks.at(-1)));
    }
  } finally {
    fs.writeFileSync(path.join(live, 'playback-topics.json'), JSON.stringify(checks, null, 2));
    await api('/JellyTube/Me/Settings', 'PUT', settings); await browser.close();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
