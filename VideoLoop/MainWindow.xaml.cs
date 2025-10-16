#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Forms = System.Windows.Forms;
using InputMouseEventArgs = System.Windows.Input.MouseEventArgs;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using VisualTreeHelper = System.Windows.Media.VisualTreeHelper;
using WindowsSize = System.Windows.Size;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VideoLoop;

public partial class MainWindow : Window
{
    private const string SpotFileName = ".spot";
    private const string PlayLabel = "Play";
    private const string PauseLabel = "Pause";

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
    private readonly DispatcherTimer _overlayHideTimer;

    private LibVLC? _libVLC;
    private VlcMediaPlayer? _mediaPlayer;
    private Media? _currentMedia;
    private List<string> _playlist = new();
    private int _currentIndex = -1;
    private string? _selectedFolder;
    private double? _videoAspectRatio;
    private bool _isAdjustingSize;
    private WindowsSize _lastSize;
    private bool _isSliderDragging;
    private bool _isUpdatingTimeline;
    private bool _isSeekable;
    private bool _isPointerOverWindow;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
        SizeChanged += OnWindowSizeChanged;

        _retryTimer.Interval = TimeSpan.FromSeconds(1);
        _retryTimer.Tick += (_, _) =>
        {
            _retryTimer.Stop();
            AdvanceToNextVideo();
        };

        _overlayHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _overlayHideTimer.Tick += (_, _) =>
        {
            _overlayHideTimer.Stop();
            HideControlsOverlay();
        };

        _lastSize = new WindowsSize(Width, Height);
        ResetTimeline();
        UpdatePlaybackControls();
        HideControlsOverlay();
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
            UpdatePlaybackControls();
            return;
        }

        UpdatePlaybackControls();

        if (!EnsurePlayerInitialized())
        {
            return;
        }

        LoadLastPlayed();
        PlayCurrentVideo();
        ShowControlsOverlay();
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
            _mediaPlayer.Playing += OnMediaPlaying;
            _mediaPlayer.Paused += OnMediaPaused;
            _mediaPlayer.Stopped += OnMediaStopped;
            _mediaPlayer.Vout += OnMediaVout;
            _mediaPlayer.PositionChanged += OnMediaPositionChanged;
            _mediaPlayer.TimeChanged += OnMediaTimeChanged;
            _mediaPlayer.SeekableChanged += OnMediaSeekableChanged;
            VideoViewControl.MediaPlayer = _mediaPlayer;
            UpdatePlaybackControls();
            return true;
        }
        catch (Exception ex)
        {
            ShowStatus($"Unable to initialize video playback: {ex.Message}");
            return false;
        }
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

        if (_mediaPlayer is null || _libVLC is null)
        {
            return;
        }

        var path = _playlist[_currentIndex];

        try
        {
            HideStatus();
            _retryTimer.Stop();
            _videoAspectRatio = null;
            _isSeekable = false;
            ResetTimeline();
            _mediaPlayer.Stop();

            _currentMedia?.Dispose();
            _currentMedia = new Media(_libVLC, path, FromType.FromPath);

            if (!_mediaPlayer.Play(_currentMedia))
            {
                throw new InvalidOperationException("LibVLC was unable to start playback.");
            }

            PersistSpot();
            UpdatePlaybackControls();
            Dispatcher.BeginInvoke(new Action(() => ShowControlsOverlay()));
        }
        catch (Exception ex)
        {
            ShowStatus($"Cannot play {Path.GetFileName(path)}: {ex.Message}");
            ScheduleAdvance();
            UpdatePlaybackControls();
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

    private void GoToPreviousVideo()
    {
        if (_playlist.Count == 0)
        {
            return;
        }

        _currentIndex = (_currentIndex - 1 + _playlist.Count) % _playlist.Count;
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
        ShowControlsOverlay();
    }

    private void HideStatus()
    {
        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowControlsOverlay(bool autoHide = true)
    {
        ControlsOverlay.Visibility = Visibility.Visible;
        ControlsOverlay.Opacity = 1;

        if (autoHide)
        {
            _overlayHideTimer.Stop();
            _overlayHideTimer.Start();
        }
        else
        {
            _overlayHideTimer.Stop();
        }
    }

    private void HideControlsOverlay(bool force = false)
    {
        if (!force && _isPointerOverWindow)
        {
            return;
        }

        _overlayHideTimer.Stop();
        ControlsOverlay.Visibility = Visibility.Collapsed;
        ControlsOverlay.Opacity = 0;
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            ResetTimeline();
            UpdatePlaybackControls();
            AdvanceToNextVideo();
        }));
    }

    private void OnMediaEncounteredError(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var currentName = _currentIndex >= 0 && _currentIndex < _playlist.Count
                ? Path.GetFileName(_playlist[_currentIndex])
                : "current video";

            ShowStatus($"Playback failed for {currentName}. Skipping...");
            ScheduleAdvance();
            UpdatePlaybackControls();
        }));
    }

    private void OnMediaPlaying(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdatePlaybackControls();
            UpdateAspectRatioFromPlayer();
            HideControlsOverlay();
        }));
    }

    private void OnMediaPaused(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdatePlaybackControls();
            ShowControlsOverlay(autoHide: false);
        }));
    }

    private void OnMediaStopped(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdatePlaybackControls();
            ShowControlsOverlay(autoHide: false);
        }));
    }

    private void OnMediaVout(object? sender, MediaPlayerVoutEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateAspectRatioFromPlayer));
    }

    private void OnMediaPositionChanged(object? sender, MediaPlayerPositionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_isSliderDragging)
            {
                return;
            }

            SetTimelineValue(e.Position);
        }));
    }

    private void OnMediaTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Intentionally left blank; retained for potential future timestamp display.
    }

    private void OnMediaSeekableChanged(object? sender, MediaPlayerSeekableChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _isSeekable = e.Seekable != 0;
            UpdatePlaybackControls();
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
            _mediaPlayer.Playing -= OnMediaPlaying;
            _mediaPlayer.Paused -= OnMediaPaused;
            _mediaPlayer.Stopped -= OnMediaStopped;
            _mediaPlayer.Vout -= OnMediaVout;
            _mediaPlayer.PositionChanged -= OnMediaPositionChanged;
            _mediaPlayer.TimeChanged -= OnMediaTimeChanged;
            _mediaPlayer.SeekableChanged -= OnMediaSeekableChanged;
            _mediaPlayer.Stop();
        }

        VideoViewControl.MediaPlayer = null;

        _currentMedia?.Dispose();
        _currentMedia = null;

        _mediaPlayer?.Dispose();
        _mediaPlayer = null;

        _libVLC?.Dispose();
        _libVLC = null;
        _isSeekable = false;
        UpdatePlaybackControls();
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

        static string Combine(params string[] parts) => Path.Combine(parts);

        var searchDirs = new List<string>
        {
            baseDir,
            Combine(baseDir, "libvlc"),
            Combine(baseDir, "libvlc", arch),
            Combine(baseDir, "runtimes", arch, "native"),
            Combine(baseDir, "runtimes", "win", "native")
        };

        foreach (var dir in searchDirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libPath = Path.Combine(dir, "libvlc.dll");
            if (!File.Exists(libPath))
            {
                continue;
            }

            string? pluginsDir = null;

            var candidatePluginFolders = new[]
            {
                Path.Combine(dir, "plugins"),
                Path.Combine(baseDir, "libvlc", arch, "plugins"),
                Path.Combine(baseDir, "plugins")
            };

            foreach (var candidate in candidatePluginFolders)
            {
                if (Directory.Exists(candidate))
                {
                    pluginsDir = candidate;
                    break;
                }
            }

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

    private void UpdatePlaybackControls()
    {
        var hasItems = _playlist.Count > 0;
        var player = _mediaPlayer;
        var isPlaying = player?.IsPlaying == true;

        PlayPauseButton.Content = isPlaying ? PauseLabel : PlayLabel;
        PlayPauseButton.IsEnabled = hasItems && player is not null;
        PreviousButton.IsEnabled = hasItems;
        NextButton.IsEnabled = hasItems;
        TimelineSlider.IsEnabled = hasItems && player is not null && _isSeekable;
    }

    private void ResetTimeline()
    {
        SetTimelineValue(0);
    }

    private void SetTimelineValue(double value)
    {
        _isUpdatingTimeline = true;
        TimelineSlider.Value = Math.Clamp(value, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _isUpdatingTimeline = false;
    }

    private void OnPlayPauseClicked(object sender, RoutedEventArgs e)
    {
        ShowControlsOverlay(autoHide: false);

        if (_mediaPlayer is null)
        {
            PlayCurrentVideo();
            return;
        }

        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            if (_currentMedia is null)
            {
                PlayCurrentVideo();
            }
            else
            {
                _mediaPlayer.Play();
            }
        }

        UpdatePlaybackControls();
    }

    private void OnNextClicked(object sender, RoutedEventArgs e)
    {
        ShowControlsOverlay();
        AdvanceToNextVideo();
    }

    private void OnPreviousClicked(object sender, RoutedEventArgs e)
    {
        ShowControlsOverlay();
        GoToPreviousVideo();
    }

    private void RootGrid_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPointerOverWindow = true;

        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            ShowControlsOverlay(autoHide: false);
            return;
        }

        if (IsOnResizeBorder(e))
        {
            return;
        }

            ShowControlsOverlay(autoHide: false);

        try
        {
            DragMove();
            e.Handled = true;
        }
        catch (InvalidOperationException)
        {
            // Ignore situations where drag cannot start.
        }
    }

    private static bool IsInteractiveElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is ButtonBase or Slider)
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private void UpdateAspectRatioFromPlayer()
    {
        if (_mediaPlayer is null)
        {
            return;
        }

        uint width = 0;
        uint height = 0;
        var hasSize = _mediaPlayer.Size(0, ref width, ref height);
        if (hasSize && width > 0 && height > 0)
        {
            _videoAspectRatio = width / (double)height;
            AdjustWindowToAspectRatio();
        }
    }

    private void AdjustWindowToAspectRatio()
    {
        if (!_videoAspectRatio.HasValue || _videoAspectRatio.Value <= 0)
        {
            return;
        }

        var ratio = _videoAspectRatio.Value;
        var workArea = SystemParameters.WorkArea;
        const double maxScreenFraction = 0.9;

        var desiredWidth = double.IsNaN(Width) || Width <= 0
            ? (ActualWidth > 0 ? ActualWidth : 640)
            : Width;

        var desiredHeight = desiredWidth / ratio;

        if (desiredHeight > workArea.Height * maxScreenFraction)
        {
            desiredHeight = workArea.Height * maxScreenFraction;
            desiredWidth = desiredHeight * ratio;
        }

        if (desiredWidth > workArea.Width * maxScreenFraction)
        {
            desiredWidth = workArea.Width * maxScreenFraction;
            desiredHeight = desiredWidth / ratio;
        }

        desiredWidth = Math.Max(MinWidth, desiredWidth);
        desiredHeight = Math.Max(MinHeight, desiredHeight);

        _isAdjustingSize = true;
        Width = desiredWidth;
        Height = desiredHeight;
        _lastSize = new WindowsSize(Width, Height);
        _isAdjustingSize = false;
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_isAdjustingSize || !_videoAspectRatio.HasValue)
        {
            _lastSize = e.NewSize;
            return;
        }

        var newWidth = e.NewSize.Width;
        var newHeight = e.NewSize.Height;

        if (newWidth <= 0 || newHeight <= 0)
        {
            _lastSize = e.NewSize;
            return;
        }

        var ratio = _videoAspectRatio.Value;
        var workArea = SystemParameters.WorkArea;
        const double maxScreenFraction = 0.95;

        _isAdjustingSize = true;

        var widthDiff = Math.Abs(newWidth - _lastSize.Width);
        var heightDiff = Math.Abs(newHeight - _lastSize.Height);

        if (widthDiff >= heightDiff)
        {
            newHeight = newWidth / ratio;
        }
        else
        {
            newWidth = newHeight * ratio;
        }

        var maxWidth = workArea.Width * maxScreenFraction;
        var maxHeight = workArea.Height * maxScreenFraction;

        if (newWidth > maxWidth)
        {
            newWidth = maxWidth;
            newHeight = newWidth / ratio;
        }

        if (newHeight > maxHeight)
        {
            newHeight = maxHeight;
            newWidth = newHeight * ratio;
        }

        newWidth = Math.Max(MinWidth, newWidth);
        newHeight = Math.Max(MinHeight, newHeight);

        Width = newWidth;
        Height = newHeight;
        _lastSize = new WindowsSize(newWidth, newHeight);

        _isAdjustingSize = false;
    }

    private void OnTimelineSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_isUpdatingTimeline || _isSliderDragging)
        {
            return;
        }

        SeekToSliderValue();
    }

    private void TimelineSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSliderDragging = true;
        _overlayHideTimer.Stop();
        ShowControlsOverlay(autoHide: false);
    }

    private void TimelineSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSliderDragging)
        {
            return;
        }

        _isSliderDragging = false;
        SeekToSliderValue();
        ShowControlsOverlay();
    }

    private void OnTimelineSliderLostMouseCapture(object sender, InputMouseEventArgs e)
    {
        if (!_isSliderDragging)
        {
            return;
        }

        _isSliderDragging = false;
        SeekToSliderValue();
        ShowControlsOverlay();
    }

    private void SeekToSliderValue()
    {
        if (_mediaPlayer is null || !_isSeekable)
        {
            return;
        }

        var target = (float)Math.Clamp(TimelineSlider.Value, TimelineSlider.Minimum, TimelineSlider.Maximum);
        _mediaPlayer.Position = target;
    }

    private void RootGrid_OnMouseEnter(object sender, InputMouseEventArgs e)
    {
        _isPointerOverWindow = true;
        ShowControlsOverlay();
    }

    private void RootGrid_OnMouseMove(object sender, InputMouseEventArgs e)
    {
        _isPointerOverWindow = true;
        ShowControlsOverlay();
    }

    private void RootGrid_OnMouseLeave(object sender, InputMouseEventArgs e)
    {
        _isPointerOverWindow = false;
        if (_isSliderDragging)
        {
            return;
        }

        _overlayHideTimer.Stop();
        HideControlsOverlay(force: true);
    }

    private bool IsOnResizeBorder(InputMouseEventArgs e)
    {
        var chrome = WindowChrome.GetWindowChrome(this);
        var border = chrome?.ResizeBorderThickness ?? new Thickness(8);
        var position = e.GetPosition(this);

        return position.X <= border.Left ||
               position.X >= ActualWidth - border.Right ||
               position.Y <= border.Top ||
               position.Y >= ActualHeight - border.Bottom;
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
