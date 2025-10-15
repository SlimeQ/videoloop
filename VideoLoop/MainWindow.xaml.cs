using System;
using System.Collections.Generic;
using System.ComponentModel;
#nullable enable

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Forms = System.Windows.Forms;

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

    private static bool _libVlcInitialized;
    private static Exception? _libVlcInitException;

    private readonly DispatcherTimer _retryTimer = new();

    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private List<string> _playlist = new();
    private int _currentIndex = -1;
    private string? _selectedFolder;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closing += OnClosing;

        _retryTimer.Interval = TimeSpan.FromSeconds(1);
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            AdvanceToNextVideo();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        if (!TrySelectFolder())
        {
            Close();
            return;
        }

        if (!BuildPlaylist())
        {
            ShowStatus("No playable videos were found in the selected folder.");
            return;
        }

        if (!EnsurePlayerInitialized())
        {
            return;
        }

        LoadLastPlayed();
        PlayCurrentVideo();
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
            catch (Exception ex)
            {
                Debug.WriteLine($"Unable to resolve path from argument '{arg}': {ex}");
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
            ShowStatus($"Unable to read the folder: {ex.Message}");
            _playlist.Clear();
        }

        return _playlist.Count > 0;
    }

    private void LoadLastPlayed()
    {
        if (_selectedFolder is null || _playlist.Count == 0)
        {
            _currentIndex = 0;
            return;
        }

        var spotPath = Path.Combine(_selectedFolder, SpotFileName);
        if (!File.Exists(spotPath))
        {
            _currentIndex = 0;
            return;
        }

        try
        {
            var spotEntry = File.ReadAllText(spotPath).Trim();
            if (!string.IsNullOrEmpty(spotEntry))
            {
                var matchIndex = _playlist.FindIndex(path =>
                    string.Equals(Path.GetFileName(path), spotEntry, StringComparison.OrdinalIgnoreCase));

                _currentIndex = matchIndex >= 0 ? matchIndex : 0;
            }
            else
            {
                _currentIndex = 0;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to read .spot file: {ex}");
            _currentIndex = 0;
        }
    }

    private bool EnsurePlayerInitialized()
    {
        if (_mediaPlayer is not null && _libVLC is not null)
        {
            return true;
        }

        if (!TryInitializeLibVlc(out var error))
        {
            ShowStatus(error ?? "Unable to locate LibVLC runtime.");
            return false;
        }

        try
        {
            _libVLC = new LibVLC();
            _mediaPlayer = new MediaPlayer(_libVLC);
            _mediaPlayer.EndReached += OnMediaEnded;
            _mediaPlayer.EncounteredError += OnMediaEncounteredError;
            VideoViewControl.MediaPlayer = _mediaPlayer;
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to initialize video playback: {ex.Message}");
            return false;
        }
    }

    private static bool TryInitializeLibVlc(out string? error)
    {
        if (_libVlcInitialized)
        {
            error = null;
            return true;
        }

        var baseDir = AppContext.BaseDirectory;
        var arch = Environment.Is64BitProcess ? "win-x64" : "win-x86";

        string CombineAndEnsure(params string[] parts)
        {
            return Path.Combine(parts);
        }

        var searchDirs = new List<string>
        {
            baseDir,
            CombineAndEnsure(baseDir, "libvlc"),
            CombineAndEnsure(baseDir, "libvlc", arch),
            CombineAndEnsure(baseDir, "runtimes", arch, "native"),
            CombineAndEnsure(baseDir, "runtimes", "win", "native")
        };

        foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libPath = Path.Combine(dir, "libvlc.dll");
            if (!File.Exists(libPath))
            {
                continue;
            }

            var pluginsDir = Directory.Exists(Path.Combine(dir, "plugins"))
                ? Path.Combine(dir, "plugins")
                : Directory.Exists(Path.Combine(baseDir, "libvlc", arch, "plugins"))
                    ? Path.Combine(baseDir, "libvlc", arch, "plugins")
                    : Directory.Exists(Path.Combine(baseDir, "plugins"))
                        ? Path.Combine(baseDir, "plugins")
                        : null;

            try
            {
                if (pluginsDir is not null)
                {
                    Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginsDir, EnvironmentVariableTarget.Process);
                }

                Core.Initialize(dir);
                _libVlcInitialized = true;
                error = null;
                return true;
            }
            catch (Exception ex)
            {
                _libVlcInitException = ex;
            }
        }

        error = _libVlcInitException?.Message ?? "libvlc.dll not found in the installation directory.";
        return false;
    }

    private void PlayCurrentVideo()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        if (_currentIndex < 0 || _currentIndex >= _playlist.Count)
        {
            _currentIndex = 0;
        }

        if (!EnsurePlayerInitialized())
        {
            return;
        }

        var player = _mediaPlayer;
        var lib = _libVLC;

        if (player is null || lib is null)
        {
            return;
        }

        var path = _playlist[_currentIndex];

        try
        {
            HideStatus();
            player.Stop();

            _currentMedia?.Dispose();
            _currentMedia = new Media(lib, path, FromType.FromPath);

            if (!player.Play(_currentMedia))
            {
                throw new InvalidOperationException("LibVLC was unable to start playback.");
            }

            PersistSpot();
        }
        catch (Exception ex)
        {
            ShowStatus($"Cannot play {Path.GetFileName(path)}: {ex.Message}");
            ScheduleAdvance();
        }
    }

    private void AdvanceToNextVideo()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        _currentIndex = (_currentIndex + 1) % _playlist.Count;
        PlayCurrentVideo();
    }

    private void ScheduleAdvance()
    {
        if (_retryTimer.IsEnabled)
        {
            return;
        }

        _retryTimer.Start();
    }

    private void PersistSpot()
    {
        if (_selectedFolder is null || _playlist.Count == 0 || _currentIndex < 0)
        {
            return;
        }

        var spotPath = Path.Combine(_selectedFolder, SpotFileName);

        try
        {
            File.WriteAllText(spotPath, Path.GetFileName(_playlist[_currentIndex]));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to write .spot file: {ex}");
        }
    }

    private void ShowStatus(string message)
    {
        StatusText.Text = message;
        StatusPanel.Visibility = Visibility.Visible;
    }

    private void HideStatus()
    {
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(AdvanceToNextVideo));
    }

    private void OnMediaEncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentName = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? Path.GetFileName(_playlist[_currentIndex])
                : "current video";

            ShowStatus($"Playback failed for {currentName}. Skipping…");
            ScheduleAdvance();
        }));
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        PersistSpot();
        DisposePlayer();
    }

    private void DisposePlayer()
    {
        _retryTimer.Stop();

        if (_mediaPlayer is not null)
        {
            _mediaPlayer.EndReached -= OnMediaEnded;
            _mediaPlayer.EncounteredError -= OnMediaEncounteredError;
            _mediaPlayer.Stop();
        }

        VideoViewControl.MediaPlayer = null;

        _currentMedia?.Dispose();
        _currentMedia = null;

        _mediaPlayer?.Dispose();
        _mediaPlayer = null;

        _libVLC?.Dispose();
        _libVLC = null;
    }

    private sealed class Win32Window : Forms.IWin32Window
    {
        public Win32Window(nint handle)
        {
            Handle = handle;
        }

        public nint Handle { get; }
    }
}
