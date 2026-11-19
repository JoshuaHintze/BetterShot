using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace BetterShot.Recording;

/// <summary>
/// The handful of raw Direct3D 11 / DXGI / WinRT-interop calls screen recording needs, as hand-written COM vtable
/// calls (no extra packages). Vtable slot numbers follow the d3d11.h / dxgi.h / windows.graphics.capture.interop.h
/// declaration order. Every returned COM pointer is owned by the caller and must be released with <see cref="Release"/>.
/// </summary>
internal static unsafe class Direct3D11Interop
{
    internal const uint DxgiFormatB8G8R8A8Unorm = 87;
    private const int DriverTypeHardware = 1, DriverTypeWarp = 5;
    private const uint CreateDeviceBgraSupport = 0x20;
    private const uint SdkVersion = 7;
    private const uint BindShaderResource = 0x8, BindRenderTarget = 0x20;

    // ID3D11Device: IUnknown(0-2), CreateBuffer(3), CreateTexture1D(4), CreateTexture2D(5).
    private const int DeviceCreateTexture2D = 5;
    // ID3D11DeviceContext: IUnknown(0-2), ID3D11DeviceChild(3-6), ... CopySubresourceRegion(46), CopyResource(47), Flush(111).
    private const int ContextCopySubresourceRegion = 46;
    private const int ContextCopyResource = 47;
    private const int ContextFlush = 111;
    // ID3D10Multithread: IUnknown(0-2), Enter(3), Leave(4), SetMultithreadProtected(5).
    private const int MultithreadSetProtected = 5;
    // IDirect3DDxgiInterfaceAccess: IUnknown(0-2), GetInterface(3).
    private const int DxgiAccessGetInterface = 3;
    // IGraphicsCaptureItemInterop: IUnknown(0-2), CreateForWindow(3), CreateForMonitor(4).
    private const int InteropCreateForWindow = 3, InteropCreateForMonitor = 4;

    private static readonly Guid IidDxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    private static readonly Guid IidDxgiSurface = new("cafcb56c-6ac3-4889-bf47-9e23bbd260ec");
    private static readonly Guid IidTexture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
    private static readonly Guid IidDxgiInterfaceAccess = new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1");
    private static readonly Guid IidMultithread = new("9b7e4e00-342c-4106-a19f-4f2704f689f0");
    private static readonly Guid IidGraphicsCaptureItemInterop = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");
    private static readonly Guid IidGraphicsCaptureItem = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");
    private const string GraphicsCaptureItemClass = "Windows.Graphics.Capture.GraphicsCaptureItem";

    [StructLayout(LayoutKind.Sequential)]
    private struct Texture2DDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Box { public uint Left, Top, Front, Right, Bottom, Back; }

    /// <summary>Creates a BGRA-capable hardware device (WARP fallback). Returns owned device and immediate-context pointers.</summary>
    internal static (IntPtr Device, IntPtr Context) CreateDevice()
    {
        int result = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out IntPtr device, out _, out IntPtr context);
        if (result < 0)
            result = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out device, out _, out context);
        Marshal.ThrowExceptionForHR(result);
        EnableMultithreadProtection(device);
        return (device, context);
    }

    /// <summary>The encoder and capture use the device from other threads; serialize the immediate context.</summary>
    private static void EnableMultithreadProtection(IntPtr device)
    {
        if (Marshal.QueryInterface(device, in IidMultithread, out IntPtr multithread) < 0 || multithread == IntPtr.Zero) return;
        try { ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(multithread, MultithreadSetProtected))(multithread, 1); }
        finally { Marshal.Release(multithread); }
    }

    /// <summary>Wraps the D3D11 device as the WinRT IDirect3DDevice used by capture frame pools.</summary>
    internal static IDirect3DDevice CreateWinRtDevice(IntPtr device)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IidDxgiDevice, out IntPtr dxgiDevice));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out IntPtr inspectable));
            try { return MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(dxgiDevice); }
    }

    /// <summary>Creates a GPU-only BGRA texture usable as encoder input. Returns an owned ID3D11Texture2D pointer.</summary>
    internal static IntPtr CreateTexture(IntPtr device, int width, int height)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = DxgiFormatB8G8R8A8Unorm,
            SampleCount = 1, SampleQuality = 0, Usage = 0, BindFlags = BindShaderResource | BindRenderTarget
        };
        IntPtr texture;
        int result = ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDescription*, IntPtr, IntPtr*, int>)Slot(device, DeviceCreateTexture2D))(device, &description, IntPtr.Zero, &texture);
        Marshal.ThrowExceptionForHR(result);
        return texture;
    }

    internal static void CopyRegion(IntPtr context, IntPtr destination, int destinationX, int destinationY, IntPtr source, Int32Rect sourceBox)
    {
        var box = new Box
        {
            Left = (uint)sourceBox.X, Top = (uint)sourceBox.Y, Front = 0,
            Right = (uint)(sourceBox.X + sourceBox.Width), Bottom = (uint)(sourceBox.Y + sourceBox.Height), Back = 1
        };
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(context, ContextCopySubresourceRegion))(
            context, destination, 0, (uint)destinationX, (uint)destinationY, 0, source, 0, &box);
    }

    internal static void CopyResource(IntPtr context, IntPtr destination, IntPtr source) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(context, ContextCopyResource))(context, destination, source);

    internal static void Flush(IntPtr context) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)Slot(context, ContextFlush))(context);

    /// <summary>Wraps a texture as a WinRT IDirect3DSurface (adds its own reference; the caller still releases the texture).</summary>
    internal static IDirect3DSurface CreateSurface(IntPtr texture)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(texture, in IidDxgiSurface, out IntPtr dxgiSurface));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11SurfaceFromDXGISurface(dxgiSurface, out IntPtr inspectable));
            try { return MarshalInterface<IDirect3DSurface>.FromAbi(inspectable); }
            finally { Marshal.Release(inspectable); }
        }
        finally { Marshal.Release(dxgiSurface); }
    }

    /// <summary>Returns the owned ID3D11Texture2D behind a capture frame surface.</summary>
    internal static IntPtr TextureFromSurface(IDirect3DSurface surface)
    {
        IntPtr native = MarshalInterface<IDirect3DSurface>.FromManaged(surface);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native, in IidDxgiInterfaceAccess, out IntPtr access));
            try
            {
                Guid iid = IidTexture2D;
                IntPtr texture;
                int result = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(access, DxgiAccessGetInterface))(access, &iid, &texture);
                Marshal.ThrowExceptionForHR(result);
                return texture;
            }
            finally { Marshal.Release(access); }
        }
        finally { Marshal.Release(native); }
    }

    internal static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor) => CreateItem(monitor, InteropCreateForMonitor);

    internal static GraphicsCaptureItem CreateItemForWindow(IntPtr window) => CreateItem(window, InteropCreateForWindow);

    private static GraphicsCaptureItem CreateItem(IntPtr handle, int slot)
    {
        IntPtr factory = GetCaptureItemInterop();
        try
        {
            Guid iid = IidGraphicsCaptureItem;
            IntPtr item;
            int result = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(factory, slot))(factory, handle, &iid, &item);
            Marshal.ThrowExceptionForHR(result);
            try { return GraphicsCaptureItem.FromAbi(item); }
            finally { Marshal.Release(item); }
        }
        finally { Marshal.Release(factory); }
    }

    private static IntPtr GetCaptureItemInterop()
    {
        Marshal.ThrowExceptionForHR(WindowsCreateString(GraphicsCaptureItemClass, GraphicsCaptureItemClass.Length, out IntPtr className));
        try
        {
            Guid iid = IidGraphicsCaptureItemInterop;
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(className, &iid, out IntPtr factory));
            return factory;
        }
        finally { WindowsDeleteString(className); }
    }

    internal static void Release(IntPtr comObject)
    {
        if (comObject != IntPtr.Zero) Marshal.Release(comObject);
    }

    private static IntPtr Slot(IntPtr comObject, int index) => (*(IntPtr**)comObject)[index];

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11SurfaceFromDXGISurface(IntPtr dxgiSurface, out IntPtr graphicsSurface);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, Guid* iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
}
