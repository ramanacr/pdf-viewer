using System.Runtime.InteropServices;

namespace PdfEngine.Vector.Direct2D;

/// <summary>
/// The minimum of Direct3D 9Ex a WPF <c>D3DImage</c> needs: a device, and a texture opened from a
/// Direct3D 11 shared handle whose level-0 surface becomes the image's back buffer. WPF composes
/// that surface directly, so a page rendered by Direct2D reaches the screen without the GPU →
/// CPU → WPF bitmap → GPU round trip. Called through the COM vtables (four methods), so no
/// Direct3D 9 binding package is needed. Not thread-bound, but WPF's D3DImage calls are.
/// </summary>
public sealed unsafe class D3D9ExInterop : IDisposable
{
    private const uint D3D_SDK_VERSION = 32;
    private const int D3DDEVTYPE_HAL = 1;
    private const uint D3DCREATE_FPU_PRESERVE = 0x02, D3DCREATE_MULTITHREADED = 0x04, D3DCREATE_HARDWARE_VERTEXPROCESSING = 0x40;
    private const uint D3DUSAGE_RENDERTARGET = 0x01;
    private const uint D3DFMT_A8R8G8B8 = 21;
    private const uint D3DPOOL_DEFAULT = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DPRESENT_PARAMETERS
    {
        public uint BackBufferWidth, BackBufferHeight, BackBufferFormat, BackBufferCount;
        public uint MultiSampleType, MultiSampleQuality, SwapEffect;
        public IntPtr hDeviceWindow;
        public int Windowed, EnableAutoDepthStencil;
        public uint AutoDepthStencilFormat, Flags, FullScreen_RefreshRateInHz, PresentationInterval;
    }

    [DllImport("d3d9.dll")]
    private static extern int Direct3DCreate9Ex(uint sdkVersion, out IntPtr d3d9Ex);

    private IntPtr _d3d;
    private IntPtr _device;

    private D3D9ExInterop(IntPtr d3d, IntPtr device)
    {
        _d3d = d3d;
        _device = device;
    }

    /// <summary>A Direct3D 9Ex device on the default adapter (the one Direct2D uses), or null.</summary>
    public static D3D9ExInterop? TryCreate(IntPtr hwnd)
    {
        try
        {
            if (Direct3DCreate9Ex(D3D_SDK_VERSION, out var d3d) < 0 || d3d == IntPtr.Zero)
                return null;
            var pp = new D3DPRESENT_PARAMETERS
            {
                BackBufferWidth = 1, BackBufferHeight = 1, BackBufferCount = 1,
                SwapEffect = 1 /* DISCARD */, hDeviceWindow = hwnd, Windowed = 1,
                PresentationInterval = 0x80000000 /* IMMEDIATE */,
            };
            // IDirect3D9Ex::CreateDeviceEx is vtable slot 20.
            var createDeviceEx = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, IntPtr, uint, D3DPRESENT_PARAMETERS*, void*, IntPtr*, int>)
                (*(void***)d3d)[20];
            IntPtr device;
            int hr = createDeviceEx(d3d, 0, D3DDEVTYPE_HAL, hwnd,
                D3DCREATE_HARDWARE_VERTEXPROCESSING | D3DCREATE_MULTITHREADED | D3DCREATE_FPU_PRESERVE, &pp, null, &device);
            if (hr < 0 || device == IntPtr.Zero)
            {
                Marshal.Release(d3d);
                return null;
            }
            return new D3D9ExInterop(d3d, device);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Opens a Direct3D 11 shared texture (B8G8R8A8) as a Direct3D 9 texture. The returned surface
    /// pointer is what <c>D3DImage.SetBackBuffer</c> takes; dispose the result after the image
    /// stops showing it.
    /// </summary>
    public SharedSurface9? OpenShared(IntPtr sharedHandle, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_device == IntPtr.Zero, this);
        // IDirect3DDevice9::CreateTexture is slot 23; IDirect3DTexture9::GetSurfaceLevel slot 18.
        var createTexture = (delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint, uint, uint, uint, IntPtr*, IntPtr*, int>)
            (*(void***)_device)[23];
        IntPtr texture;
        IntPtr handle = sharedHandle;
        int hr = createTexture(_device, (uint)width, (uint)height, 1, D3DUSAGE_RENDERTARGET, D3DFMT_A8R8G8B8, D3DPOOL_DEFAULT, &texture, &handle);
        if (hr < 0 || texture == IntPtr.Zero)
            return null;
        var getSurfaceLevel = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(void***)texture)[18];
        IntPtr surface;
        hr = getSurfaceLevel(texture, 0, &surface);
        if (hr < 0 || surface == IntPtr.Zero)
        {
            Marshal.Release(texture);
            return null;
        }
        return new SharedSurface9(texture, surface);
    }

    public void Dispose()
    {
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
        if (_d3d != IntPtr.Zero) { Marshal.Release(_d3d); _d3d = IntPtr.Zero; }
    }
}

/// <summary>A Direct3D 9 texture opened from a shared handle, and its level-0 surface.</summary>
public sealed class SharedSurface9 : IDisposable
{
    private IntPtr _texture;
    public IntPtr Surface { get; private set; }

    internal SharedSurface9(IntPtr texture, IntPtr surface)
    {
        _texture = texture;
        Surface = surface;
    }

    public void Dispose()
    {
        if (Surface != IntPtr.Zero) { Marshal.Release(Surface); Surface = IntPtr.Zero; }
        if (_texture != IntPtr.Zero) { Marshal.Release(_texture); _texture = IntPtr.Zero; }
    }
}
