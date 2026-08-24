# FastExplorer 🚀

FastExplorer は、Windows 11 標準エクスプローラーの遅延やオーバーヘッドを克服するために設計された、軽量・超高速な次世代タブ型ファイルマネージャーです。

**WinUI 3 (Windows App SDK)** と **.NET 10 (Native AOT)** を採用し、Win32 カーネル API (`kernel32.dll`) と直接連携することで、実質 0 秒の起動、低メモリ消費、高速フォルダー走査、Fluent Design (Mica) に完全準拠したモダン UI を提供します。

---

## ✨ 主な特徴

- ⚡ **超高速走査 & ゼロ遅延起動**: `FindFirstFileExW` 活用による 1 万ファイル < 15ms 走査と常駐による即時表示
- 🎨 **Modern Fluent UI**: WinUI 3 + Mica マテリアル、Windows 11 に最適化された洗練されたデザイン
- 📑 **マルチタブ & 分割ビュー**: タイトルバー統合タブ、タブ別履歴保持、直感的なナビゲーション
- 🔍 **豊富な表示モード & プレビュー**: 特大/大/中/小アイコン、一覧、詳細、タイル、コンテンツ表示 + サイズスライダー
- 🛠️ **実用的なツール群**: 外部エクスプローラー連携クリップボード、詳細プロパティ、フィルター検索 (Ctrl+F)
- ⚙️ **Native AOT 対応**: 高速な起動と軽量なリソース消費

---

## 🛠️ 技術スタック

- **UI Framework:** WinUI 3 (Windows App SDK)
- **Runtime / Language:** .NET 10 / C# 13 (Native AOT)
- **Core API:** Win32 API (`kernel32.dll`, `shell32.dll`, `user32.dll`, `uxtheme.dll`)
- **Packaging & Installer:** MSIX / Inno Setup

---

## 💻 動作要件

- **OS:** Windows 10 (version 19041 以降) または Windows 11
- **Architecture:** x64 / ARM64
- **.NET SDK:** .NET 10 SDK (ビルド時)

---

## 🚀 ビルド & 実行方法

### 開発環境での実行

```powershell
# プロジェクトのビルド・実行
dotnet run --project FastExplorer.csproj
```

### ネイティブ AOT パブリッシュ

```powershell
# x64 向け AOT リリースビルド
dotnet publish -c Release -r win-x64
```

### インストーラー作成

```powershell
# PowerShell スクリプトでインストーラービルド
./build_installer.ps1
```

---

## 📄 ライセンス

MIT License (or your chosen license)
