using System;
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

        private static AppConfig? _current;

        public static AppConfig Current
        {
            get
            {
                if (_current == null)
                {
                    Load();
                }
                return _current ??= new AppConfig();
            }
        }

        public static void Load()
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
        }

        public static void Save()
        {
            try
            {
                if (_current != null)
                {
                    Directory.CreateDirectory(AppDataConfigDir);
                    string json = JsonSerializer.Serialize(_current, AppConfigJsonContext.Default.AppConfig);
                    File.WriteAllText(LocalAppConfigPath, json);
                }
            }
            catch
            {
                // 保存失敗は無視
            }
        }

        public static void ResetToDefaults()
        {
            _current = new AppConfig();
            Save();
        }
    }
}
