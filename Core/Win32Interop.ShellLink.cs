using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FastExplorer.Core
{
    public static partial class Win32Interop
    {
        #region Shell Link (Shortcut Target Resolution) & Recent Docs

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        public class ShellLinkClass { }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        public interface IShellLinkW
        {
            [PreserveSig]
            int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, nint pfd, uint fFlags);
            [PreserveSig]
            int GetIDList(out nint ppidl);
            [PreserveSig]
            int SetIDList(nint pidl);
            [PreserveSig]
            int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
            [PreserveSig]
            int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig]
            int GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
            [PreserveSig]
            int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            [PreserveSig]
            int GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
            [PreserveSig]
            int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            [PreserveSig]
            int GetHotkey(out short pwHotkey);
            [PreserveSig]
            int SetHotkey(short wHotkey);
            [PreserveSig]
            int GetShowCmd(out int piShowCmd);
            [PreserveSig]
            int SetShowCmd(int iShowCmd);
            [PreserveSig]
            int GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            [PreserveSig]
            int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            [PreserveSig]
            int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            [PreserveSig]
            int Resolve(nint hwnd, uint fFlags);
            [PreserveSig]
            int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        public interface IPersistFile
        {
            [PreserveSig]
            int GetClassID(out Guid pClassID);
            [PreserveSig]
            int IsDirty();
            [PreserveSig]
            int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            [PreserveSig]
            int Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            [PreserveSig]
            int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            [PreserveSig]
            int GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
        }

        public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
        public static readonly Guid IID_IShellLinkW = new("000214F9-0000-0000-C000-000000000046");
        public static readonly Guid IID_IPersistFile = new("0000010b-0000-0000-C000-000000000046");

        [DllImport("ole32.dll", ExactSpelling = true)]
        public static extern int CoCreateInstance(
            [In] in Guid rclsid,
            nint pUnkOuter,
            uint dwClsContext,
            [In] in Guid riid,
            out nint ppv);

        public static string? ResolveShortcut(string lnkPath)
        {
            if (string.IsNullOrEmpty(lnkPath)) return null;

            nint pUnknown = nint.Zero;
            try
            {
                int hr = CoCreateInstance(in CLSID_ShellLink, nint.Zero, 1 /* CLSCTX_INPROC_SERVER */, in IID_IShellLinkW, out pUnknown);
                if (hr == 0 && pUnknown != nint.Zero)
                {
                    var linkObj = Marshal.GetObjectForIUnknown(pUnknown);
                    if (linkObj is IPersistFile persistFile && linkObj is IShellLinkW shellLink)
                    {
                        hr = persistFile.Load(lnkPath, 0); // STGM_READ = 0
                        if (hr == 0)
                        {
                            try
                            {
                                shellLink.Resolve(nint.Zero, 0x01 | 0x10 | 0x20); // SLR_NO_UI | SLR_NOSEARCH | SLR_NOTRACK
                            }
                            catch { }

                            var sb = new StringBuilder(1024);
                            hr = shellLink.GetPath(sb, sb.Capacity, nint.Zero, 0x4 /* SLGP_RAWPATH */ | 0x2 /* SLGP_UNCPRIORITY */);
                            if (hr == 0 && sb.Length > 0)
                            {
                                return sb.ToString();
                            }

                            // GetPath で空の場合は IDList 経由でパス解決 (フォルダーや特殊パス等)
                            if (shellLink.GetIDList(out nint pidl) == 0 && pidl != nint.Zero)
                            {
                                try
                                {
                                    sb.Clear();
                                    if (SHGetPathFromIDListW(pidl, sb) && sb.Length > 0)
                                    {
                                        return sb.ToString();
                                    }
                                }
                                finally
                                {
                                    ILFree(pidl);
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (pUnknown != nint.Zero)
                {
                    Marshal.Release(pUnknown);
                }
            }

            // 2. バイナリ直接パースによるフォールバック (COM 非依存・超高速)
            string? parsed = ParseLnkFileBinary(lnkPath);
            if (!string.IsNullOrEmpty(parsed))
            {
                return parsed;
            }

            return null;
        }

        private static string? ParseLnkFileBinary(string lnkPath)
        {
            try
            {
                if (!File.Exists(lnkPath)) return null;

                using var stream = new FileStream(lnkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new BinaryReader(stream);

                if (stream.Length < 76) return null;
                uint headerSize = reader.ReadUInt32();
                if (headerSize != 0x0000004C) return null;

                // Skip LinkCLSID (16 bytes)
                stream.Seek(16, SeekOrigin.Current);

                uint linkFlags = reader.ReadUInt32();

                // Skip FileAttributes(4), CreationTime(8), AccessTime(8), WriteTime(8), FileSize(4), IconIndex(4), ShowCommand(4), Hotkey(2), Reserved(10) -> 52 bytes
                stream.Seek(52, SeekOrigin.Current);

                // If HasLinkTargetIDList (bit 0 = 0x01), skip IDList
                if ((linkFlags & 0x01) != 0)
                {
                    if (stream.Position + 2 > stream.Length) return null;
                    ushort idListSize = reader.ReadUInt16();
                    stream.Seek(idListSize, SeekOrigin.Current);
                }

                // If HasLinkInfo (bit 1 = 0x02), read LinkInfo structure
                if ((linkFlags & 0x02) != 0)
                {
                    long linkInfoStart = stream.Position;
                    if (stream.Position + 28 > stream.Length) return null;
                    uint linkInfoSize = reader.ReadUInt32();
                    if (linkInfoSize >= 28)
                    {
                        uint linkInfoHeaderSize = reader.ReadUInt32();
                        uint linkInfoFlags = reader.ReadUInt32();
                        uint localBasePathOffset = reader.ReadUInt32();
                        uint commonNetworkRelativeLinkOffset = reader.ReadUInt32();
                        uint commonPathSuffixOffset = reader.ReadUInt32();

                        // Unicode local base path (header size >= 36)
                        if (linkInfoHeaderSize >= 36)
                        {
                            uint localBasePathOffsetUnicode = reader.ReadUInt32();
                            if (localBasePathOffsetUnicode > 0 && localBasePathOffsetUnicode < linkInfoSize)
                            {
                                stream.Seek(linkInfoStart + localBasePathOffsetUnicode, SeekOrigin.Begin);
                                var chars = new List<char>();
                                while (stream.Position + 1 < stream.Length)
                                {
                                    char c = reader.ReadChar();
                                    if (c == '\0') break;
                                    chars.Add(c);
                                }
                                string unicodePath = new string(chars.ToArray());
                                if (!string.IsNullOrEmpty(unicodePath)) return unicodePath;
                            }
                        }

                        // ASCII local base path
                        if (localBasePathOffset > 0 && localBasePathOffset < linkInfoSize)
                        {
                            stream.Seek(linkInfoStart + localBasePathOffset, SeekOrigin.Begin);
                            var bytes = new List<byte>();
                            while (stream.Position < stream.Length)
                            {
                                byte b = reader.ReadByte();
                                if (b == 0) break;
                                bytes.Add(b);
                            }
                            string asciiPath = Encoding.Default.GetString(bytes.ToArray());
                            if (!string.IsNullOrEmpty(asciiPath)) return asciiPath;
                        }
                    }
                }
            }
            catch { }

            return null;
        }

        public const uint SHARD_PATHW = 0x00000003;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern void SHAddToRecentDocs(uint uFlags, string pv);

        public static void RecordRecentDocument(string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && (File.Exists(path) || Directory.Exists(path)))
                {
                    SHAddToRecentDocs(SHARD_PATHW, path);
                }
            }
            catch { }
        }

        /// <summary>
        /// 最近使用した項目のショートカット (.lnk) を削除し、Windows シェルに通知してエクスプローラーと即時同期
        /// </summary>
        public static bool DeleteRecentShortcut(string targetPathOrLnk)
        {
            if (string.IsNullOrWhiteSpace(targetPathOrLnk)) return false;

            bool deleted = false;
            try
            {
                // 引数自体が .lnk ファイルかつ Recent フォルダー内にある場合
                if (targetPathOrLnk.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPathOrLnk))
                {
                    File.Delete(targetPathOrLnk);
                    deleted = true;
                }
                else
                {
                    // Recent フォルダー内からターゲットパスと一致する .lnk を探して削除
                    string recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
                    if (Directory.Exists(recentFolder))
                    {
                        var lnkFiles = Directory.GetFiles(recentFolder, "*.lnk");
                        foreach (var lnk in lnkFiles)
                        {
                            string? resolved = ResolveShortcut(lnk);
                            if (string.Equals(resolved, targetPathOrLnk, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    File.Delete(lnk);
                                    deleted = true;
                                }
                                catch { }
                            }
                        }
                    }
                }

                if (deleted)
                {
                    SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0x1000 /* SHCNF_FLUSH */, nint.Zero, nint.Zero);
                }
            }
            catch { }

            return deleted;
        }

        #endregion
    }
}
