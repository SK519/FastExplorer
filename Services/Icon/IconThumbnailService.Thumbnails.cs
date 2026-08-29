using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FastExplorer.Core;
using Windows.Graphics.Imaging;

namespace FastExplorer.Services
{
    public partial class IconThumbnailService
    {
        public static SoftwareBitmap? ExtractThumbnailViaShellItem(
            string fullPath,
            int size = 128,
            Win32Interop.SIIGBF extraFlags = Win32Interop.SIIGBF.SIIGBF_BIGGERSIZEOK | Win32Interop.SIIGBF.SIIGBF_SCALEUP)
        {
            try
            {
                int hr = Win32Interop.SHCreateItemFromParsingName(
                    fullPath,
                    nint.Zero,
                    Win32Interop.IID_IShellItemImageFactory,
                    out var pUnknown);

                if (hr == 0 && pUnknown != nint.Zero)
                {
                    try
                    {
                        var factory = (Win32Interop.IShellItemImageFactory)Marshal.GetObjectForIUnknown(pUnknown);
                        var sz = new Win32Interop.SIZE(size, size);
                        hr = factory.GetImage(sz, extraFlags, out var hBitmap);
                        if (hr == 0 && hBitmap != nint.Zero)
                        {
                            try
                            {
                                bool isDir = Directory.Exists(fullPath);
                                return ConvertHBitmapToSoftwareBitmap(hBitmap, isDirectory: isDir);
                            }
                            finally
                            {
                                Win32Interop.DeleteObject(hBitmap);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(pUnknown);
                    }
                }
            }
            catch
            {
                // ignored
            }
            return null;
        }

        public static SoftwareBitmap? ExtractSkiaImageThumbnail(string fullPath, int targetSize = 96)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;

                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var codec = SkiaSharp.SKCodec.Create(stream);
                if (codec == null) return null;

                int origW = codec.Info.Width;
                int origH = codec.Info.Height;
                if (origW <= 0 || origH <= 0) return null;

                int targetW, targetH;
                if (origW >= origH)
                {
                    targetW = targetSize;
                    targetH = Math.Max(1, (int)((double)origH / origW * targetSize));
                }
                else
                {
                    targetH = targetSize;
                    targetW = Math.Max(1, (int)((double)origW / origH * targetSize));
                }

                var imageInfo = new SkiaSharp.SKImageInfo(targetW, targetH, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
                using var origBmp = SkiaSharp.SKBitmap.Decode(codec);
                if (origBmp == null) return null;

                using var resized = origBmp.Resize(imageInfo, SkiaSharp.SKSamplingOptions.Default);
                if (resized == null) return null;

                var pixelSpan = resized.GetPixelSpan();
                int byteCount = pixelSpan.Length;
                byte[] rentedArray = ArrayPool<byte>.Shared.Rent(byteCount);
                try
                {
                    pixelSpan.CopyTo(rentedArray);
                    var softwareBitmap = new SoftwareBitmap(BitmapPixelFormat.Bgra8, targetW, targetH, BitmapAlphaMode.Premultiplied);
                    softwareBitmap.CopyFromBuffer(rentedArray.AsBuffer(0, byteCount));
                    return softwareBitmap;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rentedArray);
                }
            }
            catch
            {
                return null;
            }
        }

        private static SoftwareBitmap? ExtractDirectImageThumbnail(string fullPath, uint targetSize = 128)
        {
            try
            {
                if (!File.Exists(fullPath)) return null;

                using var stream = File.OpenRead(fullPath);
                using var randomStream = stream.AsRandomAccessStream();
                var decoder = BitmapDecoder.CreateAsync(randomStream).AsTask().GetAwaiter().GetResult();

                var transform = new BitmapTransform();
                uint origW = decoder.PixelWidth;
                uint origH = decoder.PixelHeight;
                if (origW > targetSize || origH > targetSize)
                {
                    if (origW >= origH)
                    {
                        transform.ScaledWidth = targetSize;
                        transform.ScaledHeight = Math.Max(1, (uint)((double)origH / origW * targetSize));
                    }
                    else
                    {
                        transform.ScaledHeight = targetSize;
                        transform.ScaledWidth = Math.Max(1, (uint)((double)origW / origH * targetSize));
                    }
                    transform.InterpolationMode = BitmapInterpolationMode.Linear;
                }

                return decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.RespectExifOrientation,
                    ColorManagementMode.ColorManageToSRgb
                ).AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                return null;
            }
        }
    }
}
