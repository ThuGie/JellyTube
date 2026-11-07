using System.Diagnostics;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.Plugin.JellyTube;

/// <summary>Uses only the FFmpeg already configured by Jellyfin. Never installs an external downloader.</summary>
public sealed class JellyfinMuxer(IMediaEncoder encoder)
{
    public string EncoderPath => encoder.EncoderPath;
    public async Task Merge(string video, string audio, string output, CancellationToken ct)
    {
        var start = new ProcessStartInfo(encoder.EncoderPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", video, "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", output }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new ProviderException("Jellyfin's FFmpeg could not start.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(20));
        using var registration = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stdout = Drain(process.StandardOutput, timeout.Token); var stderr = Drain(process.StandardError, timeout.Token);
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0 || !File.Exists(output) || new FileInfo(output).Length == 0) throw new ProviderException("Jellyfin's FFmpeg could not merge the audio and video tracks.");
        }
        finally { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
    }
    private static async Task Drain(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[4096]; while (await reader.ReadAsync(buffer, ct) != 0) { }
    }
}
