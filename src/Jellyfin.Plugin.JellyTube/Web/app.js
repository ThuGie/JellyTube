/* JellyTube: embedded discovery; native Jellyfin playback. No external downloader or runtime. */
(() => {
  'use strict';
  if (window.JellyTube) return;
  let root, main, status, session, library, tab = 'home', generation = 0, timer, lastFocus, lastUser;
  let playbackReturn;
  let searchState = { query: '', kind: 'videos', items: [], next: null }, libraryTab = 'favorites';
  const account = () => typeof ApiClient === 'undefined' || !ApiClient.accessToken() ? null : ApiClient.serverId() + ':' + (ApiClient.getCurrentUserId() || '').replace(/-/g, '').toLowerCase();
  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
  const api = async (path, method = 'GET', body) => {
    const owner = account();
    const headers = {}; ApiClient.setRequestHeaders(headers);
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    const response = await fetch(ApiClient.getUrl('JellyTube/' + path), { method, headers, credentials: 'same-origin', body: body === undefined ? undefined : JSON.stringify(body) });
    const data = response.status === 204 ? null : await response.json().catch(() => null);
    if (owner !== account()) throw new Error('The account changed. Reopen JellyTube to continue.');
    if (!response.ok) throw new Error(data?.error || data?.Message || `Request failed (${response.status})`);
    return data;
  };
  const message = (text, error = false) => { if (!status) return; status.className = 'jt-status' + (error ? ' jt-error' : ''); status.textContent = text; };
  const button = (label, action, cls = '') => {
    const b = el('button', cls, label); b.type = 'button'; b.onclick = async () => { b.disabled = true; try { await action(b); } catch (e) { message(e.message, true); } finally { b.disabled = false; } }; return b;
  };
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
  async function play(video, sequence = [video]) {
    if (playbackReturn) return;
    if (!session.nativeLibrary) throw new Error('An administrator needs to create your private YouTube library first.');
    const scope = account(), version = generation, autoplay = session.settings.autoplay;
    message('Opening Jellyfin player…');
    const result = await api(`Videos/${video.id}/Prepare`, 'POST');
    if (version !== generation) return;
    const itemId = result.itemId;
    const nativeItem = await ApiClient.getItem(ApiClient.getCurrentUserId(), itemId);
    if (version !== generation) return;
    if (scope !== account()) return;
    await nativePlay([itemId], nativeItem.UserData?.PlaybackPositionTicks || 0, false, scope);
    if (autoplay) {
      const index = sequence.findIndex(v => v.id === video.id), seen = new Set([video.id]);
      const following = sequence.slice(index + 1).filter(v => !seen.has(v.id) && seen.add(v.id)).slice(0, 20);
      queueFollowing(following, scope).catch(() => { /* Playback remains usable if the next item cannot be prepared. */ });
    }
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
      const item = await api(`Videos/${video.id}/Prepare`, 'POST');
      if (item?.itemId) await nativePlay([item.itemId], 0, true, scope);
    }
  }
  async function nativePlay(ids, positionTicks = 0, queue = false, expectedScope = account()) {
    // Jellyfin's registered item-action element works in the bundled web client without importing private webpack modules.
    const host = el('div'); host.hidden = true;
    // Jellyfin's customized-built-in polyfill expects declarative is= markup, not createElement's native options object.
    host.innerHTML = '<div is="emby-itemscontainer" class="itemsContainer"></div>';
    const container = host.firstElementChild;
    container.fetchData = () => ApiClient.getItems(ApiClient.getCurrentUserId(), { Ids: ids.join(','), Limit: ids.length, Fields: 'MediaSources', EnableTotalRecordCount: false });
    const action = el('button', 'itemAction'); action.type = 'button'; action.dataset.id = ids[0]; action.dataset.serverid = ApiClient.serverId();
    action.dataset.type = action.dataset.mediatype = 'Video'; action.dataset.action = queue ? 'queueallfromhere' : positionTicks > 0 ? 'resume' : 'playallfromhere'; action.dataset.positionticks = positionTicks;
    container.append(action); host.append(container); document.body.append(host);
    // Allow Jellyfin's element-upgrade observer to attach its native item-action listeners.
    await new Promise(resolve => setTimeout(resolve, 0));
    if (account() !== expectedScope) { host.remove(); return; }
    const event = new MouseEvent('click', { bubbles: true, cancelable: true }); action.dispatchEvent(event); host.remove();
    if (queue) return;
    if (!event.defaultPrevented) throw new Error('Jellyfin’s player is unavailable. Refresh Jellyfin Web and try again.');
    playbackReturn = { page: root, scope: expectedScope, entered: false };
    playbackReturn.timeout = setTimeout(() => {
      if (!playbackReturn?.entered) {
        playbackReturn = null;
        message('Jellyfin is taking longer than expected to open the player. Check the server log if playback does not start.', true);
      }
    }, 60000);
    updatePlaybackPage();
  }
  function updatePlaybackPage() {
    const pending = playbackReturn;
    if (!pending || pending.scope !== account() || pending.page !== root) return;
    const playing = /^#\/video(?:[/?]|$)/.test(location.hash);
    if (playing && !pending.entered) {
      pending.entered = true;
      clearTimeout(pending.timeout);
      root.hidden = true;
      document.body.classList.remove('jt-open');
    } else if (!playing && pending.entered) {
      // Jellyfin can update the route and then dispatch popstate in the same navigation.
      clearTimeout(pending.timeout);
      pending.timeout = setTimeout(() => {
        if (playbackReturn !== pending || /^#\/video(?:[/?]|$)/.test(location.hash)) return;
        playbackReturn = null;
        root.hidden = false;
        document.body.classList.add('jt-open');
        message('');
      }, 0);
    }
  }
  function card(video, sequence = [video]) {
    const c = el('article', 'jt-card'), img = el('img', 'jt-image'); img.alt = ''; img.loading = 'lazy'; img.referrerPolicy = 'no-referrer';
    // Images are derived from a validated provider ID, never arbitrary stored HTML/URLs.
    img.src = `https://i.ytimg.com/vi/${encodeURIComponent(video.id)}/hqdefault.jpg`;
    const body = el('div', 'jt-cardbody'), actions = el('div', 'jt-actions'); body.append(el('div', 'jt-title', video.title), el('div', 'jt-byline', video.channel + (video.durationSeconds ? ' · ' + duration(video.durationSeconds) : '')));
    actions.append(button('▶ Play', () => play(video, sequence), 'jt-primary'), button(saved('favorites', video.id) ? '★ Favorited' : '☆ Favorite', b => toggleSave(video, 'favorites', b)), button(saved('watchLater', video.id) ? '✓ Watch later' : '+ Watch later', b => toggleSave(video, 'watchLater', b)), button('Playlist', () => addToPlaylist(video)));
    if (session.downloads) actions.append(button('↓ Download', async () => { await api(`Me/Downloads/${video.id}`, 'POST'); message('Download queued. You can follow progress in Downloads.'); }));
    if (video.channelId) actions.append(button('Channel', () => channel(video.channelId, video.channel)));
    body.append(actions); c.append(img, body); return c;
  }
  function grid(videos, host = main) { const g = el('div', 'jt-grid'); for (const v of videos) g.append(card(v, videos)); host.append(g); }
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
    const results = el('div');
    const submit = button('Search', async () => { searchState = { query: input[1].value, kind: kind[1].value, items: [], next: null }; await load(false); }, 'jt-primary');
    form.append(input[0], kind[0], submit); form.onsubmit = e => { e.preventDefault(); submit.click(); }; main.append(form, results);
    async function load(next) {
      const version = generation; message('Searching…');
      const data = await api((searchState.kind === 'videos' ? 'Search?query=' : 'Discover?kind=' + searchState.kind + '&query=') + encodeURIComponent(searchState.query) + (next && searchState.next ? '&page=' + encodeURIComponent(searchState.next) : ''));
      if (version !== generation) return;
      searchState.items = next ? [...searchState.items, ...data.items] : data.items; searchState.next = data.nextPageToken; draw(); message(data.items.length ? '' : 'No results. Try a different search.');
    }
    function draw() {
      results.replaceChildren();
      if (searchState.kind === 'videos') grid(searchState.items, results);
      else for (const item of searchState.items) {
        const row = el('div', 'jt-row'), detail = el('div'); detail.append(el('strong', '', item.title), el('div', 'jt-small', item.author)); row.append(detail);
        row.append(button(item.kind === 'channels' ? 'Open channel' : 'Browse playlist', async () => { if (item.kind === 'channels') await channel(item.id, item.title); else await providerPlaylist(item.id, item.title); }, 'jt-primary')); results.append(row);
      }
      if (searchState.next) results.append(button('Load more', () => load(true)));
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
  async function channel(id, name) {
    generation++; clearTimeout(timer); main.replaceChildren(); status = el('div', 'jt-status'); status.setAttribute('role', 'status'); main.append(status);
    heading(name || 'Channel', 'Follow this creator in your private JellyTube library.');
    main.append(button('Back', () => navigate(tab)));
    const following = library.channels.some(c => c.channel.id === id);
    main.append(button(following ? '✓ Following' : '+ Follow channel', async () => { await api('Me/Channels/' + id, following ? 'DELETE' : 'PUT'); await refreshLibrary(); await channel(id, name); }, 'jt-primary'));
    message('Loading recent uploads…'); const version = generation, data = await api('Channels/' + encodeURIComponent(id) + '/Uploads');
    if (version !== generation) return; message(''); grid(data.items);
  }
  async function providerPlaylist(id, name) {
    generation++; clearTimeout(timer); main.replaceChildren(); status = el('div', 'jt-status'); status.setAttribute('role', 'status'); main.append(status);
    heading(name, 'The first 50 available videos from this public playlist. Save individual videos to your private library.');
    main.append(button('Back to Search', () => navigate('search'))); message('Loading playlist…');
    const version = generation, data = await api('Playlists/' + encodeURIComponent(id) + '/Videos'); if (version !== generation) return; message(''); grid(data.items);
  }
  async function renderHome() {
    heading('Your next great watch.', 'Discover on YouTube. Keep your library personal. Watch through Jellyfin.');
    main.append(button('Find something to watch', () => navigate('search'), 'jt-primary'));
    if (!session.nativeLibrary) message('Native playback needs a private library. An administrator can create one in Dashboard → Plugins → JellyTube.');
    const resume = library.history.filter(h => h.positionSeconds > 5 && (!h.video.durationSeconds || h.positionSeconds < h.video.durationSeconds - 10));
    if (resume.length) { main.append(el('h2', '', 'Continue watching')); grid(resume.slice(0, 8).map(h => h.video)); }
    const uploads = new Map();
    function channelUploads(id) {
      if (!uploads.has(id)) uploads.set(id, api('Channels/' + encodeURIComponent(id) + '/Uploads'));
      return uploads.get(id);
    }
    const version = generation;
    const history = session.settings.recordHistory ? library.history : [];
    const watchedChannels = [...new Set(history.map(entry => entry.video.channelId).filter(Boolean))].slice(0, 2);
    const followedChannels = library.channels.slice(0, watchedChannels.length ? 2 : 4).map(entry => entry.channel.id);
    const channels = [...new Set([...followedChannels, ...watchedChannels])];
    main.append(el('h2', '', 'Recommended'));
    const recommendations = el('div');
    main.append(recommendations);
    if (history.length) {
      const topics = el('div');
      main.append(topics);
      api('Me/Recommendations/Topics').then(data => {
        if (version !== generation || !data.items.length) return;
        topics.append(el('h3', '', 'From topics you watch'));
        grid(data.items, topics);
      }).catch(() => {});
    }
    if (!channels.length) recommendations.append(el('div', 'jt-empty', 'Follow a channel or watch videos with history enabled to get recommendations.'));
    else {
      recommendations.append(el('div', 'jt-small', 'Finding videos from channels you follow and watch…'));
      const seen = new Set(history.map(entry => entry.video.id));
      Promise.allSettled(channels.map(channelUploads)).then(results => {
        if (version !== generation) return;
        const lists = results.filter(result => result.status === 'fulfilled').map(result => result.value.items);
        const picks = [];
        const maxLength = Math.max(0, ...lists.map(items => items.length));
        for (let index = 0; index < maxLength && picks.length < 8; index++) {
          for (const items of lists) {
            const video = items[index];
            if (video && !seen.has(video.id)) {
              seen.add(video.id);
              picks.push(video);
              if (picks.length === 8) break;
            }
          }
        }
        recommendations.replaceChildren();
        if (picks.length) grid(picks, recommendations);
        else recommendations.append(el('div', 'jt-empty', lists.length ? 'No unwatched recommendations right now.' : 'Recommendations are temporarily unavailable.'));
      });
    }
    for (const [name, values] of [['Watch later', library.watchLater], ['Favorites', library.favorites]]) {
      if (!values.length) continue; main.append(el('h2', '', name)); grid(values.slice(0, 8).map(v => v.video));
    }
    if (!library.favorites.length && !library.watchLater.length) { main.append(el('h2', '', 'Start your own collection')); empty('Search for a creator, a topic, or a video. Favorite it or save it for later, and it will appear here.'); }
    for (const item of library.channels.slice(0, 2)) {
      main.append(el('h2', '', item.channel.name + ' · Recent uploads')); const shelf = el('div'); shelf.append(el('div', 'jt-small', 'Loading uploads…')); main.append(shelf);
      channelUploads(item.channel.id).then(data => { if (version !== generation) return; shelf.replaceChildren(); grid(data.items.slice(0, 8), shelf); })
        .catch(() => { if (version === generation) { shelf.replaceChildren(el('div', 'jt-small', 'Uploads are temporarily unavailable. Your saved library is still ready.')); } });
    }
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
    const height = select('Maximum video resolution', s.maxHeight, [360,480,720,1080,1440,2160].map(v => [v, v + 'p']));
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
    const form = el('form', 'jt-settings');
    const base = field('Jellyfin URL reachable by every client (include base path)', c.publicBaseUrl || location.origin + location.pathname.replace(/\/web\/.*/, ''));
    const media = field('Dedicated server folder outside existing shared libraries', c.mediaRoot), budget = field('Storage budget in MiB', c.storageBudgetMb, 'number');
    const downloads = field('Allow retained video downloads', c.enableDownloads, 'checkbox'), searches = field('Daily server search budget', c.searchCallsPerDay, 'number'), userLimit = field('Daily search budget per user', c.userSearchCallsPerDay, 'number');
    const apiKey = field(c.apiKeyPresent ? 'Replace optional YouTube API key (leave blank to keep)' : 'Optional YouTube Data API key', '', 'password');
    const minimumSafe = select('Minimum SafeSearch (official video discovery only)', c.minimumSafeSearch, [['moderate', 'Moderate'], ['strict', 'Strict']]);
    for (const f of [base, media, budget, downloads, searches, userLimit, apiKey, minimumSafe]) form.append(f[0]);
    form.append(button('Save setup', async () => { await api('Admin/Settings', 'PUT', { publicBaseUrl: base[1].value, mediaRoot: media[1].value, storageBudgetMb: Number(budget[1].value), enableDownloads: downloads[1].checked, searchCallsPerDay: Number(searches[1].value), userSearchCallsPerDay: Number(userLimit[1].value), deniedUsers: c.deniedUsers, downloadDeniedUsers: c.downloadDeniedUsers, minimumSafeSearch: minimumSafe[1].value, apiKey: apiKey[1].value || null }); message('Setup saved.'); }, 'jt-primary'));
    form.onsubmit = e => e.preventDefault(); main.append(form, el('h2', '', 'Private native libraries'));
    main.append(el('p', '', 'Create one YouTube library for each account. Other non-admin users keep access to their existing libraries and are switched from “all libraries” to an explicit list so this library stays private. Administrators retain access.'));
    for (const u of c.users) {
      const binding = c.libraries.find(b => b.userId === u.id), row = el('div', 'jt-row'); row.append(el('div', '', u.name));
      row.append(binding ? el('span', 'jt-small', 'Private library ready') : button('Create private library', async () => { await api('Admin/Libraries/' + u.id, 'POST'); await navigate('admin'); }, 'jt-primary'));
      const denied = c.deniedUsers.includes(u.id);
      if (!u.admin) row.append(button(denied ? 'Enable access' : 'Disable access', async () => { const next = denied ? c.deniedUsers.filter(id => id !== u.id) : [...c.deniedUsers, u.id]; await api('Admin/Settings', 'PUT', { ...c, deniedUsers: next }); await navigate('admin'); }));
      else row.append(el('span', 'jt-small', 'Administrator'));
      const downloadBlocked = c.downloadDeniedUsers.includes(u.id);
      row.append(button(downloadBlocked ? 'Allow downloads' : 'Disable downloads', async () => { const next = downloadBlocked ? c.downloadDeniedUsers.filter(id => id !== u.id) : [...c.downloadDeniedUsers, u.id]; await api('Admin/Settings', 'PUT', { ...c, downloadDeniedUsers: next }); await navigate('admin'); }));
      main.append(row);
    }
  }
  async function navigate(next, push = true) {
    if (!root) return; clearTimeout(timer); generation++; tab = next; main.replaceChildren();
    status = el('div', 'jt-status'); status.setAttribute('role', 'status'); main.append(status);
    root.classList.toggle('jt-list', session?.settings.view === 'list');
    for (const b of root.querySelectorAll('[data-tab]')) { if (b.dataset.tab === next) b.setAttribute('aria-current', 'page'); else b.removeAttribute('aria-current'); }
    if (push && !root.classList.contains('jt-embedded')) history.replaceState({ ...(history.state || {}), jellyTube: next }, '', location.href);
    try { if (next !== 'admin') await refreshLibrary(); if (next === 'search') await renderSearch(); else if (next === 'library') await renderLibrary(); else if (next === 'downloads') await renderDownloads(); else if (next === 'settings') await renderSettings(); else if (next === 'admin') await renderAdmin(); else await renderHome(); } catch (e) { message(e.message, true); }
  }
  function close(back = true) { generation++; clearTimeout(timer); clearTimeout(playbackReturn?.timeout); playbackReturn = null; root?.remove(); root = main = status = null; document.body.classList.remove('jt-open'); if (back && history.state?.jellyTube) history.back(); lastFocus?.focus(); }
  async function open(next = 'home', host) {
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
  document.addEventListener('keydown', e => { if (e.key === 'Escape' && root && !root.hidden && !root.classList.contains('jt-embedded')) close(); });
  function mount() {
    const scope = account();
    if (lastUser && scope !== lastUser) { close(false); library = null; searchState = { query: '', kind: 'videos', items: [], next: null }; document.getElementById('jt-launch')?.remove(); }
    lastUser = scope;
    updatePlaybackPage();
    if (document.getElementById('jt-launch') || typeof ApiClient === 'undefined' || !ApiClient.accessToken()) return;
    const target = document.querySelector('.headerRight'); if (!target) return;
    const launch = button('▶ YouTube', () => open()); launch.id = 'jt-launch'; launch.setAttribute('aria-label', 'Open JellyTube'); target.prepend(launch);
  }
  new MutationObserver(mount).observe(document.documentElement, { childList: true, subtree: true }); mount();
})();
