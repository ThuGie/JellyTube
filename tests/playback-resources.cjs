/* Real provider and disconnect checks against the isolated server only. */
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const live = path.join(__dirname, '../artifacts/live');
const s = JSON.parse(fs.readFileSync(path.join(live, 'session.json'), 'utf8'));
assert.match(s.base, /^http:\/\/127\.0\.0\.1:/);
const headers = token => ({ 'X-Emby-Token': token, 'Content-Type': 'application/json' });
async function api(route, method = 'GET', signal, admin = false) {
  return fetch(s.base + route, { method, headers: headers(admin ? s.token : s['tube-alice'].token), signal: signal ? AbortSignal.any([signal, AbortSignal.timeout(20000)]) : AbortSignal.timeout(20000) });
}
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));
async function resources() {
  const response = await api('/JellyTube/Admin/PlaybackResources', 'GET', undefined, true);
  assert.equal(response.status, 200); return response.json();
}
async function idle() {
  const deadline = Date.now() + 3000;
  while (Date.now() < deadline) {
    const value = await resources();
    if (!value.resolving && !value.interestedLookups && !value.mediaRequests) return value;
    await pause(25);
  }
  assert.fail('Playback resources were not released after disconnect');
}
(async () => {
  assert.equal((await (await api('/System/Info/Public')).json()).Id, s.serverId);
  const checks = [];
  // Prime metadata and discard the format cache. Observe active extraction when the provider is slow enough.
  const id = 'V12x80R_t_Y';
  const primer = await api(`/JellyTube/Videos/${id}/Start`, 'POST');
  assert.equal(primer.status, 200);
  const initial = await primer.json();
  assert.ok(!initial.source.path.includes('local.mp4'), 'Disconnect test requires a streaming source');
  console.log('Primed metadata for disconnect test');
  assert.equal((await api(`/JellyTube/Admin/PlaybackCache/${id}`, 'DELETE', undefined, true)).status, 204);
  console.log('Cleared format cache');
  const controller = new AbortController();
  const pending = api(`/JellyTube/Videos/${id}/Start`, 'POST', controller.signal).then(async response => { console.log('Preparation completed', response.status); await response.arrayBuffer(); return response.status; }).catch(error => { console.log('Preparation stopped', error.name); return error; });
  let observed = false;
  for (let i = 0; i < 3; i++) {
    if (i === 0) console.log('Waiting for active extraction');
    const active = await resources();
    if (active.resolving > 0) { observed = true; break; }
    await pause(5);
  }
  console.log('Disconnecting preparation request');
  controller.abort(); await pending;
  await idle(); checks.push({ check: 'No playback resources remain after disconnect', observedActiveExtraction: observed, passed: true });
  console.log(JSON.stringify(checks.at(-1)));
  const corpus = JSON.parse(fs.readFileSync(process.env.JELLYTUBE_TOPIC_CORPUS || path.join(__dirname, 'topic-corpus.json'), 'utf8'));
  for (const entry of corpus) {
    const started = performance.now();
    const response = await api(`/JellyTube/Videos/${entry.id}/Start`, 'POST', AbortSignal.timeout(18000));
    const data = await response.json();
    const ms = Math.round(performance.now() - started);
    if (!response.ok) {
      assert.ok([408, 502].includes(response.status));
      assert.ok(data.reference && data.code && typeof data.retryable === 'boolean');
      checks.push({ ...entry, status: response.status, code: data.code, ms });
    } else {
      assert.ok(data.directPlayback && data.source?.path);
      const media = await fetch(s.base + '/' + data.source.path, { headers: { ...headers(s['tube-alice'].token), Range: 'bytes=0-1023' }, signal: AbortSignal.timeout(10000) });
      assert.ok(media.ok, `Selected ${entry.id} media returned ${media.status}`);
      const reader = media.body.getReader(); assert.ok((await reader.read()).value?.length); await reader.cancel();
      checks.push({ ...entry, status: 200, mode: data.source.mode, height: data.source.height, ms });
    }
    await idle();
    console.log(JSON.stringify(checks.at(-1)));
  }
  assert.ok(checks.filter(value => value.status === 200).length >= corpus.length / 2, 'Most representative sources must actually resolve');
  const healthy = await api(`/JellyTube/Videos/${id}/Start`, 'POST'); assert.equal(healthy.status, 200);
  await idle(); checks.push({ check: 'Healthy playback succeeds after cancellation and varied extraction', passed: true });
  fs.writeFileSync(path.join(live, 'playback-resources.json'), JSON.stringify(checks, null, 2));
  console.log(JSON.stringify(checks.at(-1)));
})().catch(error => { console.error(error.message); process.exitCode = 1; });
