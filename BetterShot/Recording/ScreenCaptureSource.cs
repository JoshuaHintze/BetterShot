using System.Runtime.InteropServices;
using System.Threading;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace BetterShot.Recording;

/// <summary>
/// Windows.Graphics.Capture session for a monitor or window. Every arriving frame is cropped (CopySubresourceRegion)
/// into a fixed-size "latest" texture; the encoder snapshots that texture at its own pace, so static screens still
/// produce a steady frame rate and window resizes never change the output size.
/// </summary>
internal sealed class ScreenCaptureSource : IDisposable
{
    private readonly object _gate = new();
    private readonly IntPtr _device;
    private readonly IntPtr _context;
    private readonly IDirect3DDevice _winRtDevice;
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _framePool;
    private readonly GraphicsCaptureSession _session;
    private readonly Int32Rect? _crop;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr _latest;
    private SizeInt32 _poolSize;
    private bool _disposed;

    /// <param name="crop">Monitor-local crop box (even size), or null to keep the whole frame.</param>
    internal ScreenCaptureSource(GraphicsCaptureItem item, Int32Rect? crop, int outputWidth, int outputHeight)
    {
        _item = item;
        _crop = crop;
        Width = outputWidth;
        Height = outputHeight;
        (_device, _context) = Direct3D11Interop.CreateDevice();
        try
        {
            _winRtDevice = Direct3D11Interop.CreateWinRtDevice(_device);
            _latest = Direct3D11Interop.CreateTexture(_device, outputWidth, outputHeight);
            _poolSize = item.Size;
            _framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(_winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, _poolSize);
            _framePool.FrameArrived += FramePool_FrameArrived;
            _session = _framePool.CreateCaptureSession(item);
            ConfigureSession(_session);
            _item.Closed += Item_Closed;
        }
        catch
        {
            ReleaseNative();
            throw;
        }
    }

    internal int Width { get; }
    internal int Height { get; }
    internal bool HasFrame => _firstFrame.Task.IsCompleted;
    internal Exception? Failure { get; private set; }

    /// <summary>Raised (on a capture thread) when the captured window closes or the display disappears.</summary>
    internal event EventHandler? SourceClosed;

    internal void Start() => _session.StartCapture();

    /// <summary>Waits for the first frame (Windows may take a moment to start delivering).</summary>
    internal async Task<bool> WaitForFirstFrameAsync(TimeSpan timeout) =>
        await Task.WhenAny(_firstFrame.Task, Task.Delay(timeout)).ConfigureAwait(false) == _firstFrame.Task;

    /// <summary>Copies the current frame into a new texture and wraps it for the encoder. The caller disposes the surface.</summary>
    internal IDirect3DSurface SnapshotFrame()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            IntPtr texture = Direct3D11Interop.CreateTexture(_device, Width, Height);
            try
            {
                Direct3D11Interop.CopyResource(_context, texture, _latest);
                return Direct3D11Interop.CreateSurface(texture);
            }
            finally { Direct3D11Interop.Release(texture); }
        }
    }

    private static void ConfigureSession(GraphicsCaptureSession session)
    {
        try { session.IsCursorCaptureEnabled = true; }
        catch (Exception) { } // Older builds always draw the cursor; the property is only a preference.
        CaptureBorder.TryHide(session);
    }

    private void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        try
        {
            using Direct3D11CaptureFrame? frame = sender.TryGetNextFrame();
            if (frame is null) return;
            SizeInt32 size = frame.ContentSize;
            CopyFrame(frame, size);
            if (size.Width != _poolSize.Width || size.Height != _poolSize.Height) RecreatePool(sender, size);
            _firstFrame.TrySetResult();
        }
        catch (Exception error)
        {
            if (!_disposed) Failure ??= error; // Reported by the recorder; capture callbacks must not throw.
        }
    }

    private void CopyFrame(Direct3D11CaptureFrame frame, SizeInt32 size)
    {
        Int32Rect box = _crop is Int32Rect crop
            ? RecordingGeometry.ClampToFrame(crop, size.Width, size.Height)
            : RecordingGeometry.FitFrameToOutput(size.Width, size.Height, Width, Height);
        if (box.IsEmpty) return;
        IntPtr source = Direct3D11Interop.TextureFromSurface(frame.Surface);
        try
        {
            lock (_gate)
            {
                if (_disposed) return;
                int offsetX = _crop is Int32Rect cropBox ? box.X - cropBox.X : 0, offsetY = _crop is Int32Rect cropTop ? box.Y - cropTop.Y : 0;
                Direct3D11Interop.CopyRegion(_context, _latest, offsetX, offsetY, source, box);
            }
        }
        finally { Direct3D11Interop.Release(source); }
    }

    private void RecreatePool(Direct3D11CaptureFramePool pool, SizeInt32 size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;
        _poolSize = size;
        pool.Recreate(_winRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
    }

    private void Item_Closed(GraphicsCaptureItem sender, object args) => SourceClosed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _item.Closed -= Item_Closed;
        _framePool.FrameArrived -= FramePool_FrameArrived;
        try { _session.Dispose(); } catch (Exception) { } // The session may already be closed by Windows.
        try { _framePool.Dispose(); } catch (Exception) { }
        lock (_gate) ReleaseNative();
    }

    private void ReleaseNative()
    {
        Direct3D11Interop.Release(_latest);
        _latest = IntPtr.Zero;
        try { _winRtDevice?.Dispose(); } catch (Exception) { }
        Direct3D11Interop.Release(_context);
        Direct3D11Interop.Release(_device);
    }
}

/// <summary>
/// Turns off the yellow capture border on Windows 11 (GraphicsCaptureSession.IsBorderRequired, API contract 12). The
/// property is not in the 19041 projection, so it is set through IGraphicsCaptureSession3 when the OS has it.
/// </summary>
internal static unsafe class CaptureBorder
{
    private static readonly Guid IidGraphicsCaptureSession3 = new("f2cdd966-22ae-5ea1-9596-3a289344c3be");
    // IInspectable(0-5), get_IsBorderRequired(6), put_IsBorderRequired(7).
    private const int PutIsBorderRequired = 7;

    internal static bool TryHide(GraphicsCaptureSession session)
    {
        IntPtr native = IntPtr.Zero, session3 = IntPtr.Zero;
        try
        {
            native = MarshalInspectable<GraphicsCaptureSession>.FromManaged(session);
            if (Marshal.QueryInterface(native, in IidGraphicsCaptureSession3, out session3) < 0 || session3 == IntPtr.Zero) return false;
            IntPtr method = (*(IntPtr**)session3)[PutIsBorderRequired];
            return ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)method)(session3, 0) >= 0;
        }
        catch (Exception) { return false; } // Optional cosmetic setting; Windows 10 always shows the border.
        finally
        {
            if (session3 != IntPtr.Zero) Marshal.Release(session3);
            if (native != IntPtr.Zero) Marshal.Release(native);
        }
    }
}
