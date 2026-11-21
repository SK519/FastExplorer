using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FastExplorer.Services
{
    /// <summary>
    /// Windows Restart Manager API (rstrtmgr.dll) を使って、ファイルをロックしているプロセス名を特定するヘルパー。
    /// </summary>
    public static class FileLockInspector
    {
        private const int ERROR_MORE_DATA = 234;
        private const int CCH_RM_MAX_APP_NAME = 255;
        private const int CCH_RM_MAX_SVC_NAME = 63;

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        private enum RM_APP_TYPE
        {
            RmUnknownApp = 0,
            RmMainWindow = 1,
            RmOtherWindow = 2,
            RmService = 3,
            RmExplorer = 4,
            RmConsole = 5,
            RmCritical = 1000
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_APP_NAME + 1)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCH_RM_MAX_SVC_NAME + 1)]
            public string strServiceShortName;
            public RM_APP_TYPE ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmEndSession(uint pSessionHandle);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        private static extern int RmRegisterResources(
            uint pSessionHandle,
            uint nFiles,
            string[] rgsFilenames,
            uint nApplications,
            [In] RM_UNIQUE_PROCESS[]? rgApplications,
            uint nServices,
            string[]? rgsServiceNames);

        [DllImport("rstrtmgr.dll")]
        private static extern int RmGetList(
            uint dwSessionHandle,
            out uint pnProcInfoNeeded,
            ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[]? rgAffectedApps,
            ref uint lpdwRebootReasons);

        public static bool IsSharingOrLockViolation(Exception ex)
        {
            if (ex is IOException ioe)
            {
                int code = ioe.HResult & 0xFFFF;
                // ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33)
                return code == 32 || code == 33;
            }
            return false;
        }

        /// <summary>
        /// ファイルまたはフォルダーが別プロセスによってロックされているかを軽量チェック
        /// </summary>
        public static bool IsFileLocked(string path)
        {
            if (File.Exists(path))
            {
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    return false;
                }
                catch (IOException ex) when (IsSharingOrLockViolation(ex))
                {
                    return true;
                }
                catch
                {
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// 指定ファイルをロックしているプロセス名の一覧を取得（フォルダーの場合は直下/配下から最初のロック中ファイルを探索）
        /// </summary>
        public static List<string> GetLockingProcesses(string path)
        {
            var targets = new List<string>();
            if (File.Exists(path))
            {
                targets.Add(path);
            }
            else if (Directory.Exists(path))
            {
                try
                {
                    int checkedCount = 0;
                    foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true
                    }))
                    {
                        if (IsFileLocked(file))
                        {
                            targets.Add(file);
                            if (targets.Count >= 5) break;
                        }
                        if (++checkedCount >= 300) break;
                    }
                }
                catch { }
            }

            if (targets.Count == 0) return new List<string>();
            return QueryRestartManager(targets.ToArray());
        }

        private static List<string> QueryRestartManager(string[] filePaths)
        {
            var result = new List<string>();
            uint sessionHandle = 0;
            string sessionKey = Guid.NewGuid().ToString("N");

            try
            {
                if (RmStartSession(out sessionHandle, 0, sessionKey) != 0)
                    return result;

                try
                {
                    if (RmRegisterResources(sessionHandle, (uint)filePaths.Length, filePaths, 0, null, 0, null) != 0)
                        return result;

                    uint pnProcInfoNeeded = 0;
                    uint pnProcInfo = 0;
                    uint lpdwRebootReasons = 0;

                    int res = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, null, ref lpdwRebootReasons);
                    if (res == ERROR_MORE_DATA && pnProcInfoNeeded > 0)
                    {
                        var processInfo = new RM_PROCESS_INFO[pnProcInfoNeeded];
                        pnProcInfo = pnProcInfoNeeded;
                        res = RmGetList(sessionHandle, out pnProcInfoNeeded, ref pnProcInfo, processInfo, ref lpdwRebootReasons);
                        if (res == 0)
                        {
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            for (int i = 0; i < pnProcInfo; i++)
                            {
                                string name = processInfo[i].strAppName;
                                if (string.IsNullOrWhiteSpace(name))
                                {
                                    try
                                    {
                                        using var proc = System.Diagnostics.Process.GetProcessById(processInfo[i].Process.dwProcessId);
                                        name = proc.ProcessName;
                                    }
                                    catch
                                    {
                                        name = $"PID {processInfo[i].Process.dwProcessId}";
                                    }
                                }
                                if (seen.Add(name))
                                {
                                    result.Add(name);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    RmEndSession(sessionHandle);
                }
            }
            catch { }

            return result;
        }
    }
}
