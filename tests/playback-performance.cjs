/* Real-provider measurements on the recorded isolated server only. Never targets production. */
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const live = path.join(__dirname, '../artifacts/live');
const session = JSON.parse(fs.readFileSync(path.join(live, 'session.json'), 'utf8'));
const id = process.env.JELLYTUBE_PERF_VIDEO || 'V12x80R_t_Y';
const runs = Number(process.env.JELLYTUBE_PERF_RUNS || 20);
const results = [];
async function api(route, method = 'GET', body, token = session['tube-alice'].token) {
  const response = await fetch(session.base + route, { method, headers: { 'X-Emby-Token': token, 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body) });
  assert.ok(response.ok, `${method} ${route.split('?')[0]}: ${response.status}`);
  return response.status === 204 ? null : response.json();
}
const percentile = values => [...values].sort((a, b) => a - b)[Math.ceil(values.length * .95) - 1];
(async () => {
  assert.match(session.base, /^http:\/\/127\.0\.0\.1:/);
  const info = await api('/System/Info/Public'); assert.equal(info.Id, session.serverId);
  const settings = await api('/JellyTube/Me/Settings');
  const browser = await chromium.launch({ headless: true });
  try {
    await api('/JellyTube/Me/Settings', 'PUT', { ...settings, maxHeight: 1080 });
    const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
    page.setDefaultTimeout(35000);
    await page.goto(session.base + '/web/');
    await page.locator('#txtManualName').fill('tube-alice');
    await page.locator('#txtManualPassword').fill(session.password);
    await page.locator('#txtManualPassword').press('Enter');
    await page.locator('#jt-launch').click();
    await page.locator('.jt-nav').getByRole('button', { name: 'Search', exact: true }).click();
    await page.getByLabel('Search videos or paste a YouTube URL').fill(id);
    await page.locator('.jt-form').getByRole('button', { name: 'Search', exact: true }).click();
    const play = page.locator('.jt-card').first().getByRole('button', { name: '▶ Play', exact: true });
    await play.waitFor();
    for (let index = 0; index < runs; index++) {
      for (const mode of ['cold-format-cache', 'warm-format-cache']) {
        if (mode === 'cold-format-cache') await api('/JellyTube/Admin/PlaybackCache/' + id, 'DELETE', undefined, session.token);
        const start = performance.now();
        await play.click();
        const dialog = page.locator('.jt-direct-player[open]');
        await dialog.getByText(/Direct playback ·/).waitFor();
        const state = await dialog.locator('video').evaluate(video => ({ height: video.videoHeight, width: video.videoWidth, paused: video.paused }));
        assert.ok(state.width && state.height && !state.paused);
        const firstFrameMs = Math.round(performance.now() - start);
        results.push({ index, mode, firstFrameMs, height: state.height });
        await dialog.getByRole('button', { name: 'Back', exact: true }).click();
        await page.locator('.jt-direct-player').waitFor({ state: 'detached' });
        assert.equal(await page.locator('.jt-direct-player video').count(), 0, 'closed direct player must not retain its video');
        assert.ok(await page.locator('video').evaluateAll(videos => videos.every(video => video.paused || video.ended)), 'closing playback must stop audio');
        console.log(`${index + 1}/${runs} ${mode}: ${firstFrameMs} ms, ${state.height}p`);
      }
    }
    // Exercise the real persistent snapshot endpoint, separate from UI feed fixtures.
    await api('/JellyTube/Me/Feed?kind=following&topic=general');
    const cached = [];
    for (let i = 0; i < 20; i++) {
      const start = performance.now(); await api('/JellyTube/Me/Feed?kind=following&topic=general'); cached.push(performance.now() - start);
    }
    const report = { serverVersion: info.Version, videoId: id, runs, results,
      coldFormatP95Ms: percentile(results.filter(r => r.mode === 'cold-format-cache').map(r => r.firstFrameMs)),
      warmFormatP95Ms: percentile(results.filter(r => r.mode === 'warm-format-cache').map(r => r.firstFrameMs)),
      cachedFeedP95Ms: percentile(cached),
      limitations: ['Local Windows server; physical Synology unverified.', 'Cold runs invalidate format lookup; shared byte-range cache and browser connection pools remain warm.'] };
    fs.writeFileSync(path.join(live, 'playback-performance.json'), JSON.stringify(report, null, 2));
    console.log(JSON.stringify({ coldFormatP95Ms: report.coldFormatP95Ms, warmFormatP95Ms: report.warmFormatP95Ms, cachedFeedP95Ms: report.cachedFeedP95Ms }));
  } finally {
    fs.writeFileSync(path.join(live, 'playback-performance-attempts.json'), JSON.stringify(results, null, 2));
    await api('/JellyTube/Me/Settings', 'PUT', settings);
    await browser.close();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
