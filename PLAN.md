# JellyTube implementation plan

Status: standalone preview implemented, revised 7 November 2026. Physical NAS/client certification remains pending. Standalone managed plugin; see README.md and docs/validation.md for implemented behavior and verified limits.

## Product and first release

Build a standalone Jellyfin server plugin with native Jellyfin media items and playback, plus an embedded web interface for richer discovery and settings. Add a YouTube entry alongside the existing JellyTome, JellySpot, and JellySeerr integrations. Users sign in with their existing Jellyfin account; a Google account is not required for the initial release.

Initial target: Jellyfin 10.11.x / .NET 9, matching the supplied projects. Pin the exact package baseline during implementation and test against the actual installed server. Do not infer compatibility with other Jellyfin majors.

The first release provides:

- Search videos, channels, and public playlists, with explicit submission, pagination, sorting, duration/date filters where supported, language, region, and SafeSearch.
- Native Jellyfin playback using managed C# extraction, server-mediated streaming, and optional persistent downloads.
- Native-client access to saved videos and channel folders through restricted standard Jellyfin libraries; richer search and management in the web interface.
- Personal video favorites, Watch Later, custom ordered playlists, and followed channels.
- A personal home screen with Continue Watching, Watch Later, favorites, and recent uploads from followed channels.
- Optional watch history and resume progress across browsers using the same Jellyfin account.
- Per-user display, search, playback, and privacy settings.
- Administrator configuration, user access policies, quota controls, and diagnostics.

Favorites, follows, playlists, and history are JellyTube records. They do not change a user's Google account. Clearly label this distinction in the UI. Local playlists start private; sharing is deferred.

## Existing project patterns to reuse

Use selected patterns after review, rather than copying an entire application:

| Project | Relevant patterns |
| --- | --- |
| Jelly-Manga / JellyTome | Embedded application resources, header navigation, File Transformation registration, authenticated identity, access checks, self-contained plugin deployment |
| JellySpot | SQLite migrations, owner-scoped collections/settings, privacy tests, browser pagination, independent shelves, bounded caching |
| JellySeerr | Home-tab navigation, coexistence with other injected interfaces, relative URLs for reverse-proxy base paths, release packaging |

Keep JellyTube's plugin ID, transformation ID, routes, CSS classes, browser events, and database independent. Do not reuse JellySpot's hardcoded internal YouTube Music client or make a dependency on another Jelly plugin.

## Standalone native playback design (revised 7 November 2026)

The installed plugin has no external downloader or JavaScript runtime. Extraction and downloading run in managed C# using the bundled YoutubeExplode library. No yt-dlp, youtube-dl, Python, Deno, Node, sidecar, or container is invoked by the plugin. Build/test tools on a developer machine are separate from runtime requirements.

Jellyfin's existing configured FFmpeg handles native playback remux/transcode and merges completed downloads. No additional encoder is installed. The portable package contains the plugin DLL, the managed extraction DLL, metadata, and notices; it contains no architecture-specific executables or native libraries.

### Native catalog and private accounts

Use a standard restricted Jellyfin library for each user. Jellyfin's channel implementation materializes provider results into shared library records, making private per-user folder membership unsafe to assume even with user-specific response-cache keys. Private native libraries avoid this shared-channel ambiguity and work with ordinary Jellyfin library browsing and playback.

An explicit administrator setup action creates each private library. Other non-administrators retain access to their previous libraries through an explicit allowlist; they cannot see this new library. Administrators retain their normal access. User-creation events and a user-policy action filter preserve this isolation when accounts or policies change. Do not modify a production server during development; validate this behavior on an isolated server with multiple accounts first.

Use stable owner/video directories and STRM items, with NFO titles, descriptions and artwork. Library items keep their identity across source expiry and retained downloads. Jellyfin user data supplies canonical native favorites and playback progress once an item is indexed. Personal Watch Later, collections, channel follows and preferences remain owner-scoped SQLite records. Native clients browse prepared/saved native items and Jellyfin favorites; arbitrary YouTube search screens remain dependent on the client UI.

### Seekable streaming without full preparation

1. Resolve YouTube's indexed MP4 video and AAC audio streams in managed code, preferring H.264 to limit NAS transcoding work.
2. Read only bounded initialization/index bytes and parse ISO-BMFF sidx references in C#.
3. Generate a static MPEG-DASH manifest with explicit per-track segment timelines and byte ranges. Explicit SegmentList references avoid FFmpeg reading linearly through a single-file source when seeking.
4. Serve audio/video ranges through owner/video-specific unguessable capabilities. Upstream URLs never become durable library URLs. Only HTTPS googlevideo.com media destinations are accepted, redirects are disabled, and arbitrary user URLs/flags are never executed.
5. Let Jellyfin negotiate client playback using its ordinary native player and configured FFmpeg. Quality changes apply to future manifests; active track URLs bind a quality and format fingerprint so a settings change cannot silently swap a source under a running session.
6. Refresh expired sources once before returning a useful playback error. Bound extractor concurrency and metadata caches; cache up to 16 MiB of public byte ranges for repeated reads/seeks.

Smoothness goals: one Play action enters the normal player, warm first frame within 3 seconds, cold within 8 seconds, cached seeks within 2 seconds at p95 on a documented reference setup. Measure rather than promise performance across all NAS/client/network combinations.

### Retained downloads

Managed downloads select compatible separate tracks, download to a bounded staging area, and merge with Jellyfin's configured FFmpeg using stream copy. Publish complete files atomically outside scanned source directories and serve them through the stable native item. The queue persists across restart and supports cancellation, retry, per-user visibility, and explicit local-copy removal. Storage budgets and a per-item limit bound disk use; interactive extraction has separate capacity from background downloading.

No personal Google cookies or account credentials are required or supported in the initial implementation. Private/age-restricted videos, live/DVR streams and upstream verification requirements need explicit unsupported states. If YouTube changes extraction behavior, ship a tested plugin/dependency update rather than asking NAS owners to install a command-line tool.

References:
- https://github.com/Tyrrrz/YoutubeExplode
- https://jellyfin.org/docs/general/post-install/transcoding/
- https://raw.githubusercontent.com/jellyfin/jellyfin/v10.11.3/src/Jellyfin.LiveTv/Channels/ChannelManager.cs

## User experience

One YouTube navigation entry opens five views:

1. Home: personal shelves and followed-channel uploads; no claim to reproduce YouTube's personalized recommendation feed.
2. Search: Videos / Channels / Playlists; preserve query, filters, position, and results on Back.
3. Library: Favorites / Watch Later / My Playlists / Followed Channels.
4. History: resume, remove an entry, clear history, and pause recording.
5. Settings: personal preferences and data management.

Video cards expose Play, Favorite, Watch Later, Add to Playlist, and permission-controlled Download. Download activity shows preparing/downloading/merging/indexing/ready states. Channel pages expose Follow and recent uploads. Empty, loading, stale, quota-exhausted, disabled-account, and provider-error states explain what the user can do next. Unavailable saved videos remain removable without blocking the rest of a list.

Use responsive layouts, accessible labels, keyboard navigation, focus restoration, browser Back, lazy images, and bounded result pages. Stop active playback on logout/account switch and clear all account-scoped browser state.

## Settings and authority

| Scope | Settings |
| --- | --- |
| Administrator | API key; plugin access per user; minimum SafeSearch; allowed regions if desired; per-user search limits; project-wide quota budget; refresh cadence; metadata retention; managed extractor health; Jellyfin FFmpeg availability; download/cache folders and budgets; extraction/download concurrency; download permissions; health diagnostics |
| User: search | Language, region within policy, default result type, preferred sorting, SafeSearch at or above the administrator minimum |
| User: playback | Autoplay off by default, queue behavior, resume behavior, default volume; preferred maximum resolution, data-saving mode, stream/download preference, download quality, and captions where supported |
| User: interface | Grid/list, card density, home shelf order, optional Shorts filtering when reliably identifiable |
| User: privacy | Record watch history, record search history, history retention, clear history, export/delete personal JellyTube data |

Use capability responses to show effective permissions in the UI. Enforce them again on the server, including direct video-ID and playlist/channel routes. SafeSearch is a provider filter, not a guarantee that all content is suitable for children. If stronger filtering is required, add an explicit channel allowlist mode and apply it to every playback entry point.

## Architecture and storage

Backend: C# plugin, dependency injection, authenticated ASP.NET Core controllers, managed extraction, typed HttpClient, SQLite, and bounded background work. Frontend: embedded JavaScript/CSS following the existing plugins' approach, with isolated styling and idempotent mounting through JellyTube’s own index-response middleware. File Transformation is a compatibility fallback, not a dependency. Provide an administrator page even when navigation injection is unavailable.

Suggested components:

- YouTubeCatalogClient: search, video details, channels, and playlist membership.
- QuotaManager: request accounting, fair per-user limits, bounded retries, and circuit breaking.
- MetadataCache: public provider responses only, bounded by age and size.
- UserLibraryService: favorites, Watch Later, playlists, follows, and settings.
- NativeLibrary: private standard libraries and stable native item identities.
- Extractor: managed stream resolution, manifest caching, and bounded concurrency.
- PlaybackSourceService: current media sources, format selection, expiry recovery, and Jellyfin handoff.
- StreamRelay/CacheManager: seek-aware transport, leases, and bounded transient storage.
- DownloadService: persistent deduplicated jobs, per-user requests, staging, publication, and library indexing.
- WatchHistoryService: Jellyfin user-data bridge with optional history; verify history-off behavior across native clients and disclose any Jellyfin-level history settings required.
- ChannelRefreshService: shared channel-upload metadata refresh, with private per-user follow membership.
- JellyTubeStore: transactions, schema migrations, backups, and cleanup.

Store plugin data under Jellyfin's plugin data directory:

| Table | Ownership / key |
| --- | --- |
| user_settings | Jellyfin user ID |
| favorites | User ID + video ID, saved timestamp |
| watch_later | User ID + video ID, saved timestamp |
| playlists | Playlist ID + owner ID + name |
| playlist_items | Playlist ID + entry ID + video ID + ordering |
| channel_follows | User ID + channel ID + followed timestamp |
| watch_history | User ID + video ID + position + last watched + completion |
| search_history | User ID + query + timestamp; optional and bounded |
| metadata_cache | Resource type + provider ID + fetched/expiry times |
| media_items | Stable YouTube ID to native Jellyfin item mapping, owner scope where required |
| extraction_cache | Video + format + credential context + expiry; protected transient source data |
| download_jobs | Shared byte job, state, format, staging/final path |
| download_requests | Job + requesting user + ownership/retention policy |
| cache_entries | Video/format + size + expiry + playback lease |
| quota_usage | Project/provider bucket + date + local accounting |

Use Jellyfin user IDs rather than usernames. Resolve identity exclusively from the authenticated session; never accept an owner ID as authorization. Validate playlist ownership for every membership operation. Public metadata may be shared in cache; personal library responses must not enter a shared cache. Retain user-created references separately from expiring provider metadata, and implement the refresh/deletion rules required for each data category.

## API outline

All personal routes require a Jellyfin user session and plugin access. Static assets may be public. Administrator routes require elevation.

- GET /JellyTube/Session: capabilities and effective settings.
- GET /JellyTube/Search: query, type, filters, opaque page token.
- GET /JellyTube/Videos/{id}, /Channels/{id}, /Playlists/{id}: provider metadata; keep provider and local playlist routes unambiguous in implementation.
- GET/PATCH /JellyTube/Me/Settings.
- GET /JellyTube/Me/Favorites; PUT/DELETE /JellyTube/Me/Favorites/{videoId}.
- Equivalent personal WatchLater and FollowedChannels routes.
- CRUD /JellyTube/Me/Playlists, with item add/remove/reorder operations and optimistic concurrency.
- GET/DELETE /JellyTube/Me/History; PUT /JellyTube/Me/History/{videoId}/Progress.
- POST /JellyTube/Videos/{id}/Prepare: idempotently resolve/create an authorized native item; return item ID and preparation status, never a durable upstream URL.
- POST/GET /JellyTube/Me/Downloads; cancellation/retry and retained-copy removal routes with explicit owner rules.
- Native playback continues through Jellyfin playback APIs; internal relay requests use scoped session/source authorization and range semantics.
- GET /JellyTube/Me/Export; DELETE /JellyTube/Me/Data.
- GET/PATCH /JellyTube/Admin/Configuration; GET /JellyTube/Admin/Health.

Use idempotent favorite/follow operations, bounded inputs, stable JSON casing, cancellation tokens, UTC timestamps, and structured errors. Never return the API key in ordinary settings responses or write credentials, tokens, or personal search terms to diagnostic logs.

## Search quota and refresh

At planning time, Google's official documentation describes a default 100 search.list calls per day in a separate Search Queries quota bucket. Check the actual project's console during setup and make limits configurable; do not assume unlimited searches or hardcode historical quota models.

- Search on submit, not each keystroke. Each next-page fetch may require another provider request.
- Cache identical public searches using every response-affecting filter and policy value in the key.
- Coalesce concurrent identical requests and enforce fair per-user budgets within the server budget.
- Use video-detail batching and channel upload playlists where appropriate, rather than repeatedly searching for uploads.
- Refresh only channels actually followed, deduplicate across users, and use a bounded scheduled budget.
- Serve permitted cached metadata and local libraries during exhaustion; explain when discovery can resume.
- Honor provider rate limits and bounded backoff. Local accounting is an estimate if the Google project is used elsewhere.

References: https://developers.google.com/youtube/data/getting-started and https://developers.google.com/youtube/data/reference/rest/v3/search/list

## Implementation milestones

1. **Native playback feasibility:** real managed C# extraction; stable Jellyfin item; combined and separate A/V sources; native playback on web, Android TV, and one additional intended client; seek, resume, URL-expiry recovery, and two-user catalog isolation. Exit: documented working transport/muxing path, native browse surface, and measured cold/warm startup. Resolve interface limitations here before building the full UI.
2. **Foundation:** plugin policies, managed extractor health, SQLite migrations, private native libraries, managed DASH transport, bounded relay/cache, discovery provider, packaging, and self-contained web navigation. Exit: native playback survives restart and functions without web injection.
3. **Personal library:** favorites, Watch Later, playlists, channel follows, native saved-item exposure, settings, Jellyfin progress bridge, and two-account privacy verification.
4. **Downloads and polish:** persisted jobs, per-user permissions, local library publication, seek/cache tuning, next-item preparation, responsive discovery UI, and recovery states. Exit: stream-now and retained-file paths both work and background downloads do not starve playback.
5. **Release hardening:** plugin/dependency update and rollback, quotas/storage limits, export/deletion, lifecycle cleanup, coexistence checks, accessibility, documented client matrix, and verified release artifact.

Keep Google OAuth, importing subscriptions/playlists, Google-account writes, shared collections, comments, and live chat for later milestones. Native playback and optional downloads are part of the initial release. Live/DVR support needs a separate tested milestone.

## Required validation

- Authorization tests with two users, including guessed playlist IDs, direct API calls, disabled access, and cross-user exports/deletion.
- Database tests for idempotency, ordering conflicts, migration rollback/backup, restart persistence, and user removal cleanup.
- Provider tests for pagination, cache-key separation, expired metadata, quota exhaustion, retry limits, cancellation, and unavailable resources.
- Playback checks for combined/separate streams, audio presence/sync, Range requests, random seeks, resume after restart, expired URLs, captions, quality changes, remux/transcode, unavailable sources, and concurrent viewers.
- Download/cache checks for deduplication with private ownership, storage exhaustion, active leases, cancellation, crash recovery, atomic publication, and retention deletion.
- Browser/native checks for favorites/settings persistence, queue/resume, logout/account switch, keyboard/remote access, narrow screens, reverse-proxy base paths, and native playback without File Transformation.
- Integration checks on the installed Jellyfin patch version, alongside JellySpot, JellyTome, and JellySeerr; record actual tested clients instead of implying universal native support.

Recommended implementation order: prove native streaming, seeking, and user isolation first; then build personal discovery/library features and optional retained downloads around that working path.

## Preview delivery and follow-up scope

The current implementation delivers the standalone managed extractor, private native libraries, seekable DASH transport, personal collections/settings, managed downloads and self-contained web navigation. See docs/validation.md for concrete verified checks.

The following design items remain follow-up work and are not claimed in the preview: physical Synology/TV-client certification; full subtitle integration; server-scheduled follow refresh beyond on-demand cached uploads; cross-user retained-byte deduplication; strong channel allowlists; complete official-API retention-policy automation; and formal performance/load certification. Native search screens cannot be supplied to arbitrary client apps by a server plugin alone.
