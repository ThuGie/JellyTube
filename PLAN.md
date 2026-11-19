# JellyTube performance overhaul status

## Implemented in 0.1.0.23

- Pooled WAL SQLite connections, independent reads, transactional batches, indexed queue lookup, bounded cache eviction, public metadata with private discovery references, and aggregate diagnostics.
- Owner-scoped persistent feed snapshots, server-side aggregation, opaque continuation cursors, stale-content refresh, stable cards, General selection, managed continuation reuse, and active sorting across pages.
- Official APIs for credentialed search, channel uploads, public playlists, details, and category charts; complete metadata batches and filtering of private videos. Managed discovery remains available without credentials.
- One direct playback-start request, compatible local reuse, signed-format expiry, one bounded extraction fallback, original audio, configured quality, visible-frame telemetry, cancellable startup, one startup retry, and verified-keyframe resume/seeking.
- Shared byte-range transport with validated provider ranges, separate interactive capacity, persistent bounded thumbnails, inventory counters, coalesced download references, independent cancellation, legacy migration, and deferred deletion while media is open.
- Release notes, reproducible packaging, automated catalog generation, regression coverage, real-server checks, and browser/performance test scripts.

## Verification and release

Local implementation and verification are recorded in [validation.md](docs/validation.md). Publish through the existing GitHub Actions release workflow and verify the public archive, checksum, changelog, and manifest. Keep the existing repository URL and library identities.

Physical Synology verification and a matched historical performance baseline are still outstanding. Do not describe local Windows measurements as proof that the user's NAS delay is resolved.
