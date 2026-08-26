using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FastExplorer.Models
{
    public class EditorConfig
    {
        public string Path { get; set; } = "notepad.exe";
        public List<string> Args { get; set; } = ["{filePath}"];
    }

    public class TerminalConfig
    {
        public string Path { get; set; } = "wt.exe";
        public List<string> Args { get; set; } = ["-d", "{dirPath}"];
    }

    public class StartupConfig
    {
        public bool ResidentOnBoot { get; set; } = true;
        public string DefaultPath { get; set; } = "ThisPC";
    }

    public class CacheConfig
    {
        public int MaxEntries { get; set; } = 2000;
        public int MaxMemoryMB { get; set; } = 50;
    }

    public class UiConfig
    {
        public string Theme { get; set; } = "system";
        public bool ShowHiddenFiles { get; set; } = false;
        public bool ShowItemCheckBoxes { get; set; } = true;
        public bool ConfirmDelete { get; set; } = true;
        public string DefaultViewMode { get; set; } = "Details";

        // 壁紙・カスタム背景設定
        public string BackgroundImagePath { get; set; } = "";
        public double BackgroundOpacity { get; set; } = 0.35;
        public string BackgroundFit { get; set; } = "UniformToFill";
        public double BackgroundTintOpacity { get; set; } = 0.3;
    }

    public enum ArchiveCompressionLevel
    {
        Store = 0,    // 無圧縮
        Fast = 1,     // 高速
        Normal = 2,   // 標準
        Ultra = 3     // 最高圧縮
    }

    public class ShellMenuConfig
    {
        // アプリ標準機能
        public bool ShowOpenWith { get; set; } = true;
        public bool ShowEditWithEditor { get; set; } = true;
        public bool ShowOpenInTerminal { get; set; } = true;
        public bool ShowCopyPath { get; set; } = true;
        public bool ShowZipOptions { get; set; } = true;
        public ArchiveCompressionLevel DefaultZipLevel { get; set; } = ArchiveCompressionLevel.Normal;
        public ArchiveCompressionLevel DefaultSevenZipLevel { get; set; } = ArchiveCompressionLevel.Normal;
        public bool ShowProperties { get; set; } = true;
        public bool ShowOsStandardOption { get; set; } = true;

        // OS標準メニューからの抽出項目 (細かく設定)
        public bool ShowAllShellItems { get; set; } = true;     // すべてのサードパーティ/拡張機能項目を自動検出
        public bool ShowGoogleDrive { get; set; } = true;       // Google ドライブ / Gemini
        public bool ShowPeaZip { get; set; } = true;            // PeaZip
        public bool ShowSevenZip { get; set; } = true;          // 7-Zip / WinRAR
        public bool ShowQuickShare { get; set; } = true;        // Quick Shareで送信
        public bool ShowPowerRename { get; set; } = true;       // PowerRename
        public bool ShowRotateImage { get; set; } = true;       // 右に回転 / 左に回転
        public bool ShowPhotoEdit { get; set; } = true;         // フォトで編集 / Designer
        public bool ShowThirdPartyArchiver { get; set; } = true;// アーカイバ共通
        public bool ShowDefenderScan { get; set; } = true;      // Microsoft Defender でスキャン
        public bool ShowPrint { get; set; } = true;             // 印刷
        public bool ShowSetDesktopBackground { get; set; } = true; // デスクトップ背景に設定
        public bool ShowSendTo { get; set; } = true;            // 送る(N)
        public bool ShowGoogleSearch { get; set; } = true;      // Search with Google
        public string CustomShellKeywords { get; set; } = "";   // 抽出する追加キーワード (カンマ区切り)
        public string ExcludedShellKeywords { get; set; } = ""; // 非表示にする除外キーワード (カンマ区切り)

        /// <summary>
        /// 自動検出された OS 右クリックメニュー項目の可視性辞書 (項目名 -> 表示オンオフ)
        /// </summary>
        public Dictionary<string, bool> ItemVisibilityState { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 自作コンテキストメニュー項目の表示順序リスト
        /// </summary>
        public List<string> MenuOrder { get; set; } = new();
    }

    public class FolderViewSetting
    {
        public string ViewMode { get; set; } = "Details";
        public int ViewScale { get; set; } = 1; // 0=Compact, 1=Normal, 2=Large
        public int CustomSize { get; set; } = 48; // 無段階アイコン・アイテムピクセルサイズ
    }

    public class ShortcutConfig
    {
        /// <summary>
        /// アクションIDとカスタムショートカットキー文字列の対応辞書
        /// (例: "NewFolder" -> "Ctrl+Shift+N", "Rename" -> "F2")
        /// </summary>
        public Dictionary<string, string> CustomShortcuts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public class WindowStateConfig
    {
        public bool IsMaximized { get; set; } = false;
        public int Width { get; set; } = 1100;
        public int Height { get; set; } = 700;
        public int? X { get; set; }
        public int? Y { get; set; }
    }

    public class SystemIntegrationConfig
    {
        public bool ReplaceDefaultExplorer { get; set; } = false;
        public bool AddContextMenuToFolders { get; set; } = false;
        public bool InterceptWinE { get; set; } = false;
    }

    public class UpdateConfig
    {
        public string GitHubOwner { get; set; } = "SK519";
        public string GitHubRepo { get; set; } = "FastExplorer";
        public bool AutoCheckOnStartup { get; set; } = true;
    }

    public class AppConfig
    {
        public EditorConfig Editor { get; set; } = new();
        public TerminalConfig Terminal { get; set; } = new();
        public StartupConfig Startup { get; set; } = new();
        public CacheConfig Cache { get; set; } = new();
        public UiConfig Ui { get; set; } = new();
        public ShellMenuConfig ShellMenu { get; set; } = new();
        public ShortcutConfig Shortcuts { get; set; } = new();
        public WindowStateConfig WindowState { get; set; } = new();
        public SystemIntegrationConfig SystemIntegration { get; set; } = new();
        public UpdateConfig Update { get; set; } = new();
        public List<string> CustomPinnedFolders { get; set; } = new();
        public List<string> TypedPaths { get; set; } = new();
        public Dictionary<string, FolderViewSetting> FolderViewSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    [JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppConfig))]
    [JsonSerializable(typeof(EditorConfig))]
    [JsonSerializable(typeof(TerminalConfig))]
    [JsonSerializable(typeof(StartupConfig))]
    [JsonSerializable(typeof(CacheConfig))]
    [JsonSerializable(typeof(UiConfig))]
    [JsonSerializable(typeof(ShellMenuConfig))]
    [JsonSerializable(typeof(ShortcutConfig))]
    [JsonSerializable(typeof(WindowStateConfig))]
    [JsonSerializable(typeof(SystemIntegrationConfig))]
    [JsonSerializable(typeof(UpdateConfig))]
    [JsonSerializable(typeof(ArchiveCompressionLevel))]
    [JsonSerializable(typeof(FolderViewSetting))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, FolderViewSetting>))]
    public partial class AppConfigJsonContext : JsonSerializerContext
    {
    }
}

