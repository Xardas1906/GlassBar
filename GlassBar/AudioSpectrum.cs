using NAudio.Wave;

namespace GlassBar;

/// <summary>
/// Hört mit, was am PC abgespielt wird (Loopback), und liefert wie Wallpaper Engine 128 Werte:
/// 64 Frequenzbänder links, 64 rechts, jeweils etwa 0–1. Damit pulsiert das Licht im Hintergrund im Takt.
/// </summary>
public sealed class AudioSpectrum : IDisposable
{
    private const int N = 1024;               // FFT-Größe
    private readonly float[] _left = new float[N], _right = new float[N];
    private int _pos;
    private readonly object _lock = new();
    private WasapiLoopbackCapture? _capture;
    private readonly float[] _smooth = new float[128];

    public void Start()
    {
        try
        {
            _capture = new WasapiLoopbackCapture();
            _capture.DataAvailable += OnData;
            _capture.StartRecording();
        }
        catch { _capture = null; }
    }

    private void OnData(object? s, WaveInEventArgs e)
    {
        var fmt = _capture!.WaveFormat;
        if (fmt.Encoding != WaveFormatEncoding.IeeeFloat && fmt.Encoding != WaveFormatEncoding.Extensible) return;
        int ch = fmt.Channels, frames = e.BytesRecorded / (4 * ch);
        lock (_lock)
        {
            for (int i = 0; i < frames; i++)
            {
                float l = BitConverter.ToSingle(e.Buffer, (i * ch) * 4);
                float r = ch > 1 ? BitConverter.ToSingle(e.Buffer, (i * ch + 1) * 4) : l;
                _left[_pos] = l; _right[_pos] = r;
                _pos = (_pos + 1) % N;
            }
        }
    }

    /// <summary>Aktuelles Spektrum (128 Werte) für den Hintergrund.</summary>
    public float[] Read()
    {
        var l = new float[N]; var r = new float[N];
        lock (_lock)
        {
            for (int i = 0; i < N; i++) { int k = (_pos + i) % N; l[i] = _left[k]; r[i] = _right[k]; }
        }
        var bl = Bands(l); var br = Bands(r);
        var outv = new float[128];
        for (int i = 0; i < 64; i++) { outv[i] = bl[i]; outv[64 + i] = br[i]; }
        for (int i = 0; i < 128; i++)
        {
            // schnell hoch, langsam runter – wie Wallpaper Engine
            _smooth[i] = outv[i] > _smooth[i] ? outv[i] : _smooth[i] * 0.82f + outv[i] * 0.18f;
            outv[i] = _smooth[i];
        }
        return outv;
    }

    private static float[] Bands(float[] samples)
    {
        var re = new double[N]; var im = new double[N];
        for (int i = 0; i < N; i++) re[i] = samples[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (N - 1))); // Hann-Fenster
        Fft(re, im);

        var bands = new float[64];
        const double sampleRate = 48000, fMin = 30, fMax = 16000;
        for (int b = 0; b < 64; b++)
        {
            double f0 = fMin * Math.Pow(fMax / fMin, b / 64.0), f1 = fMin * Math.Pow(fMax / fMin, (b + 1) / 64.0);
            int k0 = Math.Max(1, (int)(f0 / sampleRate * N)), k1 = Math.Max(k0 + 1, (int)(f1 / sampleRate * N));
            double sum = 0;
            for (int k = k0; k < k1 && k < N / 2; k++) sum += Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
            double mag = sum / (k1 - k0) / (N / 4.0);
            // logarithmisch auf 0..1 (ca. -60 dB … 0 dB)
            double db = 20 * Math.Log10(mag + 1e-9);
            bands[b] = (float)Math.Clamp((db + 60) / 60, 0, 1.2);
        }
        return bands;
    }

    private static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j, b = a + len / 2;
                    double xr = re[b] * cr - im[b] * ci, xi = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - xr; im[b] = im[a] - xi;
                    re[a] += xr; im[a] += xi;
                    double t = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = t;
                }
            }
        }
    }

    public void Dispose()
    {
        try { _capture?.StopRecording(); _capture?.Dispose(); } catch { }
        _capture = null;
    }
}
