using System.ComponentModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace GlassBar;

/// <summary>
/// Liest über Windows, was gerade läuft (Spotify, Browser, …), samt Cover, und steuert die Wiedergabe.
/// Dieselbe Quelle, die auch die Windows-Mediensteuerung und der Hintergrund „Still“ nutzen.
/// </summary>
public class MediaWatcher : INotifyPropertyChanged
{
    private readonly Dispatcher _ui;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private int _version;

    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public ImageSource? Cover { get; private set; }
    public bool HasMedia { get; private set; }
    public bool IsPlaying { get; private set; }
    public string App { get; private set; } = "";
    public string Album { get; private set; } = "";
    /// <summary>Cover als data-URL (für den Hintergrund).</summary>
    public string CoverDataUrl { get; private set; } = "";
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public bool IsPaused { get; private set; }

    /// <summary>Nur die Abspielposition hat sich geändert.</summary>
    public event Action? TimelineChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MediaWatcher(Dispatcher ui) => _ui = ui;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += (_, _) => _ui.BeginInvoke(() => Hook(_manager.GetCurrentSession()));
            Hook(_manager.GetCurrentSession());
        }
        catch { /* Mediensteuerung nicht verfügbar */ }
    }

    private void Hook(GlobalSystemMediaTransportControlsSession? s)
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnChanged;
            _session.PlaybackInfoChanged -= OnChanged;
            _session.TimelinePropertiesChanged -= OnTimeline;
        }
        _session = s;
        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnChanged;
            _session.PlaybackInfoChanged += OnChanged;
            _session.TimelinePropertiesChanged += OnTimeline;
        }
        _ = UpdateAsync();
    }

    private void OnChanged(GlobalSystemMediaTransportControlsSession s, object e) => _ui.BeginInvoke(() => _ = UpdateAsync());

    private void OnTimeline(GlobalSystemMediaTransportControlsSession s, object e) => _ui.BeginInvoke(() =>
    {
        try
        {
            var t = s.GetTimelineProperties();
            Position = t.Position.TotalSeconds;
            Duration = (t.EndTime - t.StartTime).TotalSeconds;
            TimelineChanged?.Invoke();
        }
        catch { }
    });

    private async Task UpdateAsync()
    {
        int v = ++_version;
        var s = _session;
        if (s == null)
        {
            HasMedia = false; IsPlaying = false; Title = Artist = App = ""; Cover = null;
            Raise();
            return;
        }
        try
        {
            var info = s.GetPlaybackInfo();
            var props = await s.TryGetMediaPropertiesAsync();
            ImageSource? cover = null;
            string coverUrl = "";
            if (props?.Thumbnail != null)
            {
                using var ras = await props.Thumbnail.OpenReadAsync();
                using var stream = ras.AsStreamForRead();
                var mem = new MemoryStream();
                await stream.CopyToAsync(mem);
                var mime = string.IsNullOrEmpty(ras.ContentType) ? "image/png" : ras.ContentType;
                coverUrl = $"data:{mime};base64,{Convert.ToBase64String(mem.ToArray())}";
                mem.Position = 0;
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 96;
                bmp.StreamSource = mem;
                bmp.EndInit();
                bmp.Freeze();
                cover = bmp;
            }
            if (v != _version) return; // inzwischen neuere Daten

            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            Cover = cover;
            CoverDataUrl = coverUrl;
            Album = props?.AlbumTitle ?? "";
            IsPaused = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            try { var t = s.GetTimelineProperties(); Position = t.Position.TotalSeconds; Duration = (t.EndTime - t.StartTime).TotalSeconds; } catch { }
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            HasMedia = !string.IsNullOrWhiteSpace(Title) &&
                       info.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                                           or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            App = s.SourceAppUserModelId ?? "";
        }
        catch
        {
            HasMedia = false;
        }
        Raise();
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }
}
