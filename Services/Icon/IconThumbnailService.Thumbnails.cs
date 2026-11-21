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

        public static SoftwareBitmap? ExtractDirectImageThumbnail(string fullPath, uint targetSize = 96)
        {
            try
            {
                var fi = new FileInfo(fullPath);
                if (!fi.Exists || fi.Length == 0) return null;

                // 20MB 超の超高解像度画像はまず Windows シェル既存サムネイルキャッシュのみをゼロデコード試行
                if (fi.Length > 20L * 1024 * 1024)
                {
                    var cachedThumb = ExtractThumbnailViaShellItem(
                        fullPath,
                        (int)targetSize,
                        Win32Interop.SIIGBF.SIIGBF_INCACHEONLY | Win32Interop.SIIGBF.SIIGBF_THUMBNAILONLY);
                    if (cachedThumb != null)
                    {
                        return cachedThumb;
                    }
                }

                using var stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
