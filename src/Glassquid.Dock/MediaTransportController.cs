using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Glassquid.Dock;

/// <summary>
/// Snapshot of current audio or video playback info from Windows GlobalSystemMediaTransportControls.
/// </summary>
internal sealed class MediaTrackInfo
{
    public bool HasMedia { get; init; }
    public string Title { get; init; } = "Đang phát";
    public string Artist { get; init; } = "Không có nội dung";
    public bool IsPlaying { get; init; }
    public ImageSource? Thumbnail { get; init; }
    public bool CanPlayPause { get; init; } = true;
    public bool CanSkipPrevious { get; init; } = true;
    public bool CanSkipNext { get; init; } = true;
}

/// <summary>
/// Monitors and controls system audio/video playback (Spotify, YouTube, Chrome, Media Player, etc.)
/// using Windows System Media Transport Controls (GSMTC).
/// </summary>
internal sealed class MediaTransportController : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _sessionManager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _disposed;

    public event Action<MediaTrackInfo>? MediaChanged;
    public MediaTrackInfo CurrentTrack { get; private set; } = new();

    public async Task InitializeAsync()
    {
        try
        {
            _sessionManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_sessionManager != null)
            {
                _sessionManager.CurrentSessionChanged += OnCurrentSessionChanged;
                _sessionManager.SessionsChanged += OnSessionsChanged;
                await RefreshMediaAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] Failed to init GSMTC: {ex.Message}");
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _ = RefreshMediaAsync();
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        _ = RefreshMediaAsync();
    }

    private void SubscribeToSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
        }

        _currentSession = session;

        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged += OnPlaybackInfoChanged;
            }
            catch { }
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        _ = RefreshMediaAsync();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        _ = RefreshMediaAsync();
    }

    public async Task RefreshMediaAsync()
    {
        if (_disposed || _sessionManager == null) return;

        try
        {
            var sessions = _sessionManager.GetSessions();
            var session = _sessionManager.GetCurrentSession()
                       ?? (sessions.Count > 0 ? sessions[0] : null);

            SubscribeToSession(session);

            if (session == null)
            {
                CurrentTrack = new MediaTrackInfo
                {
                    HasMedia = false,
                    Title = "Đang phát",
                    Artist = "Không có nội dung",
                    IsPlaying = false,
                    Thumbnail = null,
                };
                MediaChanged?.Invoke(CurrentTrack);
                return;
            }

            var mediaProperties = await session.TryGetMediaPropertiesAsync();
            var playbackInfo = session.GetPlaybackInfo();

            bool isPlaying = playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            string title = mediaProperties?.Title?.Trim() ?? string.Empty;
            string artist = mediaProperties?.Artist?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(mediaProperties?.AlbumArtist))
            {
                artist = mediaProperties.AlbumArtist.Trim();
            }
            if (string.IsNullOrEmpty(artist) && !string.IsNullOrEmpty(session.SourceAppUserModelId))
            {
                artist = CleanAppId(session.SourceAppUserModelId);
            }

            if (string.IsNullOrEmpty(title))
            {
                title = isPlaying ? "Đang phát" : "Đã tạm dừng";
            }
            if (string.IsNullOrEmpty(artist))
            {
                artist = isPlaying ? "Đang phát nội dung" : "Không có nội dung";
            }

            ImageSource? thumbnail = null;
            if (mediaProperties?.Thumbnail != null)
            {
                thumbnail = await LoadThumbnailAsync(mediaProperties.Thumbnail);
            }

            var controls = playbackInfo?.Controls;
            bool canPlayPause = controls?.IsPlayPauseToggleEnabled == true ||
                                controls?.IsPlayEnabled == true ||
                                controls?.IsPauseEnabled == true;
            bool canPrev = controls?.IsPreviousEnabled == true;
            bool canNext = controls?.IsNextEnabled == true;

            CurrentTrack = new MediaTrackInfo
            {
                HasMedia = true,
                Title = title,
                Artist = artist,
                IsPlaying = isPlaying,
                Thumbnail = thumbnail,
                CanPlayPause = canPlayPause,
                CanSkipPrevious = canPrev,
                CanSkipNext = canNext,
            };

            MediaChanged?.Invoke(CurrentTrack);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] RefreshMediaAsync error: {ex.Message}");
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession == null) return;
        try
        {
            var playbackInfo = _currentSession.GetPlaybackInfo();
            if (playbackInfo?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                await _currentSession.TryPauseAsync();
            }
            else
            {
                await _currentSession.TryPlayAsync();
            }
            await RefreshMediaAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] TogglePlayPause error: {ex.Message}");
        }
    }

    public async Task SkipPreviousAsync()
    {
        if (_currentSession == null) return;
        try
        {
            await _currentSession.TrySkipPreviousAsync();
            await RefreshMediaAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] SkipPrevious error: {ex.Message}");
        }
    }

    public async Task SkipNextAsync()
    {
        if (_currentSession == null) return;
        try
        {
            await _currentSession.TrySkipNextAsync();
            await RefreshMediaAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] SkipNext error: {ex.Message}");
        }
    }

    private static async Task<BitmapImage?> LoadThumbnailAsync(IRandomAccessStreamReference? thumbnailRef)
    {
        if (thumbnailRef == null) return null;
        try
        {
            using var stream = await thumbnailRef.OpenReadAsync();
            if (stream == null || stream.Size == 0) return null;

            var bytes = new byte[stream.Size];
            using (var reader = new DataReader(stream.GetInputStreamAt(0)))
            {
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
            }

            var ms = new MemoryStream(bytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Glassquid Media] LoadThumbnailAsync error: {ex.Message}");
            return null;
        }
    }

    private static string CleanAppId(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return string.Empty;
        var lower = appId.ToLowerInvariant();
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("chrome")) return "Google Chrome";
        if (lower.Contains("msedge") || lower.Contains("edge")) return "Microsoft Edge";
        if (lower.Contains("firefox")) return "Firefox";
        if (lower.Contains("zunemusic") || lower.Contains("media player")) return "Media Player";
        if (lower.Contains("applemusic")) return "Apple Music";
        if (lower.Contains("vlc")) return "VLC";
        if (lower.Contains("foobar2000")) return "foobar2000";

        var name = Path.GetFileNameWithoutExtension(appId);
        if (!string.IsNullOrEmpty(name)) return name;
        return appId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_sessionManager != null)
        {
            try
            {
                _sessionManager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _sessionManager.SessionsChanged -= OnSessionsChanged;
            }
            catch { }
        }
        if (_currentSession != null)
        {
            try
            {
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _currentSession.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            }
            catch { }
        }
    }
}
