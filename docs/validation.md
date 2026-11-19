# Validation and compatibility

Candidate: **0.1.0.23**, validated 19 November 2026 on an isolated Windows Jellyfin **10.11.11** server. The plugin targets Jellyfin 10.11.3 APIs and .NET 9. This is not physical Synology certification.

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

## Remaining verification limits

- Physical Synology DSM/package permissions, ARM64 execution, real NAS CPU/I/O load, and timing over the user's reverse proxy remain unverified.
- Live Google OAuth subscription/account behavior was not exercised with a real Google account in this isolated setup. Official request/response handling and connected browser behavior have deterministic coverage.
- Android TV, iOS, Android, casting, hardware decoding, and sustained multi-viewer load need separate device testing.
- Live/DVR streams, private or restricted YouTube playback, subtitle integration, and YouTube's personalized homepage are outside this release. The official Data API supplies subscriptions and public discovery, not an exact personalized Home feed.

The reported Synology slowdown must be remeasured after installing this version before it can be called resolved.
