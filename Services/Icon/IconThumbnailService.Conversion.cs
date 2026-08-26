using System;
using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using FastExplorer.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace FastExplorer.Services
{
    public partial class IconThumbnailService
    {
        private static readonly int BitmapHeaderSize = Marshal.SizeOf<Win32Interop.BITMAPINFOHEADER>();
        private static readonly int BitmapStructSize = Marshal.SizeOf<Win32Interop.BITMAP>();

        public static SoftwareBitmap? ConvertHIconToSoftwareBitmap(nint hIcon)
        {
            try
            {
                if (!Win32Interop.GetIconInfo(hIcon, out var iconInfo))
                    return null;

                nint hBitmap = iconInfo.hbmColor != nint.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
                if (hBitmap == nint.Zero) return null;

                try
                {
                    Win32Interop.GetObject(hBitmap, BitmapStructSize, out var bmp);
                    int width = bmp.bmWidth;
                    int height = bmp.bmHeight;

                    if (iconInfo.hbmColor == nint.Zero)
                    {
                        height /= 2;
                    }

                    if (width <= 0 || height <= 0) return null;

                    var bmi = new Win32Interop.BITMAPINFO
                    {
                        bmiHeader = new Win32Interop.BITMAPINFOHEADER
                        {
                            biSize = (uint)BitmapHeaderSize,
                            biWidth = width,
                            biHeight = -height,
                            biPlanes = 1,
                            biBitCount = 32,
                            biCompression = Win32Interop.BI_RGB
                        }
                    };

                    int requiredBytes = width * height * 4;
                    byte[] pixelData = ArrayPool<byte>.Shared.Rent(requiredBytes);

                    try
                    {
                        nint hdc = Win32Interop.GetDC(nint.Zero);
                        try
                        {
                            int scanLines = Win32Interop.GetDIBits(
                                hdc,
                                hBitmap,
                                0,
                                (uint)height,
                                pixelData,
                                ref bmi,
                                Win32Interop.DIB_RGB_COLORS);

                            if (scanLines == 0) return null;
                        }
                        finally
                        {
                            Win32Interop.ReleaseDC(nint.Zero, hdc);
                        }

                        // アルファチャンネルの有無をチェック
                        bool hasAlpha = false;
                        for (int i = 3; i < requiredBytes; i += 4)
                        {
                            if (pixelData[i] > 0)
                            {
                                hasAlpha = true;
                                break;
                            }
                        }

                        // アルファが全て0（24bit GDIビットマップ）の場合、hbmMask があればマスクからアルファを復元
                        if (!hasAlpha)
                        {
                            bool appliedMask = false;
                            if (iconInfo.hbmMask != nint.Zero && iconInfo.hbmColor != nint.Zero)
                            {
                                var maskBmi = new Win32Interop.BITMAPINFO
                                {
                                    bmiHeader = new Win32Interop.BITMAPINFOHEADER
                                    {
                                        biSize = (uint)BitmapHeaderSize,
                                        biWidth = width,
                                        biHeight = -height,
                                        biPlanes = 1,
                                        biBitCount = 1,
                                        biCompression = Win32Interop.BI_RGB
                                    }
                                };
                                int maskRowBytes = ((width + 31) / 32) * 4;
                                int maskTotalBytes = maskRowBytes * height;
                                byte[] maskBytes = ArrayPool<byte>.Shared.Rent(maskTotalBytes);
                                try
                                {
                                    nint maskHdc = Win32Interop.GetDC(nint.Zero);
                                    try
                                    {
                                        int maskLines = Win32Interop.GetDIBits(
                                            maskHdc,
                                            iconInfo.hbmMask,
                                            0,
                                            (uint)height,
                                            maskBytes,
                                            ref maskBmi,
                                            Win32Interop.DIB_RGB_COLORS);

                                        if (maskLines > 0)
                                        {
                                            for (int y = 0; y < height; y++)
                                            {
                                                int rowOffset = y * maskRowBytes;
                                                for (int x = 0; x < width; x++)
                                                {
                                                    int byteIdx = rowOffset + (x / 8);
                                                    int bitIdx = 7 - (x % 8);
                                                    bool isTransparent = ((maskBytes[byteIdx] >> bitIdx) & 1) != 0;
                                                    int pixelIdx = (y * width + x) * 4;
                                                    pixelData[pixelIdx + 3] = isTransparent ? (byte)0 : (byte)255;
                                                }
                                            }
                                            hasAlpha = true;
                                            appliedMask = true;
                                        }
                                    }
                                    finally
                                    {
                                        Win32Interop.ReleaseDC(nint.Zero, maskHdc);
                                    }
                                }
                                finally
                                {
                                    ArrayPool<byte>.Shared.Return(maskBytes);
                                }
                            }

                            if (!appliedMask)
                            {
                                for (int i = 3; i < requiredBytes; i += 4)
                                {
                                    pixelData[i] = 255;
                                }
                            }
                        }

                        var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                            pixelData.AsBuffer(0, requiredBytes),
                            BitmapPixelFormat.Bgra8,
                            width,
                            height,
                            hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore);

                        return softwareBitmap;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixelData);
                    }
                }
                finally
                {
                    if (iconInfo.hbmColor != nint.Zero) Win32Interop.DeleteObject(iconInfo.hbmColor);
                    if (iconInfo.hbmMask != nint.Zero) Win32Interop.DeleteObject(iconInfo.hbmMask);
                }
            }
            catch
            {
                return null;
            }
        }

        public static SoftwareBitmap? ConvertHBitmapToSoftwareBitmap(nint hBitmap, bool isDirectory = false)
        {
            if (hBitmap == nint.Zero) return null;
            try
            {
                Win32Interop.GetObject(hBitmap, BitmapStructSize, out var bmp);
                int width = bmp.bmWidth;
                int height = bmp.bmHeight;
                if (width <= 0 || height <= 0) return null;

                var bmi = new Win32Interop.BITMAPINFO
                {
                    bmiHeader = new Win32Interop.BITMAPINFOHEADER
                    {
                        biSize = (uint)BitmapHeaderSize,
                        biWidth = width,
                        biHeight = -height,
                        biPlanes = 1,
                        biBitCount = 32,
                        biCompression = Win32Interop.BI_RGB
                    }
                };

                int requiredBytes = width * height * 4;
                byte[] pixelData = ArrayPool<byte>.Shared.Rent(requiredBytes);

                try
                {
                    nint hdc = Win32Interop.GetDC(nint.Zero);
                    try
                    {
                        int scanLines = Win32Interop.GetDIBits(
                            hdc,
                            hBitmap,
                            0,
                            (uint)height,
                            pixelData,
                            ref bmi,
                            Win32Interop.DIB_RGB_COLORS);

                        if (scanLines == 0) return null;
                    }
                    finally
                    {
                        Win32Interop.ReleaseDC(nint.Zero, hdc);
                    }

                    bool hasAlpha = false;
                    for (int i = 3; i < requiredBytes; i += 4)
                    {
                        if (pixelData[i] > 0)
                        {
                            hasAlpha = true;
                            break;
                        }
                    }

                    // フォルダーの場合で四隅が白の場合は白背景プレートとみなして破棄し、透過アイコンへフォールバック
                    if (isDirectory)
                    {
                        int tl = 0;
                        int tr = Math.Max(0, (width - 1) * 4);
                        int bl = Math.Max(0, (height - 1) * width * 4);
                        int br = Math.Max(0, ((height - 1) * width + width - 1) * 4);

                        if (br + 2 < requiredBytes)
                        {
                            bool isCornerWhite = (pixelData[tl] > 240 && pixelData[tl + 1] > 240 && pixelData[tl + 2] > 240) &&
                                                 (pixelData[tr] > 240 && pixelData[tr + 1] > 240 && pixelData[tr + 2] > 240);

                            if (isCornerWhite)
                            {
                                return null;
                            }
                        }
                    }

                    if (!hasAlpha)
                    {
                        for (int i = 3; i < requiredBytes; i += 4)
                        {
                            pixelData[i] = 255;
                        }
                    }

                    return SoftwareBitmap.CreateCopyFromBuffer(
                        pixelData.AsBuffer(0, requiredBytes),
                        BitmapPixelFormat.Bgra8,
                        width,
                        height,
                        hasAlpha ? BitmapAlphaMode.Premultiplied : BitmapAlphaMode.Ignore);
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(pixelData);
                }
            }
            catch
            {
                return null;
            }
        }
    }
}
