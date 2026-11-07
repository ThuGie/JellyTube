/* Integration UI checks against the isolated Jellyfin session created by scripts/live-test.py. */
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const live = path.join(__dirname, '../artifacts/live');
const s = JSON.parse(fs.readFileSync(path.join(live, 'session.json'), 'utf8'));
(async () => {
  const browser = await chromium.launch({ headless: true }); const checks = [];
  try {
    for (const [name, width, height] of [['desktop', 1440, 1000], ['mobile', 390, 844]]) {
      const page = await browser.newPage({ viewport: { width, height } }); page.setDefaultTimeout(20000);
      const errors = []; page.on('pageerror', e => errors.push(e.message));
      await page.goto(s.base + '/web/');
      await page.locator('#txtManualName').fill('tube-alice'); await page.locator('#txtManualPassword').fill(s.password); await page.locator('#txtManualPassword').press('Enter');
      await page.locator('#jt-launch').waitFor(); await page.locator('#jt-launch').click();
      await page.getByRole('heading', { name: 'YouTube', exact: true }).waitFor();
      const feeds = page.getByRole('navigation', { name: 'Home feeds' });
      await feeds.getByRole('button', { name: 'Following', exact: true }).click();
      assert.equal(await feeds.getByRole('button', { name: 'Following', exact: true }).getAttribute('aria-pressed'), 'true');
      await feeds.getByRole('button', { name: 'For you', exact: true }).click();
      const formats = page.getByRole('navigation', { name: 'Video formats' });
      await formats.getByRole('button', { name: 'Shorts', exact: true }).click();
      assert.equal(await formats.getByRole('button', { name: 'Shorts', exact: true }).getAttribute('aria-pressed'), 'true');
      await formats.getByRole('button', { name: 'Full videos', exact: true }).click();
      await page.screenshot({ path: path.join(live, name + '-home.png') });
      await page.getByRole('button', { name: 'Search', exact: true }).first().click();
      await page.getByLabel('Search videos or paste a YouTube URL').fill('aqz-KE-bpKQ');
      await page.locator('.jt-form').getByRole('button', { name: 'Search', exact: true }).click();
      await page.locator('.jt-card').first().waitFor(); await page.screenshot({ path: path.join(live, name + '-search.png') });
      assert.ok(await page.locator('.jt-card-info').first().innerText());
      const favorite = page.locator('.jt-card').first().getByRole('button', { name: /Favorit/ });
      if ((await favorite.textContent()).includes('Favorited')) { await favorite.click(); await expectButton(favorite, '☆ Favorite'); }
      await favorite.click(); await expectButton(favorite, '★ Favorited');
      await page.getByRole('button', { name: 'Settings', exact: true }).click();
      await page.getByLabel('Maximum download resolution').selectOption('720');
      await page.getByRole('button', { name: 'Save preferences', exact: true }).click(); await page.getByText('Preferences saved.', { exact: true }).waitFor();
      await page.screenshot({ path: path.join(live, name + '-settings.png') });
      assert.equal(await page.locator('#jt-app').evaluate(e => e.scrollWidth <= e.clientWidth), true, 'no horizontal overflow');
      await page.getByRole('button', { name: 'Library', exact: true }).click(); await page.locator('.jt-card').first().waitFor();
      await page.screenshot({ path: path.join(live, name + '-library.png') });
      if (name === 'desktop') {
        const play = page.locator('.jt-card').first().getByRole('button', { name: '▶ Play', exact: true });
        const details = page.locator('.jt-card').first().getByRole('button', { name: 'Details', exact: true });
        await details.focus();
        await page.keyboard.press('Enter');
        await page.locator('dialog[open]').waitFor();
        await page.keyboard.press('Escape');
        await page.locator('dialog[open]').waitFor({ state: 'detached' });
        assert.equal(await details.evaluate(button => button === document.activeElement), true, 'details restores keyboard focus');
        let release;
        const held = new Promise(resolve => { release = resolve; });
        const preparation = '**/JellyTube/Videos/*/Prepare';
        await page.route(preparation, async route => {
          await held;
          await route.abort().catch(() => {});
        }, { times: 1 });
        await play.click();
        await page.locator('.jt-playback-notice').getByRole('button', { name: 'Cancel' }).click();
        release();
        await page.locator('.jt-playback-notice').waitFor({ state: 'detached' });
        assert.ok(await play.isVisible());
        checks.push({ check: 'keyboard details and cancelling a stalled playback preparation', passed: true });
        const started = performance.now();
        await play.click();
        await page.waitForFunction(() => document.getElementById('jt-app')?.hidden, null, { timeout: 60000 });
        await page.waitForFunction(() => document.querySelector('video') && document.querySelector('video').readyState >= 2, null, { timeout: 60000 }).catch(async error => {
          await page.screenshot({ path: path.join(live, 'native-playback-failure.png') });
          console.log(await page.evaluate(() => ({ hash: location.hash, dialogs: [...document.querySelectorAll('[role=dialog],dialog')].map(d => d.textContent.slice(0, 500)), video: [...document.querySelectorAll('video')].map(v => ({ readyState: v.readyState, error: v.error?.message })), text: document.body.innerText.slice(-900) })));
          throw error;
        });
        const firstFrameMs = Math.round(performance.now() - started);
        const state = await page.evaluate(() => { const v = document.querySelector('video'); return { readyState: v.readyState, width: v.videoWidth, currentTime: v.currentTime }; });
        assert.ok(state.width > 0); await page.screenshot({ path: path.join(live, name + '-native-player.png') });
        await page.goBack();
        await page.getByRole('heading', { name: 'Your library' }).waitFor();
        assert.ok(await page.locator('#jt-app .jt-card').first().isVisible());
        checks.push({ check: 'one-click native web playback', passed: true, videoWidth: state.width, firstFrameMs });
        const warmStarted = performance.now();
        await play.click();
        await page.waitForFunction(() => {
          const video = document.querySelector('video');
          return document.getElementById('jt-app')?.hidden && video?.readyState >= 2 && !video.paused && video.currentTime > 0;
        }, null, { timeout: 60000 });
        checks.push({ check: 'repeat native web playback', passed: true, firstFrameMs: Math.round(performance.now() - warmStarted) });
        await page.goBack();
        await page.getByRole('heading', { name: 'Your library' }).waitFor();
      }
      checks.push({ check: name + ' real Jellyfin search, favorites, settings, library and layout', passed: true, pageErrors: errors });
      assert.deepEqual(errors.filter(e => !e.includes('ResizeObserver')), []); await page.close();
    }
    // Hold a real Alice response across an account change. It must not populate Bob's newly opened interface.
    {
      const page = await browser.newPage();
      await page.goto(s.base + '/web/'); await page.locator('#txtManualName').fill('tube-alice'); await page.locator('#txtManualPassword').fill(s.password); await page.locator('#txtManualPassword').press('Enter');
      await page.locator('#jt-launch').waitFor();
      let release, signal, first = true;
      const held = new Promise(resolve => { release = resolve; }), started = new Promise(resolve => { signal = resolve; });
      await page.route('**/JellyTube/Me/Library', async route => {
        if (!first) return route.continue(); first = false;
        const response = await route.fetch(); signal(); await held; await route.fulfill({ response });
      });
      await page.evaluate(() => { window.jellyTubeOpening = JellyTube.open('library'); }); await started;
      await page.evaluate(({ id, token }) => {
        const client = window.ApiClient, originalHeaders = client.setRequestHeaders.bind(client);
        client.getCurrentUserId = () => id; client.accessToken = () => token;
        client.setRequestHeaders = headers => {
          originalHeaders(headers);
          for (const name of Object.keys(headers)) if (/authorization|x-emby-token|x-mediabrowser-token/i.test(name)) delete headers[name];
          headers.Authorization = `MediaBrowser Client="JellyTubeRaceTest", DeviceId="race-test", Version="1", Token="${token}"`;
        };
        document.body.appendChild(document.createElement('span'));
      }, s['tube-bob']);
      release(); await page.evaluate(() => window.jellyTubeOpening); await page.evaluate(() => JellyTube.open('library'));
      await page.getByText('Nothing saved here yet. Find a video in Search to get started.', { exact: true }).waitFor();
      assert.equal(await page.locator('#jt-app .jt-card').count(), 0);
      await page.screenshot({ path: path.join(live, 'account-switch-privacy.png') });
      checks.push({ check: 'late API responses cannot leak the previous account library', passed: true }); await page.close();
    }
    // Dashboard must render inside the normal plugin configuration page.
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    await page.goto(s.base + '/web/'); await page.locator('#txtManualName').fill('tube-admin'); await page.locator('#txtManualPassword').fill(s.password); await page.locator('#txtManualPassword').press('Enter');
    await page.locator('#jt-launch').waitFor(); await page.goto(s.base + '/web/#/configurationpage?name=JellyTube');
    await page.locator('#jt-admin-host #jt-app.jt-embedded').waitFor(); await page.getByRole('heading', { name: 'JellyTube setup' }).waitFor();
    await page.screenshot({ path: path.join(live, 'desktop-admin.png') }); checks.push({ check: 'administrator dashboard embedded in Jellyfin', passed: true });
  } finally {
    fs.writeFileSync(path.join(live, 'browser-report.json'), JSON.stringify({ checks }, null, 2)); await browser.close();
  }
  console.log(JSON.stringify(checks, null, 2));
})().catch(e => { console.error(e); process.exitCode = 1; });
async function expectButton(button, text) { await button.page().waitForFunction(({ selector, text }) => [...document.querySelectorAll(selector)].some(b => b.textContent === text), { selector: '.jt-card button', text }); }
