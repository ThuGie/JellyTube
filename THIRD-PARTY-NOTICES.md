# Third-party components

- **YoutubeExplode 6.6.3**, copyright Oleksii Holub, MIT. Bundled as a managed DLL. Its license and upstream README are in `licenses/`. Source: https://github.com/Tyrrrz/YoutubeExplode/tree/6.6.3
- YoutubeExplode internalizes **AngleSharp 1.8.3**, **Deorcify 2.0.1**, **JsonExtensions 1.2.3** and **PowerKit 2.4.0**. Their MIT notices and component references are also included in `licenses/`.
- **dash.js 5.3.0**, BSD-3-Clause. The browser playback bundle is embedded in the plugin and served by Jellyfin. Its license and bundled notices are in `licenses/`. [Source](https://github.com/Dash-Industry-Forum/dash.js/tree/v5.3.0)
- Jellyfin server APIs, Microsoft ASP.NET Core, and the server's SQLite/JSON assemblies are provided by Jellyfin and the .NET runtime. JellyTube does not bundle a second Jellyfin runtime or native SQLite binary.
- Jellyfin's existing configured FFmpeg is used for normal playback and lossless audio/video merging. No FFmpeg binary or external downloader is included in the archive.
- File Transformation is supported as a compatibility fallback for existing Jellyfin setups. JellyTube provides its own navigation middleware and does not require it. Source: https://github.com/IAmParadox27/jellyfin-plugin-file-transformation

Developer test/build dependencies are not installed on the NAS and are not shipped in the plugin archive.
