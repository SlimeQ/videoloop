#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;

namespace VideoLoop;

public partial class MainWindow : Window
{
    private const string SpotFileName = ".spot";

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".m4v",
        ".mov",
        ".wmv",
        ".avi",
        ".mpg",
        ".mpeg",
        ".mkv"
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string? _selectedFolder;
    private List<string> _playlist = new();
    private WebApplication? _webApp;
    private string? _serverUrl;
    private ServerCoordinator? _coordinator;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object? sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!TrySelectFolder())
        {
            Close();
            return;
        }

        if (!BuildPlaylist())
        {
            ShowError("No playable videos were found in the selected folder.");
            return;
        }

        var spotSnapshot = LoadSpotSnapshot();
        _coordinator = new ServerCoordinator(_playlist, _selectedFolder!, spotSnapshot);

        try
        {
            await StartWebServerAsync(_coordinator);
        }
        catch (Exception ex)
        {
            ShowError($"Unable to start the web server: {ex.Message}");
            return;
        }

        if (string.IsNullOrWhiteSpace(_serverUrl))
        {
            ShowError("The web server started but no address was reported.");
            return;
        }

        InstructionText.Text = "Keep this window open while the browser player is running.";
        ShowStatus("Serving videos to your browser.");
        ShowServerDetails(_serverUrl);
        OpenBrowser(_serverUrl);
    }

    protected override async void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        await StopWebServerAsync();
    }

    private bool TrySelectFolder()
    {
        var args = Environment.GetCommandLineArgs().Skip(1);
        foreach (var arg in args)
        {
            var expanded = Environment.ExpandEnvironmentVariables(arg.Trim('"'));
            if (string.IsNullOrWhiteSpace(expanded))
            {
                continue;
            }

            string? fullPath = null;
            try
            {
                fullPath = Path.GetFullPath(expanded);
            }
            catch
            {
                fullPath = expanded;
            }

            if (fullPath is not null && Directory.Exists(fullPath))
            {
                AssignFolder(fullPath);
                return true;
            }
        }

        var currentDir = Environment.CurrentDirectory;
        if (Directory.Exists(currentDir) && ContainsVideos(currentDir))
        {
            AssignFolder(currentDir);
            return true;
        }

        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Select a folder that contains the videos to loop.",
            ShowNewFolderButton = false,
            UseDescriptionForTitle = true
        };

        var owner = new Win32Window(new WindowInteropHelper(this).Handle);
        var result = dialog.ShowDialog(owner);

        if (result != Forms.DialogResult.OK || string.IsNullOrWhiteSpace(dialog.SelectedPath))
        {
            return false;
        }

        AssignFolder(dialog.SelectedPath);
        return true;
    }

    private bool ContainsVideos(string folder)
    {
        try
        {
            return Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Any(path => SupportedExtensions.Contains(Path.GetExtension(path)));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to inspect folder {folder}: {ex}");
            return false;
        }
    }

    private void AssignFolder(string folder)
    {
        string resolved;
        try
        {
            resolved = Path.GetFullPath(folder);
        }
        catch
        {
            resolved = folder;
        }

        _selectedFolder = resolved;
        var trimmed = Path.TrimEndingDirectorySeparator(resolved);
        var folderName = Path.GetFileName(trimmed);
        Title = string.IsNullOrEmpty(folderName) ? "Video Loop" : $"Video Loop - {folderName}";
    }

    private bool BuildPlaylist()
    {
        if (_selectedFolder is null)
        {
            return false;
        }

        try
        {
            _playlist = Directory.EnumerateFiles(_selectedFolder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to read the folder: {ex}");
            _playlist.Clear();
        }

        return _playlist.Count > 0;
    }

    private SpotSnapshot LoadSpotSnapshot()
    {
        if (_selectedFolder is null || _playlist.Count == 0)
        {
            return new SpotSnapshot(0, 0);
        }

        var spotPath = Path.Combine(_selectedFolder, SpotFileName);
        if (!File.Exists(spotPath))
        {
            return new SpotSnapshot(0, 0);
        }

        try
        {
            var content = File.ReadAllText(spotPath).Trim();
            if (string.IsNullOrEmpty(content))
            {
                return new SpotSnapshot(0, 0);
            }

            var parts = content.Split('|', 2);
            var fileName = parts[0].Trim();
            var matchIndex = _playlist.FindIndex(path =>
                string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase));

            if (matchIndex < 0)
            {
                return new SpotSnapshot(0, 0);
            }

            var position = 0.0;
            if (parts.Length > 1)
            {
                var positionText = parts[1].Trim();
                if (double.TryParse(positionText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) &&
                    parsed > 0)
                {
                    position = parsed;
                }
            }

            return new SpotSnapshot(matchIndex, position);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read .spot file: {ex}");
            return new SpotSnapshot(0, 0);
        }
    }

    private async Task StartWebServerAsync(ServerCoordinator coordinator)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = Array.Empty<string>()
        });

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, 0, listenOptions =>
            {
                listenOptions.Protocols = HttpProtocols.Http1;
            });
        });

        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton<FileExtensionContentTypeProvider>();

        var app = builder.Build();

        app.MapGet("/", () => Results.Content(BuildHtml(), "text/html; charset=utf-8"));

        app.MapGet("/api/state", (ServerCoordinator state) => Results.Json(state.CreateStateDto()));

        app.MapPost("/api/spot", async (HttpContext context, ServerCoordinator state) =>
        {
            var payload = await context.Request.ReadFromJsonAsync<SpotUpdatePayload>(JsonOptions);
            if (payload is null)
            {
                return Results.BadRequest();
            }

            state.UpdateSpot(payload.Index, payload.PositionSeconds);
            return Results.Ok();
        });

        app.MapMethods("/media/{index:int}", new[] { HttpMethods.Get, HttpMethods.Head }, (int index, HttpContext context, ServerCoordinator state, FileExtensionContentTypeProvider provider) =>
        {
            if (!state.TryGetVideo(index, out var video))
            {
                return Results.NotFound();
            }

            if (!provider.TryGetContentType(video.Path, out var contentType))
            {
                contentType = "application/octet-stream";
            }

            context.Response.Headers["Accept-Ranges"] = "bytes";
            return Results.File(video.Path, contentType, enableRangeProcessing: true);
        });

        try
        {
            await app.StartAsync();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var addressesFeature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        _serverUrl = addressesFeature?.Addresses.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(_serverUrl) && app.Urls.Count > 0)
        {
            _serverUrl = app.Urls.First();
        }

        _webApp = app;
    }

    private async Task StopWebServerAsync()
    {
        if (_webApp is null)
        {
            return;
        }

        try
        {
            await _webApp.StopAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to stop the web server: {ex}");
        }
        finally
        {
            await _webApp.DisposeAsync();
            _webApp = null;
            _serverUrl = null;
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
    }

    private void ShowError(string message)
    {
        ShowStatus(message);
        ServerUrlText.Visibility = Visibility.Collapsed;
        OpenBrowserButton.Visibility = Visibility.Collapsed;
    }

    private void ShowServerDetails(string address)
    {
        ServerUrlText.Text = address;
        ServerUrlText.Visibility = Visibility.Visible;
        OpenBrowserButton.Visibility = Visibility.Visible;
    }

    private void OnOpenBrowserClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_serverUrl))
        {
            OpenBrowser(_serverUrl);
        }
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Unable to open browser: {ex}");
        }
    }

    private static string BuildHtml() =>
        """
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <title>Video Loop Web Player</title>
          <meta name="viewport" content="width=device-width, initial-scale=1.0" />
          <style>
            :root {
              color-scheme: light dark;
              font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
            }

            body {
              margin: 0;
              padding: 24px;
              background: #111;
              color: #f5f5f5;
            }

            body.light {
              background: #f5f5f5;
              color: #111;
            }

            main {
              max-width: 920px;
              margin: 0 auto;
            }

            h1 {
              font-size: 1.8rem;
              margin-bottom: 0.75rem;
            }

            h2 {
              font-size: 1.2rem;
              margin: 1.5rem 0 0.75rem;
            }

            #info {
              margin-bottom: 1.5rem;
              font-size: 1rem;
            }

            video {
              width: 100%;
              max-height: 70vh;
              background: #000;
              border-radius: 12px;
              box-shadow: 0 12px 30px rgba(0, 0, 0, 0.4);
            }

            #playlist {
              display: flex;
              flex-direction: column;
              gap: 8px;
            }

            button.playlist-item {
              text-align: left;
              padding: 10px 14px;
              border-radius: 10px;
              border: 1px solid rgba(255, 255, 255, 0.15);
              background: rgba(255, 255, 255, 0.06);
              color: inherit;
              cursor: pointer;
              font-size: 1rem;
              transition: border-color 0.2s ease, background 0.2s ease;
            }

            button.playlist-item:hover {
              border-color: rgba(255, 255, 255, 0.35);
            }

            button.playlist-item.active {
              border-color: #2f81f7;
              background: rgba(47, 129, 247, 0.2);
            }

            body.light button.playlist-item {
              border: 1px solid rgba(0, 0, 0, 0.1);
              background: rgba(255, 255, 255, 0.8);
            }

            body.light button.playlist-item:hover {
              border-color: rgba(29, 78, 216, 0.35);
            }

            body.light button.playlist-item.active {
              border-color: #1d4ed8;
              background: rgba(29, 78, 216, 0.15);
            }

            @media (max-width: 640px) {
              body {
                padding: 16px;
              }
              h1 {
                font-size: 1.5rem;
              }
            }
          </style>
        </head>
        <body>
          <main>
            <header>
              <h1>Video Loop Web Player</h1>
              <p id="info">Loading playlist...</p>
            </header>
            <section>
              <video id="player" controls playsinline preload="metadata"></video>
            </section>
            <section>
              <h2>Playlist</h2>
              <div id="playlist"></div>
            </section>
          </main>
          <script>
            (() => {
              const state = { videos: [], currentIndex: 0, positionSeconds: 0 };
              const player = document.getElementById('player');
              const playlistContainer = document.getElementById('playlist');
              const info = document.getElementById('info');
              let lastUpdate = 0;
              const updateThrottleMs = 5000;
              const supportNotes = "Firefox only enables HEVC (H.265) starting with version 134 on Windows (requires hardware support or Microsoft's HEVC Video Extensions), version 136 on macOS, and version 137 on Linux/Android. See MDN: https://developer.mozilla.org/en-US/docs/Web/Media/Formats/Video_codecs#hevc_h.265";

              async function fetchState() {
                const response = await fetch('/api/state', { cache: 'no-store' });
                if (!response.ok) {
                  throw new Error('Failed to load playlist');
                }
                return response.json();
              }

              function renderPlaylist() {
                playlistContainer.innerHTML = '';
                state.videos.forEach((video, idx) => {
                  const button = document.createElement('button');
                  button.type = 'button';
                  button.className = 'playlist-item';
                  button.textContent = video.fileName;
                  button.addEventListener('click', () => {
                    setCurrentVideo(idx, false);
                    player.play().catch(() => {});
                  });
                  playlistContainer.appendChild(button);
                });
                highlightActive();
              }

              function highlightActive() {
                const children = playlistContainer.children;
                for (let i = 0; i < children.length; i += 1) {
                  children[i].classList.toggle('active', i === state.currentIndex);
                }
              }

              function normalizeIndex(index) {
                if (state.videos.length === 0) {
                  return 0;
                }
                const modulo = index % state.videos.length;
                return modulo >= 0 ? modulo : modulo + state.videos.length;
              }

              function setCurrentVideo(index, restorePosition) {
                if (state.videos.length === 0) {
                  return;
                }
                const normalized = normalizeIndex(index);
                state.currentIndex = normalized;
                const descriptor = state.videos[normalized];
                state.positionSeconds = restorePosition ? state.positionSeconds : 0;
                const src = `/media/${descriptor.index}`;
                if (player.getAttribute('data-current-src') !== src) {
                  player.setAttribute('data-current-src', src);
                  player.src = src;
                }
                player.load();
                if (restorePosition && state.positionSeconds > 0) {
                  const seekTo = Math.max(state.positionSeconds, 0);
                  const onLoaded = () => {
                    if (!Number.isNaN(seekTo)) {
                      try {
                        player.currentTime = Math.min(seekTo, player.duration || seekTo);
                      } catch {
                        // Ignore seek failures until the media is ready.
                      }
                    }
                    player.removeEventListener('loadedmetadata', onLoaded);
                  };
                  player.addEventListener('loadedmetadata', onLoaded);
                }
                highlightActive();
                queueUpdate(state.positionSeconds, true);
              }

              function playNext() {
                setCurrentVideo(state.currentIndex + 1, false);
                player.play().catch(() => {});
              }

              async function queueUpdate(positionSeconds, urgent = false) {
                if (state.videos.length === 0) {
                  return;
                }
                if (Number.isNaN(positionSeconds) || !Number.isFinite(positionSeconds)) {
                  positionSeconds = 0;
                }
                state.positionSeconds = Math.max(0, positionSeconds);
                const now = Date.now();
                if (!urgent && now - lastUpdate < updateThrottleMs) {
                  return;
                }
                lastUpdate = now;
                const payload = JSON.stringify({
                  index: state.videos[state.currentIndex].index,
                  positionSeconds: state.positionSeconds
                });
                try {
                  await fetch('/api/spot', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: payload,
                    keepalive: urgent
                  });
                } catch (error) {
                  console.warn('Failed to update spot', error);
                }
              }

              function flushUpdate() {
                if (state.videos.length === 0) {
                  return;
                }
                let positionSeconds = Number.isFinite(player.currentTime) ? player.currentTime : 0;
                if (Number.isNaN(positionSeconds) || !Number.isFinite(positionSeconds)) {
                  positionSeconds = 0;
                }
                const payload = JSON.stringify({
                  index: state.videos[state.currentIndex].index,
                  positionSeconds: Math.max(0, positionSeconds)
                });
                try {
                  if (navigator.sendBeacon) {
                    const blob = new Blob([payload], { type: 'application/json' });
                    navigator.sendBeacon('/api/spot', blob);
                  } else {
                    void fetch('/api/spot', {
                      method: 'POST',
                      headers: { 'Content-Type': 'application/json' },
                      body: payload,
                      keepalive: true
                    });
                  }
                } catch (error) {
                  console.warn('Failed to persist spot during unload', error);
                }
              }

              function attachPlayerHandlers() {
                player.addEventListener('timeupdate', () => {
                  queueUpdate(player.currentTime);
                });
                player.addEventListener('pause', () => {
                  queueUpdate(player.currentTime, true);
                });
                player.addEventListener('ended', () => {
                  queueUpdate(0, true);
                  playNext();
                });
                player.addEventListener('error', () => {
                  const mediaError = player.error;
                  const parts = ['Playback failed.'];
                  if (mediaError) {
                    if (mediaError.message) {
                      parts.push(mediaError.message);
                    }
                    if (typeof MediaError !== 'undefined') {
                      switch (mediaError.code) {
                        case MediaError.MEDIA_ERR_SRC_NOT_SUPPORTED:
                          parts.push('This browser reported that the file format or codec is not supported.');
                          break;
                        case MediaError.MEDIA_ERR_NETWORK:
                          parts.push('The video could not be loaded due to a network error.');
                          break;
                        case MediaError.MEDIA_ERR_DECODE:
                          parts.push('There was an error decoding the media data.');
                          break;
                        default:
                          break;
                      }
                    }
                  }
                  parts.push(supportNotes);
                  info.textContent = parts.join(' ');
                  info.dataset.state = 'error';
                });
                window.addEventListener('beforeunload', flushUpdate);
                document.addEventListener('visibilitychange', () => {
                  if (document.visibilityState === 'hidden') {
                    flushUpdate();
                  }
                });
              }

              async function bootstrap() {
                try {
                  const data = await fetchState();
                  state.videos = data.videos ?? [];
                  state.currentIndex = data.currentIndex ?? 0;
                  state.positionSeconds = data.positionSeconds ?? 0;
                  if (window.matchMedia && window.matchMedia('(prefers-color-scheme: light)').matches) {
                    document.body.classList.add('light');
                  }
                  if (state.videos.length === 0) {
                    info.textContent = 'No playable videos were found.';
                    player.removeAttribute('src');
                    return;
                  }
                  info.textContent = 'Ready. The playlist will loop automatically.';
                  renderPlaylist();
                  attachPlayerHandlers();
                  setCurrentVideo(state.currentIndex, true);
                  player.play().catch(() => {});
                } catch (error) {
                  console.error(error);
                  info.textContent = 'Unable to load the playlist. Check the desktop app for details.';
                }
              }

              bootstrap();
            })();
          </script>
        </body>
        </html>
        """;

    private sealed class ServerCoordinator
    {
        private readonly object _gate = new();
        private readonly string _spotPath;
        private readonly List<VideoDescriptor> _videos;
        private int _currentIndex;
        private double _positionSeconds;
        private int _lastPersistedIndex = -1;
        private double _lastPersistedPosition = double.NaN;
        private DateTime _lastPersistUtc = DateTime.MinValue;

        public ServerCoordinator(IEnumerable<string> playlist, string folder, SpotSnapshot snapshot)
        {
            _videos = playlist
                .Select((path, index) => new VideoDescriptor(index, path, Path.GetFileName(path)))
                .ToList();

            _spotPath = Path.Combine(folder, SpotFileName);
            _currentIndex = NormalizeIndex(snapshot.Index);
            _positionSeconds = Math.Max(0, snapshot.PositionSeconds);
        }

        public bool TryGetVideo(int index, out VideoDescriptor descriptor)
        {
            lock (_gate)
            {
                if (index < 0 || index >= _videos.Count)
                {
                    descriptor = default;
                    return false;
                }

                descriptor = _videos[index];
                return true;
            }
        }

        public ServerStateResponse CreateStateDto()
        {
            lock (_gate)
            {
                var safeIndex = _videos.Count == 0 ? 0 : Math.Clamp(_currentIndex, 0, _videos.Count - 1);
                return new ServerStateResponse
                {
                    Videos = _videos.Select(v => new VideoDto(v.Index, v.FileName)).ToList(),
                    CurrentIndex = safeIndex,
                    PositionSeconds = Math.Max(0, _positionSeconds)
                };
            }
        }

        public void UpdateSpot(int index, double positionSeconds)
        {
            lock (_gate)
            {
                if (_videos.Count == 0)
                {
                    return;
                }

                if (double.IsNaN(positionSeconds) || double.IsInfinity(positionSeconds))
                {
                    positionSeconds = 0;
                }

                _currentIndex = NormalizeIndex(index);
                _positionSeconds = Math.Max(0, positionSeconds);
                PersistSpotLocked();
            }
        }

        private void PersistSpotLocked()
        {
            if (_videos.Count == 0)
            {
                return;
            }

            var safeIndex = Math.Clamp(_currentIndex, 0, _videos.Count - 1);
            var now = DateTime.UtcNow;

            if (safeIndex == _lastPersistedIndex &&
                Math.Abs(_positionSeconds - _lastPersistedPosition) < 0.25 &&
                now - _lastPersistUtc < TimeSpan.FromSeconds(1))
            {
                return;
            }

            var fileName = _videos[safeIndex].FileName;
            var payload = $"{fileName}|{_positionSeconds.ToString("F3", CultureInfo.InvariantCulture)}";

            try
            {
                File.WriteAllText(_spotPath, payload);
                _lastPersistedIndex = safeIndex;
                _lastPersistedPosition = _positionSeconds;
                _lastPersistUtc = now;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write .spot file: {ex}");
            }
        }

        private int NormalizeIndex(int index)
        {
            if (_videos.Count == 0)
            {
                return 0;
            }

            var modulo = index % _videos.Count;
            return modulo >= 0 ? modulo : modulo + _videos.Count;
        }
    }

    private sealed class ServerStateResponse
    {
        public required List<VideoDto> Videos { get; init; }
        public int CurrentIndex { get; init; }
        public double PositionSeconds { get; init; }
    }

    private sealed class SpotUpdatePayload
    {
        public int Index { get; set; }
        public double PositionSeconds { get; set; }
    }

    private readonly record struct SpotSnapshot(int Index, double PositionSeconds);

    private readonly record struct VideoDescriptor(int Index, string Path, string FileName);

    private readonly record struct VideoDto(int Index, string FileName);

    private sealed class Win32Window : Forms.IWin32Window
    {
        public Win32Window(nint handle)
        {
            Handle = handle;
        }

        public nint Handle { get; }
    }
}
