using System.Runtime.InteropServices;

namespace Glassquid.Dock;

/// <summary>
/// Controls the screen color temperature effect using the Windows Magnification API.
/// This directly alters the display color matrix at the DWM compositor level,
/// providing instant, flicker-free blue light reduction (Night light warmth).
/// </summary>
internal static class NightLightDisplayEffect
{
    private static readonly object Sync = new();
    private static bool _isInitialized;
    private static bool _isActive;
    private static int _currentStrength = 50;

    [DllImport("Magnification.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool MagInitialize();

    [DllImport("Magnification.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool MagUninitialize();

    [DllImport("Magnification.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);

    [StructLayout(LayoutKind.Sequential)]
    private struct MAGCOLOREFFECT
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
        public float[] transform;
    }

    private static readonly MAGCOLOREFFECT IdentityEffect = new()
    {
        transform = new float[25]
        {
            1.0f, 0.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 1.0f, 0.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 1.0f, 0.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 1.0f, 0.0f,
            0.0f, 0.0f, 0.0f, 0.0f, 1.0f
        }
    };

    static NightLightDisplayEffect()
    {
        try
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
        }
        catch { /* best effort */ }
    }

    public static bool IsActive
    {
        get
        {
            lock (Sync) return _isActive;
        }
    }

    public static int CurrentStrength
    {
        get
        {
            lock (Sync) return _currentStrength;
        }
    }

    public static void Apply(int strength)
    {
        lock (Sync)
        {
            strength = Math.Clamp(strength, 0, 100);
            _currentStrength = strength;
            _isActive = true;

            var (r, g, b) = StrengthToRgb(strength);

            var effect = new MAGCOLOREFFECT
            {
                transform = new float[25]
                {
                    r,    0.0f, 0.0f, 0.0f, 0.0f,
                    0.0f, g,    0.0f, 0.0f, 0.0f,
                    0.0f, 0.0f, b,    0.0f, 0.0f,
                    0.0f, 0.0f, 0.0f, 1.0f, 0.0f,
                    0.0f, 0.0f, 0.0f, 0.0f, 1.0f
                }
            };

            EnsureInitialized();
            MagSetFullscreenColorEffect(ref effect);
        }
    }

    public static void Restore()
    {
        lock (Sync)
        {
            if (_isInitialized || _isActive)
            {
                var identity = IdentityEffect;
                MagSetFullscreenColorEffect(ref identity);
                if (_isInitialized)
                {
                    MagUninitialize();
                    _isInitialized = false;
                }
            }
            _isActive = false;
        }
    }

    private static void EnsureInitialized()
    {
        if (!_isInitialized)
        {
            _isInitialized = MagInitialize();
        }
    }

    public static (float r, float g, float b) StrengthToRgb(int strength)
    {
        if (strength <= 0) return (1.0f, 1.0f, 1.0f);
        var kelvin = 6500.0 - (strength * 53.0);
        var temp = kelvin / 100.0;

        double r = 255.0;
        if (temp > 66.0)
        {
            r = 329.698727446 * Math.Pow(temp - 60.0, -0.1332047592);
            r = Math.Clamp(r, 0.0, 255.0);
        }

        double g;
        if (temp <= 66.0)
        {
            g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
        }
        else
        {
            g = 288.1221695283 * Math.Pow(temp - 60.0, -0.0755148492);
        }
        g = Math.Clamp(g, 0.0, 255.0);

        double b;
        if (temp >= 66.0)
        {
            b = 255.0;
        }
        else if (temp <= 19.0)
        {
            b = 0.0;
        }
        else
        {
            b = 138.5177312231 * Math.Log(temp - 10.0) - 305.0447927307;
        }
        b = Math.Clamp(b, 0.0, 255.0);

        return ((float)(r / 255.0), (float)(g / 255.0), (float)(b / 255.0));
    }
}
