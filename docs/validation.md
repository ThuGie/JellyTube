# Validation and compatibility

Release **0.1.0.23**, validated 19 November 2026 on isolated Windows Jellyfin **10.11.11** and through the HTTPS API/media transport of a Synology-hosted server reporting version **12.4.0**. The plugin targets Jellyfin 10.11.3 APIs and .NET 9. DSM, ARM64, and host hardware certification were not performed.

## Automated checks

- 52 .NET tests and three release/catalog tests pass; Release packaging builds without warnings.
- Official API contract tests cover complete channel cards, continuation, metadata batching and reuse, public/private filtering, playlist privacy, and API-key category charts. Fake Google responses are used; no credentials are included in tests.
- Real-server integration checks cover shared library access, private favorites and playlists, native item identity, playback negotiation, DASH audio/video, byte ranges, retained playback, and FFmpeg seeking to 120 seconds.
- Shared-storage checks cover matching concurrent requests, independent cancellation, immediate late reuse, removal during playback, release after an aborted reader, validated legacy migration, different qualities, and restart recovery.
- Public YouTube discovery checks cover video/channel/playlist search, private follows, uploads, playlist browsing, download restrictions, cancellation, managed downloads, and stream-copy muxing.
- Desktop/mobile browser checks cover General selection, complete cards, active sorting while loading more, keyboard Details, stalled-start cancellation, startup retry, direct playback, resume, seeking, explicit native playback, return to the originating page, account-switch isolation, connected-feed fixtures, and the embedded dashboard. Successful runs have no page errors.

Machine-readable reports and screenshots remain under ignored `artifacts/live/`. Test credentials, source capabilities, and signed upstream URLs are excluded from distributable documentation.

## Local playback measurements

`tests/playback-performance.cjs` measured the same public video (`V12x80R_t_Y`) at 1080p for 20 cold-format-cache and 20 warm-format-cache starts. All 40 starts decoded a visible video frame on the first click.

| Measurement | p95 |
| --- | ---: |
| Format lookup cache cleared before Play | 354 ms |
| Warm format lookup | 214 ms |
| Cached private feed response | 12.4 ms |

The first cold-format attempt took 872 ms. Cold-format tests invalidate the signed-format lookup cache; shared byte-range caches and browser connection pools can remain warm. These are local measurements of the new version, not a matched historical before/after benchmark, a fully cold network test, or a NAS guarantee.

Direct DASH playback does not re-encode media. Resume and seeking align to verified random-access boundaries to avoid Chromium mid-GOP decode failures; playback may resume a few seconds before the saved position. Local and combined MP4 playback retain browser seeking.

## Live NAS verification

The installed plugin was confirmed active at 0.1.0.23 with a working Google connection and a 1080p preference. Public media URL, shared library, and FFmpeg availability checks passed.

| Measurement through the NAS | Result |
| --- | ---: |
| Cold stream preparation, one request | 910 ms |
| Warm stream preparation | 81 ms |
| Decoded 1080p first frame, 20 warm-cache starts | 20/20 successful; p95 316 ms |
| Largest observed first-frame time in those starts | 632 ms |
| Cached feed responses, 20 requests | 20/20 successful; p95 44 ms |
| Official search, first request / cached repeat | 878 ms / 39 ms |

Live Google subscriptions, search, channel uploads, public-playlist discovery, and playlist browsing all succeeded. Refreshed feeds returned complete date metadata; legitimately empty provider descriptions remain empty.

Video frames were decoded in an isolated Chromium video element using the NAS's own bundled DASH player and media endpoints. Format and byte-range caches were warm for the frame measurements. This did not drive the production Jellyfin UI. No configuration, library, viewing-history, or resume settings were changed; the temporary API session was logged out afterward. Credentials, server address, subscription contents, and capability URLs were not saved in reports or documentation.

SQLite batch writes still measured approximately 160 ms at p95 on this NAS and contribute to cold refresh cost. Warm feed requests avoided that cost. Native playback/transcoding and host-level disk/CPU inspection were not exercised remotely.

## Remaining verification limits

- Physical Synology DSM/package permissions, ARM64 execution, host-level CPU/I/O load, and the full production Jellyfin UI remain unverified. HTTPS API/media timing through the reverse proxy was measured as described above.
- Existing live Google account access was verified on the NAS. A fresh Google consent/reconnect flow was not repeated; deterministic official-API and connected-UI tests complement the live checks.
- Android TV, iOS, Android, casting, hardware decoding, and sustained multi-viewer load need separate device testing.
- Live/DVR streams, private or restricted YouTube playback, subtitle integration, and YouTube's personalized homepage are outside this release. The official Data API supplies subscriptions and public discovery, not an exact personalized Home feed.

These measurements confirm fast cached API responses and successful decoded playback through the NAS for the tested source. They do not establish a matched historical before/after baseline or guarantee every cold source, client, or UI session.
