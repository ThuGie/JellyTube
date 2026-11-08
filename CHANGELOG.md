# Changelog

## 0.1.0.16

- Use complete YouTube MP4 formats from a bounded quick lookup and keep the full extractor as a fallback.
- Reuse visitor data across requests while preserving original audio, resolution, and native playback compatibility.

## 0.1.0.15

- Put General first in the Home category row and select it by default.
- Keep direct playback, resume, saved videos, and downloads private without adding native library items.
- Exclude JellyTube's shared library from Recently Added and ask before adding a video for native playback.

## 0.1.0.14

- Play supported streams directly in Jellyfin Web, with per-user resume and a native player fallback.
- Respect Jellyfin playback permissions and administrator-required transcoding.
- Default shared streaming to 1080p and prefer resolution before frame rate.
- Add a General feed and wait for a mix of sources on For You.
- Generate useful manifest changelogs and fill in previous releases.

## 0.1.0.13

- Play supported streams directly in Jellyfin Web, with per-user resume and a native player fallback.
- Default shared streaming to 1080p and prefer resolution before frame rate.
- Add a General feed and wait for a mix of sources on For You.
- Generate useful manifest changelogs and fill in previous releases.

## 0.1.0.12

- Keep the player visible on late startup, cache thumbnails and metadata, and keep feed cards stable.

## 0.1.0.11

- Prefer original audio, pass through compatible MP4 streams, and reduce startup buffering.

## 0.1.0.10

- Separate Explore topics, add newest-first sorting and scroll-to-load, and open Search immediately.

## 0.1.0.9

- Expand recommendations and add Explore, Watch Later, and Favorites feeds.

## 0.1.0.8

- Consolidate native libraries, redesign Home, show video details, and split Shorts from full videos.

## 0.1.0.7

- Track playback through first frame, improve shared storage, and add filters and browse pagination.

## 0.1.0.6

- Warm playback earlier, share media across accounts, and expose video details.

## 0.1.0.5

- Keep search results stable when loading more or switching tabs.

## 0.1.0.4

- Use Jellyfin's single-item playback action and keep playback status visible while scrolling.

## 0.1.0.3

- Support both Jellyfin library API signatures during native video registration.

## 0.1.0.2

- Keep the JellyTube page in place during native playback and restore it when the player closes.
- Coordinate video registration with Jellyfin's file watcher and include server log references for unexpected playback errors.
- Fetch audio and video indexes in parallel during streaming startup.
- Recommend unwatched uploads from followed and recently watched channels on Home. When history is enabled, use tags from recent watches to discover videos across channels.

## 0.1.0.1

- Register native playback items directly when Play is pressed. Streaming and retained downloads no longer wait for library indexing, including videos whose stream files already exist.

## 0.1.0.0

- Standalone managed C# extraction and downloads; no external downloader, runtime, helper plugin or sidecar.
- Native Jellyfin playback with seekable DASH audio/video transport and retained local copies.
- Private per-user libraries, favorites, Watch Later, playlists, followed channels and settings.
- Self-contained web navigation, administrator setup, download permissions, quotas and storage controls.
- Preview: tested on Jellyfin 10.11.11; physical Synology and TV-client certification remains pending.
