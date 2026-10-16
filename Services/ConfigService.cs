using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FastExplorer.Models;

namespace FastExplorer.Services
{
    public class ConfigService
    {
        private static readonly string AppDataConfigDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastExplorer");

        private static readonly string LocalAppConfigPath = Path.Combine(AppDataConfigDir, "config.json");

        private static readonly string BaseDirConfigPath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "config.json");

        private static readonly object _lock = new();
        private static AppConfig? _current;

        public static AppConfig Current
        {
            get
            {
                lock (_lock)
                {
                    if (_current == null)
                    {
                        Load();
                    }
                    return _current ??= new AppConfig();
                }
            }
        }

        public static void Load()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(LocalAppConfigPath))
                    {
                        string json = File.ReadAllText(LocalAppConfigPath);
                        _current = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    }
                    else if (File.Exists(BaseDirConfigPath))
                    {
                        string json = File.ReadAllText(BaseDirConfigPath);
                        _current = JsonSerializer.Deserialize(json, AppConfigJsonContext.Default.AppConfig);
                    }
                }
                catch
                {
                    // ロード失敗時はデフォルト設定を使用
                }

                _current ??= new AppConfig();
                if (_current.FolderViewSettings != null)
                {
                    _current.FolderViewSettings = new Dictionary<string, FolderViewSetting>(_current.FolderViewSettings, StringComparer.OrdinalIgnoreCase);
                }
                else
                {
                    _current.FolderViewSettings = new Dictionary<string, FolderViewSetting>(StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        public static void Save()
        {
            lock (_lock)
            {
                try
                {
                    if (_current != null)
                    {
                        Directory.CreateDirectory(AppDataConfigDir);
                        string json = JsonSerializer.Serialize(_current, AppConfigJsonContext.Default.AppConfig);
                        string tempPath = LocalAppConfigPath + ".tmp";
                        File.WriteAllText(tempPath, json);
                        File.Move(tempPath, LocalAppConfigPath, overwrite: true);
                    }
                }
                catch
                {
                    // 保存失敗は無視
                }
            }
        }

        public static void ResetToDefaults()
        {
            lock (_lock)
            {
                _current = new AppConfig();
                Save();
            }
        }
    }
}
