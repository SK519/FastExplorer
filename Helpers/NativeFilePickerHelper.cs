using System;
using System.IO;
using System.Runtime.InteropServices;
using FastExplorer.Core;

namespace FastExplorer.Helpers
{
    public static class NativeFilePickerHelper
    {
        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_ENABLESIZING = 0x00800000;
        private const int OFN_DONTADDTORECENT = 0x02000000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAMEW
        {
            public int lStructSize;
            public nint hwndOwner;
            public nint hInstance;
            public string? lpstrFilter;
            public string? lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public nint lpstrFile;
            public int nMaxFile;
            public nint lpstrFileTitle;
            public int nMaxFileTitle;
            public string? lpstrInitialDir;
            public string? lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public nint lCustData;
            public nint lpfnHook;
            public string? lpTemplateName;
            public nint pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", EntryPoint = "GetOpenFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

        [DllImport("user32.dll")]
        private static extern nint GetActiveWindow();

        /// <summary>
        /// 単一ファイル選択ダイアログを表示し、選択されたファイルのフルパスを返します。キャンセル時は null を返します。
        /// </summary>
        /// <param name="ownerHwnd">親ウィンドウハンドル (0 の場合はアクティブウィンドウ)</param>
        /// <param name="title">ダイアログタイトル</param>
        /// <param name="filter">Win32 形式のフィルター文字列 (例: "画像ファイル\0*.png;*.jpg\0すべてのファイル\0*.*\0\0")</param>
        /// <param name="initialDir">初期フォルダー</param>
        /// <returns>選択されたファイルパス、またはキャンセル時 null</returns>
        public static string? PickSingleFile(nint ownerHwnd, string title, string filter, string? initialDir = null)
        {
            if (ownerHwnd == 0)
            {
                ownerHwnd = GetActiveWindow();
            }

            const int maxBuffer = 4096;
            nint bufferPtr = Marshal.AllocHGlobal(maxBuffer * sizeof(char));
            try
            {
                // バッファーをゼロクリア
                unsafe
                {
                    byte* p = (byte*)bufferPtr;
                    for (int i = 0; i < maxBuffer * sizeof(char); i++) p[i] = 0;
                }

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = ownerHwnd,
                    lpstrFilter = filter,
                    lpstrFile = bufferPtr,
                    nMaxFile = maxBuffer,
                    lpstrTitle = title,
                    lpstrInitialDir = string.IsNullOrEmpty(initialDir) || !Directory.Exists(initialDir)
                        ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
                        : initialDir,
                    Flags = OFN_EXPLORER | OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_ENABLESIZING | OFN_DONTADDTORECENT
                };

                if (GetOpenFileNameW(ref ofn))
                {
                    string? selected = Marshal.PtrToStringUni(bufferPtr);
                    if (!string.IsNullOrWhiteSpace(selected) && File.Exists(selected))
                    {
                        return selected;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeFilePickerHelper] Error: {ex.Message}");
            }
            finally
            {
                Marshal.FreeHGlobal(bufferPtr);
            }

            return null;
        }

        /// <summary>
        /// 壁紙画像（PNG, JPG, BMP, WebP, GIF 等）の選択用ダイアログを開きます。
        /// </summary>
        public static string? PickWallpaperImage(nint ownerHwnd)
        {
            const string filter = "画像ファイル (*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif)\0*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.gif\0すべてのファイル (*.*)\0*.*\0\0";
            return PickSingleFile(
                ownerHwnd,
                "背景画像を選択",
                filter,
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
        }

        /// <summary>
        /// 実行可能ファイル (*.exe) の選択用ダイアログを開きます。
        /// </summary>
        public static string? PickExecutable(nint ownerHwnd, string title)
        {
            const string filter = "実行可能プログラム (*.exe)\0*.exe\0すべてのファイル (*.*)\0*.*\0\0";
            return PickSingleFile(
                ownerHwnd,
                title,
                filter,
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        }
    }
}
