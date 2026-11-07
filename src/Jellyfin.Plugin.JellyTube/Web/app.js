/* JellyTube: embedded discovery; native Jellyfin playback. No external downloader or runtime. */
(() => {
  'use strict';
  if (window.JellyTube) return;
  let root, main, status, session, library, tab = 'home', generation = 0, timer, lastFocus, lastUser;
  let playbackReturn, preparing;
  let searchState = { query: '', kind: 'videos', items: [], next: null }, libraryTab = 'favorites';
  const account = () => typeof ApiClient === 'undefined' || !ApiClient.accessToken() ? null : ApiClient.serverId() + ':' + (ApiClient.getCurrentUserId() || '').replace(/-/g, '').toLowerCase();
  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
  const api = async (path, method = 'GET', body, signal, keepalive = false) => {
    const owner = account();
    const headers = {}; ApiClient.setRequestHeaders(headers);
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(ApiClient.getUrl('JellyTube/' + path), { method, headers, signal, keepalive, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
    const data = response.status === 204 ? null : await response.json().catch(() => null);
    if (owner !== account()) throw new Error('The account changed. Reopen JellyTube to continue.');
    if (!response.ok) throw new Error(data?.error || data?.Message || `Request failed (${response.status})`);
    return data;
  };
  const message = (text, error = false) => { if (!status) return; status.className = 'jt-status' + (error ? ' jt-error' : ''); status.textContent = text; };
  const button = (label, action, cls = '') => {
    const b = el('button', cls, label); b.type = 'button'; b.onclick = async event => { event.stopPropagation(); b.disabled = true; try { await action(b); } catch (e) { message(e.message, true); } finally { b.disabled = false; } }; return b;
  };
  let scrollFrame = 0;
  document.addEventListener('scroll', () => {
    if (scrollFrame || !root || root.hidden) return;
    scrollFrame = requestAnimationFrame(() => {
      scrollFrame = 0;
      const more = root?.querySelector('button.jt-auto-more:not([hidden]):not(:disabled)');
      if (!more || root.hidden) return;
      const area = more.getBoundingClientRect();
      if (area.height && area.bottom > 0 && area.top < innerHeight + 250) more.click();
    });
  }, { capture: true, passive: true });
  const field = (label, value, type = 'text') => { const wrap = el('label', '', label), input = el('input'); input.type = type; if (type === 'checkbox') input.checked = value; else input.value = value ?? ''; wrap.append(input); return [wrap, input]; };
  const select = (label, value, values) => { const wrap = el('label', '', label), input = el('select'); for (const [key, name] of values) { const o = el('option', '', name); o.value = key; input.append(o); } input.value = value; wrap.append(input); return [wrap, input]; };
  const heading = (title, subtitle) => { main.append(el('div', 'jt-kicker', 'JELLYTUBE / YOUR VIDEO SPACE'), el('h1', '', title)); if (subtitle) main.append(el('p', '', subtitle)); };
  const empty = text => main.append(el('div', 'jt-empty', text));
  const saved = (kind, id) => (library?.[kind] || []).some(s => s.video.id === id);
  const duration = s => !s ? '' : `${Math.floor(s / 60)}:${String(Math.floor(s % 60)).padStart(2, '0')}`;
  async function refreshLibrary() { library = await api('Me/Library'); }
  async function toggleSave(video, kind, b) {
    const exists = saved(kind, video.id), route = kind === 'favorites' ? 'Favorites' : 'WatchLater';
    await api(`Me/${route}/${video.id}`, exists ? 'DELETE' : 'PUT'); await refreshLibrary();
    b.textContent = kind === 'favorites' ? (exists ? '☆ Favorite' : '★ Favorited') : (exists ? '+ Watch later' : '✓ Watch later');
  }
  let directPlayer, dashBundle;
  function loadDashPlayer() {
    if (window.dashjs?.MediaPlayer) return Promise.resolve(window.dashjs);
    if (!dashBundle) dashBundle = new Promise((resolve, reject) => {
      const script = el('script');
      script.src = ApiClient.getUrl('JellyTube/assets/dash.js') + '?v=' + encodeURIComponent(session.version);
      script.onload = () => window.dashjs?.MediaPlayer ? resolve(window.dashjs) : reject(new Error('DASH playback is unavailable.'));
      script.onerror = () => reject(new Error('DASH playback could not be loaded.'));
      document.head.append(script);
    }).catch(error => { dashBundle = null; throw error; });
    return dashBundle;
  }
  async function play(video, sequence = [video]) {
    if (playbackReturn || preparing || directPlayer) return;
    if (!session.nativeLibrary) throw new Error('An administrator needs to set up the shared JellyTube library first.');
    const scope = account(), version = generation, autoplay = session.settings.autoplay;
    const request = new AbortController();
    preparing = request;
    const started = performance.now();
    const dashReady = window.MediaSource ? loadDashPlayer().catch(() => null) : Promise.resolve(null);
    const notice = playbackNotice('Preparing video…', () => request.abort('cancelled'));
    const timeout = setTimeout(() => request.abort('timeout'), 15000);
    try {
      const result = await api(`Videos/${video.id}/Prepare`, 'POST', undefined, request.signal);
      if (request.signal.aborted || version !== generation || scope !== account()) return;
      if (result.directPlayback) {
        openDirectPlayer(video, sequence, result, scope, version, started, dashReady);
        return;
      }
      await nativePlay([result.itemId], result.positionTicks || 0, false, scope, version, { ...result, started });
    } catch (error) {
      if (version !== generation || scope !== account() || request.signal.reason === 'cancelled') return;
      if (request.signal.aborted) throw new Error('Jellyfin timed out while preparing the video. Try Play again.');
      throw error;
    } finally {
      clearTimeout(timeout);
      notice.remove();
      preparing = null;
    }
    if (autoplay) {
      const index = sequence.findIndex(v => v.id === video.id), seen = new Set([video.id]);
      const following = sequence.slice(index + 1).filter(v => !seen.has(v.id) && seen.add(v.id)).slice(0, 20);
      queueFollowing(following, scope).catch(() => { /* Playback remains usable if the next item cannot be prepared. */ });
    }
  }
  function openDirectPlayer(info, sequence, result, scope, version, started, dashReady) {
    const dialog = el('dialog', 'jt-direct-player');
    dialog.setAttribute('aria-label', info.title);
    const video = el('video');
    video.controls = true;
    video.playsInline = true;
    const statusLine = el('p', 'jt-small', 'Connecting directly to the video…');
    statusLine.setAttribute('role', 'status');
    const request = new AbortController();
    const resume = result.positionTicks / 10000000 || 0;
    let player, played = false, completed = false, savedAt = 0, checkpoint = Promise.resolve();
    const save = (done = false, keepalive = false) => {
      if (!played || (completed && !done) || account() !== scope || !Number.isFinite(video.currentTime)) return checkpoint;
      const positionSeconds = video.currentTime;
      checkpoint = checkpoint.then(() => api(`Videos/${info.id}/Progress`, 'PUT',
        { positionSeconds, completed: done }, undefined, keepalive)).catch(() => {});
      return checkpoint;
    };
    const pageHide = () => { save(false, true); };
    const fail = () => {
      if (dialog.open) statusLine.textContent = 'Direct playback could not continue. Use Play in Jellyfin to try the native player.';
    };
    const closeDirect = () => {
      if (directPlayer !== dialog) return checkpoint;
      const saved = save(false, true);
      request.abort();
      window.removeEventListener('pagehide', pageHide);
      player?.destroy();
      video.removeAttribute('src');
      video.load();
      directPlayer = null;
      dialog.remove();
      return saved;
    };
    const playNative = button('Play in Jellyfin', async () => {
      const position = played ? Math.round(video.currentTime * 10000000) : result.positionTicks || 0;
      dialog.close();
      await closeDirect();
      if (account() === scope && generation === version)
        await nativePlay([result.itemId], position, false, scope, version, { ...result, started: performance.now() });
    });
    dialog.append(el('h2', '', info.title), video, statusLine, button('Back', () => dialog.close(), 'jt-primary'), playNative);
    dialog.addEventListener('close', closeDirect, { once: true });
    video.addEventListener('playing', () => {
      if (!played) console.debug('JellyTube direct playback started', {
        reference: result.reference, firstFrameMs: performance.now() - started, height: video.videoHeight
      });
      played = true;
      statusLine.textContent = video.videoHeight ? `Direct playback · ${video.videoHeight}p` : 'Direct playback';
    });
    video.addEventListener('timeupdate', () => {
      if (!played || Math.abs(video.currentTime - savedAt) < 10) return;
      savedAt = video.currentTime;
      save();
    });
    video.addEventListener('pause', () => { if (!video.ended) save(); });
    video.addEventListener('ended', async () => {
      if (completed) return;
      completed = true;
      await save(true);
      if (directPlayer !== dialog || account() !== scope || !session.settings.autoplay) return;
      const next = sequence[sequence.findIndex(item => item.id === info.id) + 1];
      if (next && next.id !== info.id) { dialog.close(); await closeDirect(); play(next, sequence).catch(error => message(error.message, true)); }
    });
    video.addEventListener('error', fail);
    root.append(dialog);
    directPlayer = dialog;
    dialog.showModal();
    window.addEventListener('pagehide', pageHide);
    api(`Videos/${info.id}/Source`, 'GET', undefined, AbortSignal.any([request.signal, AbortSignal.timeout(70000)])).then(async source => {
      if (!dialog.open || request.signal.aborted || account() !== scope) return;
      const url = ApiClient.getUrl(source.path);
      statusLine.textContent = source.height ? `Buffering ${source.height}p…` : 'Buffering video…';
      if (source.mode === 'mp4') {
        video.addEventListener('loadedmetadata', () => {
          if (resume > 0 && resume < video.duration) video.currentTime = resume;
          video.play().catch(() => { if (dialog.open) statusLine.textContent = 'Press play to start the video.'; });
        }, { once: true });
        video.src = url;
      } else {
        const dash = await dashReady;
        if (!dialog.open || request.signal.aborted) return;
        if (!dash) throw new Error('This browser cannot play DASH directly.');
        player = dash.MediaPlayer().create();
        player.on(dash.MediaPlayer.events.ERROR, fail);
        player.updateSettings({ streaming: { buffer: { initialBufferLevel: 1 } } });
        player.initialize(video, url, true, resume);
      }
    }).catch(error => { if (!request.signal.aborted) fail(error); });
  }
  async function queueFollowing(videos, scope) {
    for (let attempt = 0; attempt < 50 && !document.querySelector('video')?.currentSrc; attempt++) {
      if (account() !== scope) return;
      await new Promise(r => setTimeout(r, 200));
    }
    for (const video of videos) {
      if (account() !== scope) return;
      const player = document.querySelector('video');
      if (!player?.currentSrc || player.ended) return;
      const item = await api(`Videos/${video.id}/Prepare?warm=false`, 'POST');
      if (item?.itemId) await nativePlay([item.itemId], 0, true, scope);
    }
  }
  async function nativePlay(ids, positionTicks = 0, queue = false, expectedScope = account(), expectedGeneration = generation, attempt = null) {
    // Jellyfin's registered item-action element works in the bundled web client without importing private webpack modules.
    const host = el('div'); host.hidden = true;
    // Jellyfin's customized-built-in polyfill expects declarative is= markup, not createElement's native options object.
    host.innerHTML = '<div is="emby-itemscontainer" class="itemsContainer"></div>';
    const container = host.firstElementChild;
    container.fetchData = () => ApiClient.getItems(ApiClient.getCurrentUserId(), { Ids: ids.join(','), Limit: ids.length, Fields: 'MediaSources', EnableTotalRecordCount: false });
    const action = el('button', 'itemAction'); action.type = 'button'; action.dataset.id = ids[0]; action.dataset.serverid = ApiClient.serverId();
    action.dataset.type = action.dataset.mediatype = 'Video'; action.dataset.action = queue ? 'queueallfromhere' : positionTicks > 0 ? 'resume' : 'play'; action.dataset.positionticks = positionTicks;
    container.append(action); host.append(container); document.body.append(host);
    // Allow Jellyfin's element-upgrade observer to attach its native item-action listeners.
    await new Promise(resolve => setTimeout(resolve, 0));
    if (account() !== expectedScope || generation !== expectedGeneration) { host.remove(); return; }
    const existingVideo = document.querySelector('video');
    const existingSource = existingVideo?.currentSrc;
    const event = new MouseEvent('click', { bubbles: true, cancelable: true }); action.dispatchEvent(event); host.remove();
    if (queue) return;
    if (!event.defaultPrevented) throw new Error('Jellyfin’s player is unavailable. Refresh Jellyfin Web and try again.');
    if (playbackReturn) return;
    const pending = {
      page: root, scope: expectedScope, entered: false, attempt,
      scroll: root.scrollTop, focus: document.activeElement, retried: false, existingVideo, existingSource
    };
    playbackReturn = pending;
    pending.notice = playbackNotice('Opening Jellyfin player…', () => {
      leavePlayback(pending);
    });
    pending.interval = setInterval(() => observePlayback(pending), 200);
    updatePlaybackPage();
  }
  function playbackNotice(text, cancel) {
    const notice = el('div', 'jt-playback-notice');
    const label = el('span', '', text);
    label.setAttribute('role', 'status');
    notice.append(label, button('Cancel', cancel));
    document.body.append(notice);
    return notice;
  }
  const isPlayerRoute = () => /^#\/video(?:[/?]|$)/.test(location.hash);
  function activeNativeVideo(pending) {
    return [...document.querySelectorAll('video')].find(video => video.currentSrc && !video.paused && !video.ended
      && (video !== pending.existingVideo || video.currentSrc !== pending.existingSource)
      && video.getClientRects().length > 0);
  }
  function leavePlayback(pending, error) {
    if (pending.entered && isPlayerRoute()) {
      pending.returnError = error;
      pending.notice?.remove();
      clearInterval(pending.interval);
      history.back();
    } else {
      activeNativeVideo(pending)?.pause();
      finishPlayback(pending, error);
    }
  }
  function finishPlayback(pending, error) {
    if (playbackReturn !== pending) return;
    clearInterval(pending.interval);
    pending.notice?.remove();
    playbackReturn = null;
    if (root !== pending.page) return;
    root.hidden = false;
    document.body.classList.add('jt-open');
    root.scrollTop = pending.scroll;
    pending.focus?.focus({ preventScroll: true });
    message(error || '', !!error);
  }
  function observePlayback(pending) {
    if (playbackReturn !== pending) return;
    if (pending.scope !== account() || pending.page !== root) { finishPlayback(pending); return; }
    updatePlaybackPage();
    if (playbackReturn !== pending) return;
    const { attempt } = pending;
    const player = pending.entered ? activeNativeVideo(pending) || document.querySelector('video') : null;
    if (player && player.readyState >= 2 && !player.paused && player.currentTime > 0 && !player.error) {
      const negotiation = performance.getEntriesByType('resource').filter(entry =>
        entry.startTime >= attempt.started && /\/PlaybackInfo(?:[?#]|$)/i.test(entry.name)).at(-1);
      console.debug('JellyTube playback started', {
        reference: attempt.reference, prepareMs: attempt.prepareMs,
        negotiationMs: negotiation?.duration, firstFrameMs: performance.now() - attempt.started
      });
      pending.notice.remove();
      clearInterval(pending.interval);
      return;
    }
    if (player?.error?.code === 2 && !pending.retried) {
      pending.retried = true;
      pending.retryAt = performance.now();
      pending.notice.firstChild.textContent = 'The stream was interrupted. Retrying…';
      nativePlay([attempt.itemId], attempt.positionTicks || 0, false, pending.scope).catch(() => {});
    }
    const elapsed = performance.now() - attempt.started;
    if (elapsed > 180000 || (player?.error && player.error.code !== 2)
        || (player?.error && pending.retried && performance.now() - pending.retryAt > 8000)) {
      const reason = player?.error ? 'Jellyfin reported a video playback error.' : 'Jellyfin did not start playback before the timeout.';
      leavePlayback(pending, `${reason} Reference ${attempt.reference || 'unavailable'}.`);
    } else if (!pending.retried && elapsed > 15000) {
      pending.notice.firstChild.textContent = elapsed > 75000
        ? `Jellyfin is still starting this video. Reference ${attempt.reference || 'unavailable'}.`
        : 'Still waiting for the first video frame…';
    }
  }
  function updatePlaybackPage() {
    const pending = playbackReturn;
    if (!pending || pending.scope !== account() || pending.page !== root) return;
    const playerOpen = isPlayerRoute() || !!activeNativeVideo(pending);
    if (playerOpen && !pending.entered) {
      pending.entered = true;
      root.hidden = true;
      document.body.classList.remove('jt-open');
    } else if (!playerOpen && pending.entered) {
      setTimeout(() => {
        if (!isPlayerRoute() && !activeNativeVideo(pending)) finishPlayback(pending, pending.returnError);
      }, 0);
    }
  }
  const cardDetails = new Map();
  const detailQueue = [];
  let activeDetails = 0;
  const detailObserver = new IntersectionObserver(entries => {
    for (const entry of entries) {
      if (!entry.isIntersecting) continue;
      detailObserver.unobserve(entry.target);
      detailQueue.push(entry.target);
    }
    loadVisibleDetails();
  }, { rootMargin: '100px' });
  function loadVisibleDetails() {
    while (activeDetails < 2 && detailQueue.length) {
      const target = detailQueue.shift();
      if (!target.isConnected || !root?.contains(target) || root.hidden) continue;
      activeDetails++;
      target.loadDetails().catch(() => {}).finally(() => {
        activeDetails--;
        loadVisibleDetails();
      });
    }
  }
  function videoDetails(id) {
    if (cardDetails.has(id)) return cardDetails.get(id);
    const result = api(`Videos/${id}/Details`).catch(error => {
      if (cardDetails.get(id) === result) cardDetails.delete(id);
      throw error;
    });
    if (cardDetails.size >= 120) cardDetails.delete(cardDetails.keys().next().value);
    cardDetails.set(id, result);
    return result;
  }
  function card(video, sequence = [video]) {
    const c = el('article', 'jt-card'), img = el('img', 'jt-image'); img.alt = ''; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer';
    // Images are derived from a validated provider ID, never arbitrary stored HTML/URLs.
    const thumbnailId = encodeURIComponent(video.id);
    img.onerror = () => { img.onerror = null; img.src = `https://i.ytimg.com/vi/${thumbnailId}/hqdefault.jpg`; };
    img.src = ApiClient.getUrl(`JellyTube/Thumbnails/${thumbnailId}.jpg`);
    const body = el('div', 'jt-cardbody'), actions = el('div', 'jt-actions');
    body.append(el('div', 'jt-title', video.title), el('div', 'jt-byline', video.channel));
    actions.append(button('▶ Play', () => play(video, sequence), 'jt-primary'), button(saved('favorites', video.id) ? '★ Favorited' : '☆ Favorite', b => toggleSave(video, 'favorites', b)), button(saved('watchLater', video.id) ? '✓ Watch later' : '+ Watch later', b => toggleSave(video, 'watchLater', b)), button('Playlist', () => addToPlaylist(video)));
    if (session.downloads) actions.append(button('↓ Download', async () => { await api(`Me/Downloads/${video.id}`, 'POST'); message('Download queued. You can follow progress in Downloads.'); }));
    if (video.channelId) actions.append(button('Channel', () => channel(video.channelId, video.channel)));
    const info = el('div', 'jt-card-info');
    function showMetadata(details) {
      const date = details.publishedAt && published(details.publishedAt);
      info.replaceChildren(el('div', 'jt-byline', [date || 'Release date unavailable', duration(details.durationSeconds)].filter(Boolean).join(' · ')));
      if (details.description) info.append(el('p', 'jt-card-description', details.description));
    }
    showMetadata(video);
    body.append(info);
    if (!video.publishedAt || !video.description) {
      info.loadDetails = async () => {
        const details = await videoDetails(video.id);
        if (!info.isConnected) return;
        video.publishedAt ??= details.publishedAt;
        video.durationSeconds ??= details.durationSeconds;
        video.description ??= details.description;
        video.tags ??= details.tags;
        showMetadata(details);
      };
      detailObserver.observe(info);
    }
    actions.append(button('Details', () => showDetails(video, sequence)));
    body.append(actions); c.append(img, body); return c;
  }
  const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });
  const published = value => Number.isFinite(Date.parse(value)) ? dateFormat.format(new Date(value)) : '';
  function showDetails(video, sequence) {
    const dialog = el('dialog', 'jt-details');
    dialog.setAttribute('aria-label', video.title);
    const metadata = el('div');
    const note = el('div', 'jt-small', 'Loading video details…');
    note.setAttribute('role', 'status');
    dialog.append(el('h2', '', video.title), metadata, note,
      button('▶ Play', () => { dialog.close(); return play(video, sequence); }, 'jt-primary'),
      button('Close', () => dialog.close()));
    dialog.addEventListener('close', () => dialog.remove(), { once: true });
    function render(details) {
      metadata.replaceChildren(el('p', '', [
        details.channel, details.publishedAt && published(details.publishedAt), duration(details.durationSeconds)
      ].filter(Boolean).join(' · ')));
      if (details.description) metadata.append(el('p', 'jt-description', details.description));
      if (details.tags?.length) metadata.append(el('p', 'jt-small', details.tags.join(' · ')));
    }
    render(video);
    root.append(dialog);
    dialog.showModal();
    videoDetails(video.id).then(details => {
      if (!dialog.open) return;
      render(details);
      note.textContent = '';
    }).catch(error => {
      if (dialog.open) note.textContent = error.message;
    });
  }
  const videoSort = (value = 'default') => select('Sort loaded videos', value, [
    ['default', 'Original order'], ['newest', 'Newest first'], ['oldest', 'Oldest first'],
    ['title', 'Title A–Z'], ['shortest', 'Shortest first'], ['longest', 'Longest first']
  ]);
  const durationFilter = () => select('Duration', 'all', [
    ['all', 'Any duration'], ['short', 'Under 4 minutes'], ['medium', '4–20 minutes'], ['long', 'Over 20 minutes']
  ]);
  function filteredVideos(videos, filter) {
    if (filter === 'all') return videos;
    return videos.filter(video => {
      const seconds = video.durationSeconds;
      return Number.isFinite(seconds) && (filter === 'short' ? seconds < 240 : filter === 'long' ? seconds > 1200 : seconds >= 240 && seconds <= 1200);
    });
  }
  const isShortVideo = video => /(?:^|\s)#shorts\b/i.test(video.title)
    || video.tags?.some(tag => /^#?shorts$/i.test(tag))
    || (Number.isFinite(video.durationSeconds) && video.durationSeconds > 0 && video.durationSeconds <= 180);
  function formatTabs(current, change) {
    const nav = el('nav', 'jt-feed-tabs jt-format-tabs');
    nav.setAttribute('aria-label', 'Video formats');
    for (const [id, title] of [['videos', 'Full videos'], ['shorts', 'Shorts']]) {
      const control = button(title, () => {
        for (const entry of nav.children) entry.setAttribute('aria-pressed', String(entry === control));
        return change(id);
      });
      control.setAttribute('aria-pressed', String(current === id));
      nav.append(control);
    }
    return nav;
  }
  function sortedVideos(videos, order) {
    const date = video => video.publishedAt ? Date.parse(video.publishedAt) : NaN;
    const length = video => video.durationSeconds ?? NaN;
    const compare = (field, direction) => (a, b) => {
      const first = field(a), second = field(b);
      if (!Number.isFinite(first)) return Number.isFinite(second) ? 1 : 0;
      if (!Number.isFinite(second)) return -1;
      return direction * (first - second);
    };
    const orders = {
      newest: compare(date, -1), oldest: compare(date, 1), title: (a, b) => a.title.localeCompare(b.title),
      shortest: compare(length, 1), longest: compare(length, -1)
    };
    return orders[order] ? [...videos].sort(orders[order]) : videos;
  }
  function grid(videos, host = main, paged = false, controls = true) {
    const g = el('div', 'jt-grid');
    const sort = videoSort(), filter = durationFilter();
    function draw() {
      const ordered = sortedVideos(filteredVideos(videos, filter[1].value), sort[1].value);
      g.replaceChildren(...ordered.map(video => card(video, ordered)));
      if (!ordered.length && videos.length) g.append(el('p', 'jt-small', 'No loaded videos match this duration.'));
    }
    sort[1].onchange = filter[1].onchange = draw;
    if (controls && (videos.length > 1 || paged)) host.append(sort[0], filter[0]);
    host.append(g);
    draw();
    return draw;
  }
  async function addToPlaylist(video) {
    await refreshLibrary();
    const box = el('div', 'jt-settings'); box.setAttribute('role', 'dialog'); box.setAttribute('aria-label', 'Add to playlist');
    box.append(el('h2', '', 'Add to playlist'));
    const choice = select('Playlist', library.playlists[0]?.id || '', library.playlists.map(p => [p.id, p.name])); box.append(choice[0]);
    const name = field('Or create a new playlist', ''); box.append(name[0]);
    box.append(button('Add video', async () => {
      let p = name[1].value.trim() ? await api('Me/Playlists', 'POST', { name: name[1].value.trim() }) : library.playlists.find(p => p.id === choice[1].value);
      if (!p) throw new Error('Choose or name a playlist.');
      if (!p.videos.some(v => v.id === video.id)) { p.videos.push(video); await api('Me/Playlists/' + p.id, 'PUT', p); }
      box.remove(); await refreshLibrary(); message('Added to playlist.');
    }, 'jt-primary'), button('Cancel', () => box.remove()));
    main.prepend(box); name[1].focus();
  }
  async function renderSearch() {
    heading('What do you want to watch?', 'Search YouTube or paste a video link. Play opens Jellyfin’s own player.');
    const form = el('form', 'jt-form'), input = field('Search videos or paste a YouTube URL', searchState.query); input[0].classList.add('jt-search');
    const kind = select('Search for', searchState.kind, [['videos', 'Videos'], ['channels', 'Channels'], ['playlists', 'Playlists']]);
    const results = el('div'), sort = videoSort(), filter = durationFilter();
    const formats = formatTabs(searchState.format || 'videos', value => { searchState.format = value; draw(); });
    const formatHint = el('p', 'jt-small', 'Shorts include videos up to 3 minutes when YouTube does not provide a format label.');
    sort[1].onchange = filter[1].onchange = () => {
      searchState.sort = sort[1].value;
      searchState.filter = filter[1].value;
      draw();
    };
    sort[1].value = searchState.sort || 'default';
    filter[1].value = searchState.filter || 'all';
    input[1].required = true;
    const submit = button('Search', async () => {
      const query = input[1].value.trim();
      if (!query) { input[1].focus(); return; }
      await load({ query, kind: kind[1].value, items: [], next: null, sort: sort[1].value, filter: filter[1].value, format: searchState.format });
    }, 'jt-primary');
    form.append(input[0], kind[0], submit); form.onsubmit = e => { e.preventDefault(); submit.click(); }; main.append(form, formats, formatHint, sort[0], filter[0], results);
    let loading = false;
    async function load(request, more = false) {
      if (loading) return;
      loading = true;
      const version = generation; message('Searching…');
      try {
        const route = request.kind === 'videos' ? 'Search?query=' : 'Discover?kind=' + request.kind + '&query=';
        const data = await api(route + encodeURIComponent(request.query) + (more ? '&page=' + encodeURIComponent(request.next) : ''));
        if (version !== generation) return;
        const seen = new Set(request.items.map(item => item.id));
        const added = data.items.filter(item => !seen.has(item.id) && seen.add(item.id));
        const offset = request.items.length;
        request.items.push(...added);
        searchState = { ...request, next: data.nextPageToken === request.next ? null : data.nextPageToken };
        if (more) draw(added, offset);
        else draw();
        message(searchState.items.length ? (more ? `${added.length} more results loaded.` : '') : 'No results. Try a different search.');
      } catch (error) {
        if (version === generation) throw error;
      } finally {
        loading = false;
      }
    }
    let moreButton;
    function draw(items = searchState.items, offset = 0) {
      filter[0].hidden = sort[0].hidden = searchState.kind !== 'videos' || !searchState.items.length;
      formats.hidden = searchState.kind !== 'videos';
      formatHint.hidden = formats.hidden || searchState.format !== 'shorts';
      const customized = sort[1].value !== 'default' || filter[1].value !== 'all';
      if (searchState.kind === 'videos' && customized) {
        items = sortedVideos(filteredVideos(searchState.items, filter[1].value), sort[1].value);
        offset = 0;
      }
      if (offset === 0) results.replaceChildren();
      else moreButton?.remove();
      if (searchState.kind === 'videos') {
        const shorts = searchState.format === 'shorts';
        items = items.filter(video => !!isShortVideo(video) === shorts);
        const videos = results.querySelector('.jt-grid') || el('div', 'jt-grid');
        videos.querySelector('.jt-format-empty')?.remove();
        const sequence = customized ? items : searchState.items.filter(video => !!isShortVideo(video) === shorts);
        const firstNew = videos.children.length;
        for (const video of items) videos.append(card(video, sequence));
        if (!videos.children.length && searchState.items.length) videos.append(el('p', 'jt-small jt-format-empty', 'No loaded results match this format and duration.'));
        if (offset) offset = firstNew;
        if (!videos.parentNode) results.append(videos);
      } else for (const item of items) {
        const row = el('div', 'jt-row'), detail = el('div'); detail.append(el('strong', '', item.title), el('div', 'jt-small', item.author)); row.append(detail);
        row.append(button(item.kind === 'channels' ? 'Open channel' : 'Browse playlist', async () => { if (item.kind === 'channels') await channel(item.id, item.title); else await providerPlaylist(item.id, item.title); }, 'jt-primary')); results.append(row);
      }
      moreButton = searchState.next ? button('Load more', () => load(searchState, true), 'jt-auto-more') : null;
      if (moreButton) results.append(moreButton);
      if (offset) {
        const firstNew = searchState.kind === 'videos' ? results.querySelector('.jt-grid')?.children[offset] : results.children[offset];
        (firstNew?.querySelector('button') || moreButton || submit).focus({ preventScroll: true });
      }
    }
    draw(); if (!searchState.items.length) input[1].focus();
    if (!session.safeSearchAvailable) main.append(el('p', 'jt-small', 'Managed discovery uses YouTube’s public results. SafeSearch requires the optional official API key; it is not a parental-control guarantee.'));
  }
  async function renderLibrary() {
    heading('Your library', 'Private to your Jellyfin account. Favorites and playlists here are saved in JellyTube.');
    const nav = el('div', 'jt-librarynav');
    for (const [id, name] of [['favorites', 'Favorites'], ['watchLater', 'Watch later'], ['playlists', 'Playlists'], ['channels', 'Channels'], ['history', 'Recent']]) nav.append(button(name, () => { libraryTab = id; return navigate('library'); }));
    main.append(nav);
    if (libraryTab === 'playlists') {
      if (!library.playlists.length) empty('Use Playlist on a video to create your first collection.');
      for (const p of library.playlists) {
        const row = el('div', 'jt-row'); row.append(el('div', '', p.name), button('Rename', async () => { const name = prompt('Playlist name', p.name); if (!name?.trim()) return; await api('Me/Playlists/' + p.id, 'PUT', { ...p, name: name.trim() }); await navigate('library'); }), button('Delete playlist', async () => { if (!confirm('Delete this private playlist? Videos remain available.')) return; await api('Me/Playlists/' + p.id, 'DELETE'); await navigate('library'); })); main.append(row);
        const g = el('div', 'jt-grid');
        p.videos.forEach((v, i) => {
          const c = card(v, p.videos), actions = c.querySelector('.jt-actions');
          if (i > 0) actions.append(button('Move up', async () => { [p.videos[i - 1], p.videos[i]] = [p.videos[i], p.videos[i - 1]]; await api('Me/Playlists/' + p.id, 'PUT', p); await navigate('library'); }));
          actions.append(button('Remove', async () => { await api('Me/Playlists/' + p.id, 'PUT', { ...p, videos: p.videos.filter((_, index) => index !== i) }); await navigate('library'); })); g.append(c);
        }); main.append(g);
      }
    } else if (libraryTab === 'channels') {
      if (!library.channels.length) empty('Open Channel on a video to follow its creator.');
      for (const item of library.channels) { const row = el('div', 'jt-row'); row.append(el('div', '', item.channel.name), button('Recent uploads', () => channel(item.channel.id, item.channel.name)), button('Unfollow', async () => { await api('Me/Channels/' + item.channel.id, 'DELETE'); await navigate('library'); })); main.append(row); }
    } else {
      const videos = (library[libraryTab] || []).map(x => x.video); if (videos.length) grid(videos); else empty('Nothing saved here yet. Find a video in Search to get started.');
    }
  }
  function openSubpage() {
    const page = main, parent = status, scroll = root.scrollTop, focus = document.activeElement;
    const nodes = [...page.childNodes];
    generation++;
    clearTimeout(timer);
    page.replaceChildren();
    status = el('div', 'jt-status');
    status.setAttribute('role', 'status');
    page.append(status);
    root.scrollTop = 0;
    return () => {
      if (page !== main) return;
      generation++;
      page.replaceChildren(...nodes);
      status = parent;
      root.scrollTop = scroll;
      focus?.focus({ preventScroll: true });
    };
  }
  async function browseVideos(path) {
    const pageStatus = status;
    message('Loading videos…');
    const first = await api(path);
    if (status !== pageStatus) return;
    message('');
    if (!first.items.length) { empty('No videos are available.'); return; }
    const videos = first.items, seen = new Set(videos.map(video => video.id));
    const draw = grid(videos, main, !!first.nextPageToken);
    let next = first.nextPageToken;
    if (!next) return;
    main.append(button('Load more', async control => {
      const page = await api(path + '?page=' + encodeURIComponent(next));
      if (status !== pageStatus) return;
      videos.push(...page.items.filter(video => !seen.has(video.id) && seen.add(video.id)));
      draw();
      next = page.nextPageToken === next ? null : page.nextPageToken;
      if (!next) { control.remove(); message('All available videos loaded.'); }
    }, 'jt-auto-more'));
  }
  async function channel(id, name) {
    const back = openSubpage();
    heading(name || 'Channel', 'Follow this creator in your private JellyTube library.');
    main.append(button('Back', back));
    let following = library.channels.some(c => c.channel.id === id);
    main.append(button(following ? '✓ Following' : '+ Follow channel', async control => {
      await api('Me/Channels/' + id, following ? 'DELETE' : 'PUT');
      following = !following;
      control.textContent = following ? '✓ Following' : '+ Follow channel';
      await refreshLibrary();
    }, 'jt-primary'));
    await browseVideos('Channels/' + encodeURIComponent(id) + '/Uploads');
  }
  async function providerPlaylist(id, name) {
    const back = openSubpage();
    heading(name, 'Browse this public playlist or save videos to your private library.');
    main.append(button('Back', back));
    await browseVideos('Playlists/' + encodeURIComponent(id) + '/Videos');
  }
  const exploreTopics = [
    ['technology', 'Technology'], ['science', 'Science & nature'], ['music', 'Music'],
    ['gaming', 'Gaming'], ['food', 'Food'], ['travel', 'Travel'], ['sports', 'Sports']
  ];
  const generalTopics = exploreTopics.filter(([id]) => id !== 'music');
  const dailyTopic = (offset = 0) => generalTopics[(Math.floor(Date.now() / 86400000) + offset) % generalTopics.length][0];
  let homeFeed = 'for-you', homeFormat = 'videos', exploreTopic;
  const homeSort = new Map();
  async function renderHome() {
    heading('YouTube');
    if (!session.nativeLibrary) message('Ask an administrator to set up the shared JellyTube library for playback.');
    const nav = el('nav', 'jt-feed-tabs');
    nav.setAttribute('aria-label', 'Home feeds');
    const feed = el('div');
    const hint = el('p', 'jt-small', 'Shorts include videos up to 3 minutes when YouTube does not provide a format label.');
    const formats = formatTabs(homeFormat, value => {
      homeFormat = value;
      return load(homeFeed);
    });
    const topics = el('nav', 'jt-feed-tabs');
    topics.setAttribute('aria-label', 'Explore topics');
    for (const [id, title] of exploreTopics) {
      const control = button(title, () => { exploreTopic = id; return load('explore'); });
      control.dataset.topic = id;
      topics.append(control);
    }
    const sort = videoSort();
    sort[1].onchange = () => {
      homeSort.set(homeFeed, sort[1].value);
      reorderFeed();
    };
    let reorderFeed = () => {};
    main.append(nav, topics, formats, hint, sort[0], feed);
    const pageStatus = status;
    const pages = new Map();
    function cachedPage(path) {
      if (!pages.has(path)) pages.set(path, api(path, 'GET', undefined, AbortSignal.timeout(16000))
        .catch(error => { pages.delete(path); throw error; }));
      return pages.get(path);
    }
    const history = session.settings.recordHistory ? library.history : [];
    const resume = history.filter(entry => entry.positionSeconds > 5
      && (!entry.video.durationSeconds || entry.positionSeconds < entry.video.durationSeconds - 10));
    const watched = new Set(history.map(entry => entry.video.id));
    const followed = library.channels.slice(0, 12).map(entry => entry.channel.id);
    const watchedChannels = [...new Set(history.map(entry => entry.video.channelId).filter(Boolean))].slice(0, 4);
    const channelPath = id => 'Channels/' + encodeURIComponent(id) + '/Uploads';
    const explorePath = topic => 'Explore?topic=' + encodeURIComponent(topic);
    let request = 0;
    async function load(kind) {
      homeFeed = kind;
      const revision = ++request;
      const isCurrent = () => status === pageStatus && request === revision;
      const topic = exploreTopic || dailyTopic();
      for (const control of nav.children) control.setAttribute('aria-pressed', String(control.dataset.feed === kind));
      for (const control of topics.children) control.setAttribute('aria-pressed', String(control.dataset.topic === topic));
      const saved = ['continue', 'later', 'favorites'].includes(kind);
      sort[1].value = homeSort.get(kind) || (['continue', 'for-you', 'general'].includes(kind) ? 'default' : 'newest');
      sort[0].hidden = false;
      reorderFeed = () => {};
      topics.hidden = kind !== 'explore';
      formats.hidden = saved;
      hint.hidden = saved || homeFormat !== 'shorts';
      if (saved) {
        const videos = kind === 'continue' ? resume : library[kind === 'later' ? 'watchLater' : 'favorites'];
        feed.replaceChildren();
        if (videos.length) {
          const entries = videos.map(entry => entry.video);
          reorderFeed = () => {
            feed.replaceChildren();
            grid(sortedVideos(entries, sort[1].value), feed, false, false);
          };
          reorderFeed();
        }
        else feed.append(el('p', 'jt-empty', kind === 'continue'
          ? 'Videos you pause partway through appear here when history is enabled.'
          : 'Save a video to see it here.'));
        return;
      }
      const loading = el('p', 'jt-small', 'Loading videos…');
      loading.setAttribute('role', 'status');
      feed.replaceChildren(loading);
      const discovery = [dailyTopic(), dailyTopic(2), dailyTopic(4)].map(explorePath);
      const paths = kind === 'general' ? discovery : kind === 'explore' ? [explorePath(topic)] : kind === 'following'
        ? followed.map(channelPath)
        : ['Me/Recommendations/Topics', ...discovery,
          ...[...new Set([...followed, ...watchedChannels])].map(channelPath)];
      const ready = new Map();
      let mixedReady, usableCount = 0;
      const usable = new Promise(resolve => { mixedReady = resolve; });
      let waiting = true;
      const all = Promise.allSettled(paths.map(async path => {
        const page = await cachedPage(path);
        ready.set(path, { path, items: page.items, next: page.nextPageToken });
        if (page.items.some(video => !!isShortVideo(video) === (homeFormat === 'shorts')) && ++usableCount >= 2) mixedReady();
      })).then(() => { waiting = false; });
      if (kind === 'for-you' || kind === 'general')
        await Promise.race([all, usable.then(() => new Promise(resolve => setTimeout(resolve, 650)))]);
      else await all;
      if (!isCurrent()) return;
      const sources = paths.filter(path => ready.has(path)).map(path => ready.get(path));
      const videos = [], feedOrder = [], seen = new Set(kind === 'for-you' ? watched : []);
      const deferred = [];
      const cards = el('div', 'jt-grid');
      const nodes = new Map();
      const reorder = () => {
        if (!isCurrent()) return;
        const ordered = sortedVideos(feedOrder, sort[1].value);
        const current = [...cards.children];
        if (ordered.every((video, index) => current[index] === nodes.get(video.id))) return;
        const anchor = current.find(node => node.getBoundingClientRect().bottom > 0);
        const top = anchor?.getBoundingClientRect().top;
        const focused = document.activeElement;
        ordered.forEach(video => cards.append(nodes.get(video.id)));
        if (anchor && top !== undefined) root.scrollTop += anchor.getBoundingClientRect().top - top;
        if (cards.contains(focused)) focused.focus({ preventScroll: true });
        videos.splice(0, videos.length, ...ordered);
      };
      reorderFeed = reorder;
      const more = button('Load more', async () => {
        if (waiting) await all;
        if (!isCurrent()) return;
        const existing = new Set(sources.map(source => source.path));
        sources.push(...paths.filter(path => ready.has(path) && !existing.has(path)).map(path => ready.get(path)));
        if (candidates().length < 24) {
          const pageable = sources.filter(source => source.next).slice(0, 4);
          const nextPages = await Promise.allSettled(pageable.map(source =>
            cachedPage(source.path + (source.path.includes('?') ? '&' : '?') + 'page=' + encodeURIComponent(source.next))));
          if (!isCurrent()) return;
          nextPages.forEach((result, index) => {
            if (result.status !== 'fulfilled') return;
            const source = pageable[index];
            source.items = [...source.items, ...result.value.items];
            source.next = result.value.nextPageToken === source.next ? null : result.value.nextPageToken;
          });
          if (nextPages.length && nextPages.every(result => result.status === 'rejected') && !candidates().length)
            throw new Error('More videos are temporarily unavailable. Try again.');
        }
        append();
      }, 'jt-auto-more');
      function candidates() {
        const output = [], unique = new Set(seen);
        const count = Math.max(0, ...sources.map(source => source.items.length));
        for (let index = 0; index < count; index++) {
          for (const { items } of sources) {
            const video = items[index];
            if (video && !!isShortVideo(video) === (homeFormat === 'shorts') && !unique.has(video.id)) {
              unique.add(video.id);
              output.push(video);
            }
          }
        }
        return output;
      }
      function append() {
        const creators = new Map();
        const available = sortedVideos(candidates(), sort[1].value);
        deferred.length = 0;
        let added = 0;
        for (const video of available) {
          const creator = video.channelId || video.channel;
          const count = creators.get(creator) || 0;
          if (kind === 'for-you' && count >= 3) { deferred.push(video); continue; }
          creators.set(creator, count + 1);
          add(video);
          if (++added === 24) break;
        }
        if (added < 12) for (const video of deferred.slice(0, 12 - added)) add(video);
        more.hidden = !waiting && sources.length === ready.size && !candidates().length && !sources.some(source => source.next);
        if (videos.length) feed.querySelector('.jt-empty')?.remove();
      }
      function add(video) {
        seen.add(video.id);
        videos.push(video);
        feedOrder.push(video);
        const node = card(video, videos);
        nodes.set(video.id, node);
        cards.append(node);
      }
      feed.replaceChildren(cards, more);
      append();
      if (!videos.length) {
        feed.prepend(el('p', 'jt-empty', paths.length && !sources.length
          ? 'This feed is temporarily unavailable.'
          : kind === 'following' && !followed.length ? 'Follow a channel to see uploads here.'
          : kind === 'explore' ? 'No relevant videos match this topic and format on this page.' : 'No videos match this format yet.'));
        if (kind === 'following' && !followed.length) feed.append(button('Find channels', () => {
          searchState = { query: '', kind: 'channels', items: [], next: null };
          return navigate('search');
        }, 'jt-primary'));
        else if (!sources.length) feed.append(button('Retry feed', () => load(kind)));
      }
    }
    for (const [id, title] of [
      ['for-you', 'For you'], ['general', 'General'], ['following', 'Following'], ['explore', 'Explore'],
      ['continue', 'Continue watching'], ['later', 'Watch later'], ['favorites', 'Favorites']
    ]) {
      const control = button(title, () => load(id));
      control.dataset.feed = id;
      nav.append(control);
    }
    await load(homeFeed);
  }
  async function renderDownloads() {
    heading('Downloads', 'Managed downloads run inside Jellyfin. Jellyfin’s own FFmpeg merges audio and video without re-encoding.');
    const host = el('div'); main.append(host); const version = generation;
    async function update() {
      const jobs = await api('Me/Downloads'); if (version !== generation) return; host.replaceChildren();
      if (!jobs.length) host.append(el('div', 'jt-empty', 'No downloads yet. Choose Download on a video.'));
      for (const j of jobs) {
        const row = el('div', 'jt-row'), detail = el('div'); detail.append(el('strong', '', j.video.title), el('div', 'jt-small', j.status + (j.stage ? ' · ' + j.stage : '') + (j.status === 'Downloading' ? ' · ' + Math.round(j.progress * 100) + '%' : '') + (j.error ? ' · ' + j.error : ''))); row.append(detail);
        if (['Queued', 'Downloading'].includes(j.status)) row.append(button('Cancel', async () => { await api('Me/Downloads/' + j.id, 'DELETE'); await update(); }));
        if (['Failed', 'Cancelled'].includes(j.status)) row.append(button('Retry', async () => { await api('Me/Downloads/' + j.id, 'POST'); await update(); }));
        if (j.status === 'Ready') row.append(button('Play', () => play(j.video), 'jt-primary'), button('Remove local copy', async () => { if (!confirm('Remove the retained local copy? This video remains in your library and will stream from YouTube.')) return; await api('Me/Retained/' + j.id, 'DELETE'); await update(); }));
        host.append(row);
      }
      if (jobs.some(j => ['Queued', 'Downloading'].includes(j.status))) timer = setTimeout(() => update().catch(e => message(e.message, true)), document.hidden ? 15000 : 3000);
    }
    await update();
  }
  async function renderSettings() {
    heading('Make it yours.', 'Preferences follow your Jellyfin account across browsers.');
    const form = el('form', 'jt-settings'), s = session.settings;
    const height = select('Maximum download resolution', s.maxHeight, [360,480,720,1080,1440,2160].map(v => [v, v + 'p']));
    const view = select('Library layout', s.view, [['grid', 'Grid'], ['list', 'List']]);
    const lang = field('Search language (official API)', s.language), region = field('Search region (official API)', s.region);
    const safe = select('SafeSearch (official API discovery)', session.minimumSafeSearch === 'strict' ? 'strict' : s.safeSearch, session.minimumSafeSearch === 'strict' ? [['strict', 'Strict (administrator policy)']] : [['strict', 'Strict'], ['moderate', 'Moderate']]); safe[1].disabled = !session.safeSearchAvailable;
    const autoplay = field('Queue following videos in this list (web player)', s.autoplay, 'checkbox'), record = field('Show recent viewing in JellyTube', s.recordHistory, 'checkbox');
    for (const f of [height, view, lang, region, safe, autoplay, record]) form.append(f[0]);
    form.append(el('p', 'jt-small', 'Jellyfin controls native playback history and resume. Hiding JellyTube recent viewing does not disable Jellyfin’s own user data.'));
    form.append(button('Save preferences', async () => {
      session.settings = await api('Me/Settings', 'PUT', { maxHeight: Number(height[1].value), view: view[1].value, language: lang[1].value, region: region[1].value.toUpperCase(), safeSearch: safe[1].value, autoplay: autoplay[1].checked, recordHistory: record[1].checked });
      message('Preferences saved.');
    }, 'jt-primary'));
    form.onsubmit = e => e.preventDefault(); main.append(form, el('h2', '', 'Your data'));
    main.append(button('Export my JellyTube data', async () => { const data = await api('Me/Export'); const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' })); const a = el('a'); a.href = url; a.download = 'jellytube-data.json'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); }), button('Clear recent viewing', async () => { await api('Me/History', 'DELETE'); await refreshLibrary(); message('JellyTube recent viewing cleared.'); }));
  }
  async function renderAdmin() {
    heading('JellyTube setup', 'A standalone plugin. No yt-dlp, Python, Node, Deno, container, or companion service.');
    const [c, health] = await Promise.all([api('Admin/Settings'), api('Admin/Health')]);
    main.append(el('p', 'jt-small', `${health.extractor} · Jellyfin FFmpeg: ${health.ffmpegAvailable ? 'ready' : 'unavailable'} · ${(health.storageBytes / 1073741824).toFixed(2)} GiB stored · Web navigation: ${health.navigation ? 'ready' : 'File Transformation unavailable'}`));
    main.append(el('p', 'jt-small', `Shared library: ${health.sharedLibraryAvailable ? 'ready' : 'needs setup'} · Shared storage: ${(health.sharedBytes / 1073741824).toFixed(2)} GiB · Media URL: ${health.mediaUrlConfigured ? 'configured' : 'missing or invalid'}`));
    if (health.mediaUrlConfigured) main.append(button('Check saved media URL', async () => {
      const result = await fetch(c.publicBaseUrl.replace(/\/$/, '') + '/System/Info/Public', {
        credentials: 'omit', signal: AbortSignal.timeout(8000)
      });
      if (!result.ok || (await result.json()).Id !== ApiClient.serverId())
        throw new Error('The saved media URL did not identify this Jellyfin server.');
      message('The saved media URL reaches this Jellyfin server from your browser.');
    }));
    const form = el('form', 'jt-settings');
    const base = field('Jellyfin URL reachable by every client (include base path)', c.publicBaseUrl || location.origin + location.pathname.replace(/\/web\/.*/, ''));
    const media = field('Dedicated server folder outside existing shared libraries', c.mediaRoot), budget = field('Storage budget in MiB', c.storageBudgetMb, 'number');
    const downloads = field('Allow retained video downloads', c.enableDownloads, 'checkbox'), searches = field('Daily server search budget', c.searchCallsPerDay, 'number'), userLimit = field('Daily search budget per user', c.userSearchCallsPerDay, 'number');
    const apiKey = field(c.apiKeyPresent ? 'Replace optional YouTube API key (leave blank to keep)' : 'Optional YouTube Data API key', '', 'password');
    const minimumSafe = select('Minimum SafeSearch (official video discovery only)', c.minimumSafeSearch, [['moderate', 'Moderate'], ['strict', 'Strict']]);
    for (const f of [base, media, budget, downloads, searches, userLimit, apiKey, minimumSafe]) form.append(f[0]);
    form.append(button('Save setup', async () => { await api('Admin/Settings', 'PUT', { publicBaseUrl: base[1].value, mediaRoot: media[1].value, storageBudgetMb: Number(budget[1].value), enableDownloads: downloads[1].checked, searchCallsPerDay: Number(searches[1].value), userSearchCallsPerDay: Number(userLimit[1].value), deniedUsers: c.deniedUsers, downloadDeniedUsers: c.downloadDeniedUsers, minimumSafeSearch: minimumSafe[1].value, apiKey: apiKey[1].value || null }); message('Setup saved.'); }, 'jt-primary'));
    form.onsubmit = e => e.preventDefault(); main.append(form, el('h2', '', 'Shared JellyTube library'));
    main.append(el('p', '', 'Everyone with library access can browse its videos. Favorites, history, playlists and download ownership stay with each account.'));
    main.append(button(
      c.legacyLibraryCount ? 'Set up shared library and migrate' : c.sharedLibrary ? 'Refresh shared library access' : 'Create shared library',
      async () => {
        await api('Admin/Library', 'POST');
        await navigate('admin');
      }, 'jt-primary'));
    main.append(el('h2', '', 'Account access'));
    for (const u of c.users) {
      const row = el('div', 'jt-row'); row.append(el('div', '', u.name));
      const denied = c.deniedUsers.includes(u.id);
      if (!u.admin) row.append(button(denied ? 'Enable access' : 'Disable access', async () => { const next = denied ? c.deniedUsers.filter(id => id !== u.id) : [...c.deniedUsers, u.id]; await api('Admin/Settings', 'PUT', { ...c, deniedUsers: next }); await navigate('admin'); }));
      else row.append(el('span', 'jt-small', 'Administrator'));
      const downloadBlocked = c.downloadDeniedUsers.includes(u.id);
      row.append(button(downloadBlocked ? 'Allow downloads' : 'Disable downloads', async () => { const next = downloadBlocked ? c.downloadDeniedUsers.filter(id => id !== u.id) : [...c.downloadDeniedUsers, u.id]; await api('Admin/Settings', 'PUT', { ...c, downloadDeniedUsers: next }); await navigate('admin'); }));
      main.append(row);
    }
  }
  async function navigate(next, push = true) {
    if (!root) return;
    preparing?.abort('cancelled');
    if (playbackReturn && !playbackReturn.entered) finishPlayback(playbackReturn);
    if (next === 'home' && push) homeFeed = 'for-you';
    clearTimeout(timer); generation++; tab = next; main.replaceChildren();
    const version = generation;
    status = el('div', 'jt-status'); status.setAttribute('role', 'status'); main.append(status);
    root.classList.toggle('jt-list', session?.settings.view === 'list');
    for (const b of root.querySelectorAll('[data-tab]')) { if (b.dataset.tab === next) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); }
    if (push && !root.classList.contains('jt-embedded')) history.replaceState({ ...(history.state || {}), jellyTube: next }, '', location.href);
    try {
      if (next === 'search') {
        await renderSearch();
        return;
      }
      if (next !== 'admin') {
        message('Loading JellyTube…');
        const currentLibrary = await api('Me/Library');
        if (version !== generation) return;
        library = currentLibrary;
        message('');
      }
      if (next === 'library') await renderLibrary();
      else if (next === 'downloads') await renderDownloads();
      else if (next === 'settings') await renderSettings();
      else if (next === 'admin') await renderAdmin();
      else await renderHome();
    } catch (error) {
      if (version === generation) message(error.message, true);
    }
  }
  function close(back = true) { generation++; clearTimeout(timer); preparing?.abort("cancelled"); directPlayer?.close(); if (playbackReturn) finishPlayback(playbackReturn); root?.remove(); root = main = status = null; document.body.classList.remove('jt-open'); if (back && history.state?.jellyTube) history.back(); lastFocus?.focus(); }
  async function open(next = 'home', host) {
    if (next === 'home') homeFeed = 'for-you';
    if (typeof ApiClient === 'undefined' || !ApiClient.accessToken()) return;
    if (root) close(false); lastFocus = document.activeElement;
    session = await api('Session'); root = el('section', host ? 'jt-embedded' : ''); root.id = 'jt-app'; root.setAttribute('aria-label', 'JellyTube'); document.body.classList.add('jt-open');
    const header = el('header', 'jt-header'), brand = el('div', 'jt-brand', 'Jelly'); brand.append(el('span', '', 'Tube')); const nav = el('nav', 'jt-nav'); nav.setAttribute('aria-label', 'YouTube navigation');
    if (!host) for (const [id, name] of [['home', 'Home'], ['search', 'Search'], ['library', 'Library'], ['downloads', 'Downloads'], ['settings', 'Settings']]) { const b = button(name, () => navigate(id)); b.dataset.tab = id; nav.append(b); }
    if (session.admin && !host) nav.append(button('Admin', () => { close(false); location.hash = '#/configurationpage?name=JellyTube'; }));
    header.append(brand, nav); if (!host) header.append(button('Back to Jellyfin', () => close())); main = el('main', 'jt-main'); root.append(header, main); (host || document.body).append(root);
    if (!host) history.pushState({ ...(history.state || {}), jellyTube: next }, '', location.href);
    await navigate(next, false);
  }
  window.JellyTube = { open, close };
  window.addEventListener('popstate', () => {
    if (playbackReturn) { updatePlaybackPage(); return; }
    if (root && !root.classList.contains('jt-embedded')) close(false);
  });
  window.addEventListener('hashchange', updatePlaybackPage);
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && root && !root.hidden && !root.querySelector('dialog[open]') && !root.classList.contains('jt-embedded')) close(); });
  function mount() {
    const scope = account();
    if (lastUser && scope !== lastUser) { close(false); library = null; cardDetails.clear(); detailQueue.length = 0; homeFeed = 'for-you'; homeFormat = 'videos'; homeSort.clear(); exploreTopic = undefined; searchState = { query: '', kind: 'videos', items: [], next: null }; document.getElementById('jt-launch')?.remove(); }
    lastUser = scope;
    updatePlaybackPage();
    if (document.getElementById('jt-launch') || typeof ApiClient === 'undefined' || !ApiClient.accessToken()) return;
    const target = document.querySelector('.headerRight'); if (!target) return;
    const launch = button('▶ YouTube', () => open()); launch.id = 'jt-launch'; launch.setAttribute('aria-label', 'Open JellyTube'); target.prepend(launch);
  }
  new MutationObserver(mount).observe(document.documentElement, { childList: true, subtree: true }); mount();
})();
