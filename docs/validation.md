# Validation and compatibility

Validation date: 7 November 2026. This is a preview, not physical Synology/client certification.

## Verified locally

- Clean Release build against Jellyfin 10.11.3 APIs; real plugin loading and execution in an isolated Jellyfin **10.11.11** server.
- Managed metadata/search/stream extraction from YouTube, with H.264 720p and AAC separate tracks. No external downloader/runtime process was involved.
- Native Jellyfin playback negotiation, generated DASH manifests, HTTP 206 range transport, both audio/video tracks, and FFmpeg input seeking to 120 seconds.
- Desktop Chromium: one Play action from JellyTube opened Jellyfin's normal video player and decoded video. This was also checked against a completed local retained copy.
- Desktop/mobile Chromium layouts, search, favorites, preference saving, personal libraries and embedded administrator dashboard; no browser page errors in the successful integration run. These checks passed with the File Transformation helper DLL disabled: web navigation is self-contained.
- Alice/Bob libraries are isolated, including direct native item-ID requests. New users do not inherit private libraries; explicitly enabling all libraries through the user-policy endpoint preserves the private-library restriction.
- Indexing refreshes only the owner’s YouTube tree; a newly prepared native item was ready in 2.63 seconds in one local sample (not a NAS benchmark).
- Shared-library parent/child overlap is rejected through both body and query path inputs; normal options edits on the owned private library remain allowed.
- Owner-scoped SQLite settings/collections, restart persistence, optimistic playlist edits, quota concurrency and Pacific-midnight reset, URL/domain/path validation, DASH index parsing and bounded cache behavior are covered by automated tests.
- Managed text, channel and public-playlist discovery, private follows, upload/playlist browsing, per-user download restrictions, cancellation, and other-user retained-copy deletion checks passed.
- A real managed download completed; Jellyfin's configured FFmpeg merged the separate tracks into a retained MP4. The native item identity remains stable.

Machine-readable reports and screenshots are generated under `artifacts/live/`; the live extractor probe is `artifacts/managed-probe.json`. Test credentials are deliberately excluded from distributable documentation.

## Not yet certified

- Physical Synology DSM/package permissions, ARM64 execution, hardware acceleration, real Android TV/iOS/Android clients, casting, and sustained multi-viewer NAS load.
- Universal native YouTube search: rich discovery is the web interface; native apps get ordinary Jellyfin library items.
- Live streams, DVR, Google account imports/cookies, private videos, guaranteed age/region-restricted playback and subtitle track integration.
- Formal p95 first-frame/seek performance across NAS models and real networks. The plan's timing targets are goals, not claims based on a single desktop run.

Jellyfin's own FFmpeg and device profiles decide remux/transcode behavior. Native client support must be recorded client by client rather than inferred solely from server API compatibility.

## Latest automated run

- 26 deterministic .NET tests passed.
- 29 core live server/playback/privacy checks passed in `integration-report.json`, plus two retained-copy checks when a copy exists.
- 14 real discovery/download checks passed in `discovery-download-report.json`.
- 5 browser integration checks passed in `browser-report.json`, including native video playback at both streamed 720p and retained-copy 360p across runs, and a deliberately delayed response across an account change.

These are local checks. CI is configured for Windows and Linux unit/build validation but has not been run on GitHub from this workspace.
