# FastExplorer 🚀

FastExplorer は、Windows 11 標準エクスプローラーの遅延やオーバーヘッドを克服するために設計された、**超軽量・爆速な次世代タブ型ファイルマネージャー**です。

**WinUI 3 (Windows App SDK 2.3+)** と **.NET 10**、および C++ ネイティブ常駐コア（**FastExplorerWatcher**）を採用し、Win32 カーネル API とのダイレクト連携によって、実質 0 秒の起動、低メモリ消費、高速フォルダー走査、カーネルレベルの超高速ファイル転送、Fluent Design (Mica) に完全準拠したモダン UI を提供します。

---

## ✨ 主な特徴

### ⚡ 圧倒的なパフォーマンス & ゼロ遅延起動
- **超高速フォルダー走査**: Win32 `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) 活用による 1 万ファイル < 15ms の極小オーバーヘッド走査
- **万単位フォルダーの一括高速展開**: `BulkObservableCollection` による単一 Reset 通知で、数万件のアイテムを UI 仮想化パネルへ一瞬でバッチ反映
- **2段階遅延読み込み**: 走査直後のデフォルトアイコン即時適用 + STA バックグラウンドワーカースレッドによるサムネイル生成 & 2,000 件 LRU キャッシュ
- **低リソース消費**: バックグラウンド待機とシングルインスタンス管理による最小限のメモリ消費
- **爆速タブ生成**: 名前付きパイプ（Named Pipe）と高優先度 UI ディスパッチによる瞬時のタブ作成・前面化

### 📦 カーネルレベルの超高速ファイル転送
- **ReFS / Dev Drive Block Cloning (0 秒複製)**: `FSCTL_DUPLICATE_EXTENTS_TO_FILE` によるエクステント複製を活用し、同一ボリューム内の大容量ファイルも実データ読み書きなし（Copy-on-Write）で実質 0 秒コピー
- **Win32 `CopyFileExW` カーネルダイレクト転送**: ユーザー空間バッファリングを排し、OS カーネルレベルの DMA / ODX オフロード転送により NVMe SSD の最大帯域をフル活用
- **小規模ファイル群の並行パイプライン**: `node_modules` などの大量の小さなファイルを含むフォルダーを最大 8 並行ワーカーで高速並行転送
- **MFT 高速移動**: 同一ドライブ内のファイル・フォルダー移動は MFT ポインタ書き換えのみで瞬時に完了

### 🪟 Windows 11 完全統合（既定エクスプローラー代替）
- **Win+E 完全横取り**: 低レベルキーボードフックによる `Win + E` の即時 FastExplorer 起動
- **シームレスなシェルインターセプト**: スタートメニューの「ファイルの場所を開く」、Windows 検索、Google ドライブ、外部アプリからのフォルダーオープン要求を 0ms で検知
- **ゼロチラつきリダイレクト**: 画面外退避と完全透明化により、標準エクスプローラーのチラつきを 1 ピクセルも出さずに FastExplorer へ自動転送
- **システム全体登録**: HKLM / HKCU レジストリ（`Directory`, `Drive`, `RecycleBin`）の既定ファイルマネージャー登録

### 📑 モダンなタブ & ウィンドウシステム
- **Fluent Design UI**: WinUI 3 + Mica マテリアルによる Windows 11 ネイティブデザイン
- **マルチタブ管理**: タイトルバー統合タブ、タブ別履歴保持、ドラッグ＆ドロップによる並べ替え・ウィンドウ分離
- **柔軟な表示モード**: 特大/大/中/小アイコン、一覧、詳細、タイル、コンテンツ表示 + 無段階サイズスライダー (20px〜160px)
- **インクリメンタル検索**: `Ctrl + F` によるリアルタイムフィルター検索（`Esc` で即時フォーカス解除）
- **インタラクティブ・パンくずアドレスバー**: 階層横の `>` クリックでサブフォルダー一覧へ直接ジャンプ、手動入力パス履歴 (MRU) の保存・展開
- **画面右プレビューペイン (`Alt+P`)**: 画像、テキスト/ソースコード、高解像度アイコンのリアルタイム非同期プレビュー
- **詳細プロパティウィンドウ**: SHA-256 / MD5 ハッシュ非同期計算、セキュリティ ACL 権限、デジタル署名検証、画像 EXIF メタデータ

---

## 🛠️ 技術スタック & アーキテクチャ

| レイヤー | 技術 / 構成 |
| :--- | :--- |
| **GUI Framework** | WinUI 3 (Windows App SDK 2.3+) |
| **Language & Runtime** | C# 13 / .NET 10 (win-x64, win-arm64) |
| **Watcher Daemon** | C++20 (MSVC Native Win32 / Named Pipe / WinEventHook) |
| **Core OS API** | Win32 API (`kernel32.dll`, `shell32.dll`, `user32.dll`, `ntdll.dll`, `uxtheme.dll`) |
| **Installer** | Inno Setup 7 (PowerShell 自動化スクリプト同梱) |

---

## 💻 動作要件

- **OS:** Windows 11 (推奨) または Windows 10 (version 19041 以降)
- **Architecture:** x64 / ARM64
- **ビルドツール:** .NET 10 SDK, Visual Studio 2022/2026 (C++ デスクトップ開発), Inno Setup 7

---

## 🚀 ビルド & インストール手順

### 1. 開発環境でのデバッグ実行

```powershell
# メインアプリケーションのビルド & 実行
dotnet run --project FastExplorer.csproj
```

### 2. Watcher の個別ビルド

```powershell
# C++ MSVC で FastExplorerWatcher.exe をコンパイル
./build_watcher.ps1
```

### 3. 完全インストーラーの作成

```powershell
# .NET 発行 + C++ Watcher ビルド + Inno Setup パッケージングを一括実行
./build_installer.ps1 -Arch x64
```
生成されたインストーラーは `dist\FastExplorer_Setup_v<Version>.exe` および `dist\FastExplorer_Setup.exe` に出力されます。

---

## ⚙️ 既定のエクスプローラー設定

アプリ内の **設定** 画面から「FastExplorer を既定のエクスプローラーにする」をオンにするだけで、Windows のすべてのフォルダーオープン操作が自動的に FastExplorer に切り替わります（オフにすれば Windows 標準エクスプローラーへ安全に復帰します）。

---

## 📄 ライセンス

MIT License
