using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using FastExplorer.Core;

namespace FastExplorer.Services
{
    public static partial class SystemIntegrationService
    {
        public static void RestartWindowsExplorer()
        {
            try
            {
                long now = Environment.TickCount64;
                if (now - _lastExplorerRestartTime < 4000)
                {
                    // 直近4秒以内の連続再起動呼び出しを防止
                    return;
                }
                _lastExplorerRestartTime = now;

                foreach (var p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }

                System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1200);
                    if (Win32Interop.FindWindowW("Shell_TrayWnd", null) == nint.Zero &&
                        Process.GetProcessesByName("explorer").Length == 0)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "explorer.exe",
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] RestartWindowsExplorer error: {ex.Message}");
            }
        }

        private static void KillWatcherProcesses()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("FastExplorerWatcher"))
                {
                    try { p.Kill(); } catch { }
                }
            }
            catch { }
        }

        private static string GetWatcherExecutablePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "FastExplorerWatcher.exe");
            if (File.Exists(path)) return path;

            string devPath = Path.Combine(baseDir, "..", "..", "..", "Watcher", "bin", "FastExplorerWatcher.exe");
            if (File.Exists(devPath)) return Path.GetFullPath(devPath);

            return path;
        }

        public static bool SetTaskSchedulerTask(bool enable)
        {
            try
            {
                string taskName = "FastExplorer_Background";
                if (enable)
                {
                    string watcherExe = GetWatcherExecutablePath();
                    string tempXml = Path.Combine(Path.GetTempPath(), "FastExplorer_Task.xml");

                    string xmlContent = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo>
    <Description>FastExplorer Background Resident for Instant Launch</Description>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id=""Author"">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author"">
    <Exec>
      <Command>{System.Security.SecurityElement.Escape(watcherExe)}</Command>
    </Exec>
  </Actions>
</Task>";

                    File.WriteAllText(tempXml, xmlContent, System.Text.Encoding.Unicode);

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("/create");
                    psi.ArgumentList.Add("/tn");
                    psi.ArgumentList.Add(taskName);
                    psi.ArgumentList.Add("/xml");
                    psi.ArgumentList.Add(tempXml);
                    psi.ArgumentList.Add("/f");

                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);

                    try { File.Delete(tempXml); } catch { }

                    // Watcher プロセスを今すぐ起動
                    if (File.Exists(watcherExe))
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = watcherExe,
                            UseShellExecute = true,
                            CreateNoWindow = true
                        });
                    }

                    return proc?.ExitCode == 0;
                }
                else
                {
                    // Watcher プロセスをキル
                    try
                    {
                        foreach (var p in Process.GetProcessesByName("FastExplorerWatcher"))
                        {
                            p.Kill();
                        }
                    }
                    catch { }

                    var psi = new ProcessStartInfo
                    {
                        FileName = "schtasks.exe",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("/delete");
                    psi.ArgumentList.Add("/tn");
                    psi.ArgumentList.Add(taskName);
                    psi.ArgumentList.Add("/f");

                    using var proc = Process.Start(psi);
                    proc?.WaitForExit(3000);
                    return proc?.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetTaskSchedulerTask error: {ex.Message}");
                return false;
            }
        }

        public static bool SetStartupFolderShortcut(bool enable)
        {
            try
            {
                string startupFolder = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string shortcutPath = Path.Combine(startupFolder, "FastExplorerWatcher.lnk");
                string legacyShortcut = Path.Combine(startupFolder, "FastExplorer.lnk");

                // 常に古い FastExplorer.lnk をクリーンアップ
                if (File.Exists(legacyShortcut))
                {
                    try { File.Delete(legacyShortcut); } catch { }
                }

                if (enable)
                {
                    string watcherExe = GetWatcherExecutablePath();
                    if (CreateShortcutCom(shortcutPath, watcherExe, $"{watcherExe},0"))
                    {
                        return File.Exists(shortcutPath);
                    }
                    return false;
                }
                else
                {
                    if (File.Exists(shortcutPath))
                    {
                        File.Delete(shortcutPath);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetStartupFolderShortcut error: {ex.Message}");
                return false;
            }
        }

        #region Native COM Shortcut Interfaces (Safe IShellLinkW / IPersistFile)

        [ComImport]
        [Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkClass
        {
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cchMaxPath, out IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cchMaxName);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cchMaxPath);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cchMaxPath);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cchIconPath, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        [Guid("0000010b-0000-0000-C000-000000000046")]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
            void GetCurFile([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder ppszFileName);
        }

        private static bool CreateShortcutCom(string shortcutPath, string targetPath, string? iconLocation = null)
        {
            try
            {
                var linkObj = new ShellLinkClass();
                if (linkObj is not IShellLinkW shellLink) return false;

                try
                {
                    shellLink.SetPath(targetPath);
                    string? dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        shellLink.SetWorkingDirectory(dir);
                    }

                    if (!string.IsNullOrEmpty(iconLocation))
                    {
                        string iconPath = iconLocation.Trim('"');
                        int iconIndex = 0;
                        int commaIdx = iconLocation.LastIndexOf(',');
                        if (commaIdx > 0 && int.TryParse(iconLocation[(commaIdx + 1)..], out int parsedIdx))
                        {
                            iconPath = iconLocation[..commaIdx].Trim('"');
                            iconIndex = parsedIdx;
                        }
                        shellLink.SetIconLocation(iconPath, iconIndex);
                    }

                    if (linkObj is IPersistFile persistFile)
                    {
                        persistFile.Save(shortcutPath, true);
                        return true;
                    }
                    return false;
                }
                finally
                {
                    Marshal.FinalReleaseComObject(linkObj);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] CreateShortcutCom error: {ex.Message}");
                return false;
            }
        }

        #endregion

        public static bool SetStartupRunKey(bool enable)
        {
            try
            {
                // 1. スタートアップフォルダー
                SetStartupFolderShortcut(enable);

                // 2. タスクスケジューラ
                SetTaskSchedulerTask(enable);

                // 3. レジストリ Run キー
                using var runKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (runKey != null)
                {
                    if (enable)
                    {
                        string watcherExe = GetWatcherExecutablePath();
                        runKey.SetValue("FastExplorerWatcher", $"\"{watcherExe}\"");
                    }
                    else
                    {
                        runKey.DeleteValue("FastExplorerWatcher", false);
                        runKey.DeleteValue("FastExplorer", false);
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SystemIntegration] SetStartupRunKey error: {ex.Message}");
            }
            return false;
        }
    }
}
