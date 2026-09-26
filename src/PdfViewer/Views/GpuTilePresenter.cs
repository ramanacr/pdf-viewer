using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PdfEngine.Vector.Direct2D;

namespace PdfViewer.Views;

/// <summary>
/// Shows Direct2D output without copying it through the CPU: a shared Direct3D 11 texture is
/// opened as a Direct3D 9Ex surface and handed to a WPF <see cref="D3DImage"/>, which WPF
/// composes on the GPU. Used for viewport detail tiles, the largest images the viewer draws
/// (≈ 10 MP at high zoom). Only on a hardware render tier and a local session; everywhere else
/// the tile arrives as a bitmap, as before. UI thread only.
/// </summary>
internal sealed class GpuTilePresenter : IDisposable
{
    private readonly D3D9ExInterop _d3d9;

    private GpuTilePresenter(D3D9ExInterop d3d9) => _d3d9 = d3d9;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);
    private const int SM_REMOTESESSION = 0x1000;

    /// <summary>The presenter, or null where the bitmap path is the right one (software tier, RDP, opt-out).</summary>
    public static GpuTilePresenter? TryCreate(Window window)
    {
        if (Environment.GetEnvironmentVariable("PDF_GPU_PRESENT") == "0")
            return null;
        if ((RenderCapability.Tier >> 16) < 2 || GetSystemMetrics(SM_REMOTESESSION) != 0)
            return null;
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        var d3d9 = D3D9ExInterop.TryCreate(hwnd);
        return d3d9 == null ? null : new GpuTilePresenter(d3d9);
    }

    /// <summary>
    /// An image showing <paramref name="texture"/>, and what keeps it alive; disposing the
    /// lifetime detaches the surface and releases the texture. <paramref name="onLost"/> runs when
    /// the display device goes away (lock screen, driver reset): the caller drops the tile and
    /// the page bitmap shows until the next one.
    /// </summary>
    public (ImageSource Image, IDisposable Lifetime)? Present(SharedTexture texture, Action onLost)
    {
        var surface = _d3d9.OpenShared(texture.SharedHandle, texture.Width, texture.Height);
        if (surface == null)
            return null;
        var image = new D3DImage(96, 96);
        bool locked = false;
        try
        {
            image.Lock();
            locked = true;
            // Software fallback on: if WPF drops to software rendering the surface is copied, not lost.
            image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, surface.Surface, enableSoftwareFallback: true);
            image.AddDirtyRect(new Int32Rect(0, 0, texture.Width, texture.Height));
        }
        catch (Exception)
        {
            // The caller falls back to a bitmap tile.
            if (locked) image.Unlock();
            locked = false;
            surface.Dispose();
            return null;
        }
        finally
        {
            if (locked) image.Unlock();
        }
        DependencyPropertyChangedEventHandler lost = (_, e) =>
        {
            if (e.NewValue is false) onLost();
        };
        image.IsFrontBufferAvailableChanged += lost;
        return (image, new Lifetime(image, surface, texture, lost));
    }

    private sealed class Lifetime : IDisposable
    {
        private D3DImage? _image;
        private readonly SharedSurface9 _surface;
        private readonly SharedTexture _texture;
        private readonly DependencyPropertyChangedEventHandler _lost;

        public Lifetime(D3DImage image, SharedSurface9 surface, SharedTexture texture, DependencyPropertyChangedEventHandler lost)
        {
            _image = image;
            _surface = surface;
            _texture = texture;
            _lost = lost;
        }

        public void Dispose()
        {
            if (_image == null) return;
            var image = _image;
            _image = null;
            image.IsFrontBufferAvailableChanged -= _lost;
            bool locked = false;
            try
            {
                // Detach before releasing, so WPF never composes a released surface.
                image.Lock();
                locked = true;
                image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
            }
            finally
            {
                if (locked) image.Unlock();
                // Released whatever happened above: a failed detach must not leak GPU memory.
                _surface.Dispose();
                _texture.Dispose();
            }
        }
    }

    public void Dispose() => _d3d9.Dispose();
}
