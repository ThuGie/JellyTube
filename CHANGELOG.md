# Changelog

## 0.1.0.1

- Register native playback items directly when Play is pressed. Streaming and retained downloads no longer wait for library indexing, including videos whose stream files already exist.

## 0.1.0.0

- Standalone managed C# extraction and downloads; no external downloader, runtime, helper plugin or sidecar.
- Native Jellyfin playback with seekable DASH audio/video transport and retained local copies.
- Private per-user libraries, favorites, Watch Later, playlists, followed channels and settings.
- Self-contained web navigation, administrator setup, download permissions, quotas and storage controls.
- Preview: tested on Jellyfin 10.11.11; physical Synology and TV-client certification remains pending.
