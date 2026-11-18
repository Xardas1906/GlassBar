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
    /// <summary>Der Player erlaubt das Springen im Lied.</summary>
    public bool CanSeek { get; private set; }
    /// <summary>Zeitpunkt, zu dem <see cref="Position"/> galt.</summary>
    public DateTime PositionAt { get; private set; } = DateTime.Now;
    /// <summary>Zählt jeden Sprung und jedes Verwerfen eines Sprungs.</summary>
    public int SeekGeneration => _seekGen;

    // Zeitleiste und laufender Sprung
    private TimeSpan _timelineStart;
    private double? _seekTarget;
    private DateTime _seekAt, _seekUntil;
    private int _seekGen;
    private bool? _wasPlaying;
    private DateTime _playingSince = DateTime.MinValue;

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
        ResetSeek();
        CanSeek = false;
        _wasPlaying = null;
        _playingSince = DateTime.MinValue;
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
        // Immer die aktuelle Sitzung lesen: Meldungen einer abgelösten Sitzung dürfen nichts mehr setzen.
        var current = _session;
        if (current == null) return;
        try
        {
            ReadTimeline(current);
            TimelineChanged?.Invoke();
        }
        catch { }
    });

    /// <summary>Zeitleiste übernehmen; kurz nach einem Sprung bleibt die vorweggenommene Position gegen alte Meldungen stehen.</summary>
    private void ReadTimeline(GlobalSystemMediaTransportControlsSession s)
    {
        var t = s.GetTimelineProperties();
        bool playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var now = DateTime.Now;
        if (playing && _wasPlaying == false) _playingSince = now;
        _wasPlaying = playing;

        var n = MediaTimeline.Normalize(t.StartTime, t.EndTime, t.Position);
        var at = MediaTimeline.SanitizeStamp(t.LastUpdatedTime.LocalDateTime, now, n.Position, n.Duration, playing, _playingSince);
        _timelineStart = n.Start;
        Duration = n.Duration;
        if (_seekTarget is double target)
        {
            double reported = MediaTimeline.Advance(n.Position, at, now, playing, n.Duration);
            if (MediaTimeline.HoldSeek(target, _seekAt, _seekUntil, reported, now, playing, n.Duration, out double expected))
            {
                Position = expected;
                PositionAt = now;
                return;
            }
            _seekTarget = null;
        }
        Position = n.Position;
        PositionAt = at;
    }

    /// <summary>Laufenden Sprung vergessen (anderes Lied, andere Sitzung); späte Antworten zählen nicht mehr.</summary>
    private void ResetSeek()
    {
        _seekTarget = null;
        _seekGen++;
    }

    private async Task UpdateAsync()
    {
        int v = ++_version;
        var s = _session;
        if (s == null)
        {
            HasMedia = false; IsPlaying = false; CanSeek = false; Title = Artist = App = ""; Cover = null;
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

            if ((props?.Title ?? "") != Title || (props?.Artist ?? "") != Artist) ResetSeek();
            Title = props?.Title ?? "";
            Artist = props?.Artist ?? "";
            Cover = cover;
            CoverDataUrl = coverUrl;
            Album = props?.AlbumTitle ?? "";
            IsPaused = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            CanSeek = info.Controls?.IsPlaybackPositionEnabled == true;
            try { ReadTimeline(s); } catch { }
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            HasMedia = !string.IsNullOrWhiteSpace(Title) &&
                       info.PlaybackStatus is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                                           or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused;
            App = s.SourceAppUserModelId ?? "";
        }
        catch
        {
            HasMedia = false;
            CanSeek = false;
        }
        Raise();
    }

    private void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public async void PlayPause() { try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { } }
    public async void Next() { try { if (_session != null) await _session.TrySkipNextAsync(); } catch { } }
    public async void Previous() { try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { } }

    /// <summary>
    /// Im Lied springen (Sekunden ab Liedanfang, nur vom UI-Thread). Die Anzeige folgt sofort; lehnt der Player ab,
    /// springt sie gleich zurück, und bewegt er sich trotz Zusage nicht, nach Ablauf der Frist.
    /// </summary>
    public async void Seek(double seconds)
    {
        var s = _session;
        if (s == null || !CanSeek || Duration <= 0) return;
        double target = Math.Clamp(seconds, 0, Duration);
        double oldPosition = Position;
        var oldAt = PositionAt;
        int gen = ++_seekGen;
        var now = DateTime.Now;
        var until = now.AddSeconds(MediaTimeline.HoldSeconds);
        _seekTarget = target; _seekAt = now; _seekUntil = until;
        Position = target; PositionAt = now;
        TimelineChanged?.Invoke();

        bool ok = false;
        try { ok = await s.TryChangePlaybackPositionAsync(MediaTimeline.SeekTicks(_timelineStart, target)); } catch { }
        if (gen != _seekGen) return; // inzwischen neuer Sprung oder anderes Lied
        if (!ok)
        {
            Position = oldPosition; PositionAt = oldAt;
            EndSeek(s);
            return;
        }

        var wait = until - DateTime.Now + TimeSpan.FromMilliseconds(50);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        if (gen == _seekGen && _seekTarget != null) EndSeek(s);
    }

    private void EndSeek(GlobalSystemMediaTransportControlsSession s)
    {
        _seekTarget = null;
        try { ReadTimeline(s); } catch { }
        TimelineChanged?.Invoke();
    }
}
