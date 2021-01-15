using Robmikh.WindowsRuntimeHelpers;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace ManagedScreenshotDemo
{
    class Program
    {
        const int MONITOR_DEFAULTTOPRIMARY = 1;
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDesktopWindow();

        static async Task EncodeBytesAsync(string fileName, int width, int height, byte[] bytes)
        {
            var currentDirectory = Directory.GetCurrentDirectory();
            var folder = await StorageFolder.GetFolderFromPathAsync(currentDirectory);
            var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);

            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)width,
                    (uint)height,
                    1.0,
                    1.0,
                    bytes);
                await encoder.FlushAsync();
            }
        }

        static void Main(string[] args)
        {
            // Get a capture item that represents the primary monitor
            var monitor = MonitorFromWindow(GetDesktopWindow(), MONITOR_DEFAULTTOPRIMARY);
            var item = CaptureHelper.CreateItemForMonitor(monitor);
            var size = item.Size;

            // Setup D3D
            var device = Direct3D11Helper.CreateDevice();
            var d3dDevice = Direct3D11Helper.CreateSharpDXDevice(device);
            var d3dContext = d3dDevice.ImmediateContext;

            // Create our staging texture
            var description = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = size.Width,
                Height = size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription()
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = SharpDX.Direct3D11.ResourceUsage.Staging,
                BindFlags = SharpDX.Direct3D11.BindFlags.None,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.Read,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            var stagingTexture = new SharpDX.Direct3D11.Texture2D(d3dDevice, description);

            // Setup capture
            var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);
            var session = framePool.CreateCaptureSession(item);

            var imageNum = 0;
            var endEvent = new ManualResetEvent(false);
            framePool.FrameArrived += (sender, a) =>
            {
                using (var frame = sender.TryGetNextFrame())
                using (var bitmap = Direct3D11Helper.CreateSharpDXTexture2D(frame.Surface))
                {
                    // Copy to our staging texture
                    d3dContext.CopyResource(bitmap, stagingTexture);

                    // Map our texture and get the bits
                    var mapped = d3dContext.MapSubresource(stagingTexture, 0, SharpDX.Direct3D11.MapMode.Read, SharpDX.Direct3D11.MapFlags.None);
                    var source = mapped.DataPointer;
                    var sourceStride = mapped.RowPitch;

                    var bytes = new byte[size.Width * size.Height * 4]; // 4 bytes per pixel
                    unsafe
                    {
                        fixed (byte* bytesPointer = bytes)
                        {
                            var dest = (IntPtr)bytesPointer;
                            var destStride = size.Width * 4;

                            for (int i = 0; i < size.Height; i++)
                            {
                                SharpDX.Utilities.CopyMemory(dest, source, destStride);

                                source = IntPtr.Add(source, sourceStride);
                                dest = IntPtr.Add(dest, destStride);
                            }
                        }
                    }

                    // Don't forget to unmap when you're done!
                    d3dContext.UnmapSubresource(stagingTexture, 0);

                    // Encode it
                    // NOTE: Waiting here will stall the capture
                    EncodeBytesAsync($"image{imageNum}.png", size.Width, size.Height, bytes).Wait();

                    imageNum++;
                    if (imageNum > 10)
                    {
                        framePool.Dispose();
                        session.Dispose();
                        endEvent.Set();
                    }
                }
            };

            // Start the capture and wait
            session.StartCapture();
            endEvent.WaitOne();
        }
    }
}
