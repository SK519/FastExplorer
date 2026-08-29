# FastExplorer 仕様書

## 1. 概要 & システム目標
FastExplorer は、標準の Windows 11 エクスプローラー特有の遅延やオーバーヘッドを克服するために設計された、**超軽量・爆速な次世代タブ型ファイルマネージャー**です。
**WinUI 3 (Windows App SDK)**、**.NET 10**、および C++ ネイティブ常駐コア（**FastExplorerWatcher**）を採用し、OS カーネル API (`kernel32.dll` / `user32.dll` / `shell32.dll` / `ntdll.dll`) および Windows Shell COM インターフェースと直接連携することで、**実質 0 秒の即時起動**、ガベージコレクション（GC）のオーバーヘッド最小化、遅延のないフォルダー移動、Windows 11 既定エクスプローラーの完全代替（ゼロチラつきリダイレクト）、そして Fluent Design (Mica) に完全準拠したモダン UI を提供します。

---

## 2. 目標スペック & パフォーマンス指標

| 指標 | 実装仕様 / 目標値 | 実装戦略 |
| :--- | :--- | :--- |
| **起動時間** | **実質 0 秒** (初回起動 約0.3〜0.5秒) | バックグラウンド待機 + **名前付きパイプ通信による即時タブ化・前面化** |
| **フォルダー走査速度** | **15ms 未満 / 1万ファイル** | `FindFirstFileExW`（`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`） |
| **UI レンダリング** | **60 / 120 FPS 維持** | WinUI 画面仮想化 (`ListView` / `GridView`) + `{x:Bind}` 最適化 |
| **メモリ消費 (操作時)** | 100MB 未満 | Win32 構造体の直接割り当て、不要な `System.IO` 重複オブジェクトの排除 |
| **メモリ消費 (待機時)** | 30MB 未満 | ウィンドウ非表示時のグラフィックリソースおよびキャッシュ解放 |
| **アイコン / サムネイル描画** | UI 遅延 0ms | 2段階遅延読み込み（バックグラウンドワーカースレッド + 2,000件 LRU キャッシュ） |
| **コンテキストメニュー応答** | 即時表示 (0ms) | WinUI ネイティブ `Flyout` + バックグラウンド COM 抽出セッション |
| **シェルリダイレクト遅延** | **0ms (ゼロチラつき)** | `FastExplorerWatcher` による検知時画面外退避（`-32000, -32000`）+ 完全透明化 |

---

## 3. 技術スタック & ビルド構成

* **GUI Framework:** WinUI 3 (Windows App SDK 1.6+)
  * Mica 背景マテリアル (`MicaBackdrop`)
  * タイトルバー統合 (`ExtendsContentIntoTitleBar = true`)
  * `{x:Bind}` コンパイル時バインディングによる高速化
* **ランタイム / 言語:** .NET 10 (`net10.0-windows10.0.19041.0`) / C# 13
  * COM 相互運用サポート (`<BuiltInComInteropSupport>true</BuiltInComInteropSupport>`)
  * アンセーフコード許可 (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`)
  * 対応プラットフォーム: `win-x64`, `win-arm64`
* **ネイティブ常駐コア (Watcher):** C++20 (MSVC Native Win32)
  * 低レベルキーボードフック (`WH_KEYBOARD_LL`) による `Win + E` 横取り
  * WinEventHook (`SetWinEventHook`) による標準エクスプローラー捕捉
  * 名前付きパイプ (`FastExplorer_SingleInstance_Pipe`) クライアント
* **コアファイル I/O:** Win32 API (`kernel32.dll`, `shell32.dll`, `user32.dll`, `ntdll.dll`, `uxtheme.dll`) の P/Invoke
* **プロセス間通信 (IPC):** Win32 Named Pipe + Global Mutex (`Local\FastExplorer_SingleInstance_Mutex`)
* **ファイルシステム変更監視:** `System.IO.FileSystemWatcher` (Debounce 制御による連続イベント統合)
* **クリップボード連携:** Win32 `CF_HDROP` + `Preferred DropEffect` による外部エクスプローラーとの双方向カット/コピー/貼り付け
* **設定ファイル管理:** JSON 形式 (`config.json`)。`System.Text.Json` の高速シリアライズ
* **パッケージング & インストーラー:** Inno Setup 7 (PowerShell スクリプト `build_installer.ps1` による一括自動化)

---

## 4. 主要機能仕様

### 4.1 UI & ナビゲーションシステム

* **タイトルバー一体型タブインターフェース:**
  * WinUI 3 の `AppTitleBar` / `TabView` により、タイトルバー領域に直接タブを配置。
  * 右端に Windows 11 ネイティブキャプションボタン（最小化・最大化・閉じる）を配置し、Snap Layouts にも完全対応。
  * **タブドラッグ＆ドロップ操作 (`TabDragDropService`):** タブのドラッグによる順序並べ替え、およびウィンドウ外へのドラッグによる**新規独立ウィンドウへの分離**を完全サポート。
  * タブごとの独立した閲覧履歴スタック（`_backStack`, `_forwardStack`）、ソート状態、フィルター状態、カレントパスを保持。
  * `Ctrl+T` で新規タブ作成、`Ctrl+W` でアクティブタブのクローズ。
  * `Ctrl+,` または設定ボタンで専用設定タブ (`FastExplorer://Settings`) をシームレスに開閉。
* **ツールバー構成:**
  * **ナビゲーションバー:** 「戻る (Alt+Left)」「進む (Alt+Right)」「最新の情報に更新 (F5)」「パンくず / アドレス入力バー」「フィルター検索ボックス (Ctrl+F)」「設定ボタン (Ctrl+,)」。
  * **アクションツールバー:** エクスプローラー上部に「新規作成 (フォルダー/各種ファイル)」「切り取り (Ctrl+X)」「コピー (Ctrl+C)」「貼り付け (Ctrl+V)」「名前の変更 (F2)」「削除 (Delete)」「プロパティ (Alt+Enter)」および**右端に「表示 (特大/大/中/小アイコン/一覧/詳細/タイル/コンテンツ/隠しファイル表示)」ドロップダウンボタン**を配置。
* **ステータスバー:**
  * アイテム数・合計サイズ表示に加え、右端に「詳細表示」「アイコン表示 (大/中)」「縮小 (Ctrl+-)」「拡大 (Ctrl++)」のクイックボタンを配置。
* **多彩なファイル・フォルダー表示モード (View Modes) & サイズスライダー:**
  * **大型・Files App 風レイアウト Flyout (ツールバー右端「表示」):**
    * 幅 380px の開放的でモダンな Fluent 2 カードレイアウト。
    * **5つの基本レイアウトカード**: 「詳細」「グリッド (アイコン)」「一覧」「タイル」「コンテンツ」の直感的な大型ボタン（選択中モードのハイライト表示）。
    * **無段階・自由可変サイズスライダー (Smooth Slider)**: 20px 〜 160px の範囲でミリ単位で自由にスライド操作可能。リアルタイムに「48 px」「96 px」等のピクセル値が追従表示され、表示サイズが無段階に変更可能。
    * 隠しアイテム表示トグルスイッチ。
  * **グリッド / アイコン表示 (Grid Icons):** スライダー操作によって小・中・大・特大アイコンをシームレスに変更可能な統一グリッド表示。
  * **一覧 (List):** 複数列のコンパクトリスト表示（スライダーでサイズ可変）
  * **詳細 (Details):** 列ヘッダー（名前・更新日時・種類・サイズ）付きのテーブル表示（スライダーでサイズ可変）
  * **タイル (Tiles):** アイコン + ファイル名・ファイル種類/サイズの横並びカード表示（スライダーでサイズ可変）
  * **コンテンツ (Content):** アイコン + 更新日時・サイズを複数行でリッチに表示するリスト（スライダーでサイズ可変）
  * **フォルダー別表示モード・サイズ記憶:** 各フォルダーで変更した表示モードや無段階サイズ値（CustomSize）を自動記憶。ピクチャフォルダー（自動で大アイコン）やドキュメントフォルダー（詳細）など、フォルダーごとに最適な表示形式を永続化。
  * **シームレス・ズーム (Ctrl + マウスホイール / Ctrl + + / Ctrl + - / Ctrl + 0):** 全 21 段階のきめ細やかなスケール遷移を完全サポート。
* **インタラクティブ・パンくずアドレスバー:**
  * 各階層フォルダーのボタンをクリックして瞬時に上位階層へジャンプ。
  * 各階層の右側にある矢印（`>`）をクリックすると、**同階層のサブフォルダー一覧がドロップダウン表示**され、直接子フォルダーへジャンプ可能。「PC」階層では全ドライブ一覧がドロップダウンに展開。
  * パンくずバーのクリックまたは `Ctrl+L` で直接パス入力（TextBox）モードに即時切り替え。
  * **手動入力パス履歴の記憶 (`TypedPathsService`):** アドレスバーに手動で打ち込んだパスのみを記憶・保存（MRU 形式で最大20件永続化）。アドレスバー右端のドロップダウンボタン（▼）や入力時の `Down` キーで過去の入力履歴一覧を展開し、ワンクリックで移動可能。履歴クリア機能も搭載。
  * パス直打ち入力時、フォルダーだけでなくファイルパスが入力された場合は、関連付けられたデフォルトアプリでファイルを開き、親フォルダーへ自動ナビゲート。
* **サイドバー (ナビゲーションペイン):**
  * **ホーム (最上部):** システムルート・ピン留めフォルダー・最近使った項目へのクイックアクセス。
  * **ピン留めセクション (標準 Explorer 連携 / `QuickAccessService`):** 標準 Windows Explorer のクイックアクセス / ホームにピン留めされたフォルダー群と完全同期。右クリックからピン留め / ピン留め解除が可能。
  * **PC (ThisPC):** ドライブ一覧の表示。
  * **ネットワーク:** ネットワーク共有の参照。
  * **WSL (Linux) ディストリビューション:** インストール済み Linux ディストリビューションを自動検出し登録。
* **画面右プレビュー & 簡易プロパティペイン (Alt+P):**
  * 選択アイテムのリアルタイム・非同期プレビュー表示（写真/画像、テキスト/コード/設定ファイル、高解像度アプリアイコン）。
  * 簡易プロパティ情報表示（種類、サイズ/ディスクサイズ、更新日時、作成日時、属性、場所、画像解像度、パスのワンクリックコピー）。
  * 複数アイテム選択時の集計（項目数、フォルダー数、ファイル数、合計サイズ）表示。
  * ツールバーおよびキーボードショートカット (`Alt+P`) での即時表示/非表示切り替え。
* **仮想化ファイルリスト & ソート / フィルター:**
  * `ListView` (Details, Content) / `GridView` (Icons, List, Tiles) の UI 仮想化により数万件のファイルもスムーズに表示。
  * 列構成 (Details): 「名前」「更新日時」「種類」「サイズ」。各列ヘッダーのクリックで昇順 / 降順ソート。
  * **一括全選択チェックボックス (Select All):** 「名前」ヘッダー列の左端に全選択/選択解除チェックボックスを配置。ワンクリックで表示中アイテムの全選択・全解除が可能。
  * **インクリメンタル検索フィルター (Ctrl+F):** 入力と同時にファイル一覧をリアルタイム絞り込み（`Esc` で即時フォーカス解除・クリア）。
  * **インライン名前変更 (F2):** リスト内およびグリッド内で直接ファイル名を編集可能。拡張子を除いたファイル名部分のみを初期選択。
* **完全なマウス & キーボードショートカット操作 (`ShortcutService`):**
  * すべての主要キーボードショートカットを自由にカスタマイズ可能。

---

### 4.2 高速ファイル走査 & 2段階遅延読み込み

大量のファイルを含むフォルダーでも UI スレッドを一切ブロックしない 2 段階の非同期パイプラインを採用：

1. **Phase 1 (最優先テキスト描画):**
   * Win32 の `FindFirstFileExW` (`FindExInfoBasic`) を使用し、ファイル名、サイズ、属性、タイムスタンプを C# レベルで極限まで高速走査。1万件規模のフォルダーでも 15ms 未満で取得し、リストを即座に描画。
2. **Phase 2 (バックグラウンド非同期アイコン & サムネイル生成):**
   * UI スレッドの負荷を防ぐため、専用ワーカースレッド (`IconThumbnailService`) に `BlockingCollection<FileItem>` 経由でリクエストをキューイング。
   * `SHGetFileInfoW` / `SHGetStockIconInfo` / `IShellItemImageFactory` による 256×256 高解像度システムアイコン取得、および画像サムネイルの生成とデコードを実行。
   * 結果はメモリ上の **LRU キャッシュ (最大 2,000 件 / `ConcurrentDictionary` + `LinkedList`)** に保持。

---

### 4.3 高速コンテキストメニュー & OS シェルメニュー統合

標準 Windows 11 の遅延原因であるシェル拡張の読み込みオーバーヘッドを排除し、独自の高速メニューを提供：

* **選択アイテム用コンテキストメニュー (上部アイコンバー付き):**
  * **最上部アイコンバー:** 切り取り、コピー、貼り付け、名前の変更、削除 (ゴミ箱)、プロパティ。
  * **標準機能エリア:** 「開く」「プログラムから開く...」「テキストで編集」「ターミナルで開く」「ZIP/7z 圧縮・展開」「プロパティ」。
  * **動的 OS シェルメニュー抽出 & 統合 (中央エリア / `ActiveShellMenuSession`):**
    * `IContextMenu` / `IContextMenu2` / `IContextMenu3` をバックグラウンド COM セッションで初期化。
    * サードパーティ拡張（7-Zip, PeaZip, WinRAR, Google ドライブ, Quick Share, PowerRename, Defender スキャン, デスクトップ背景設定, 送る 等）を自動検出し、WinUI メニューとして統合。
    * 設定画面でのドラッグ並び替え（`MenuOrder`）に完全連動。
  * **最下部固定エリア:** 「その他のオプションを表示 (Shift+右クリック)」から OS 標準コンテキストメニューを呼び出し可能。
* **背景 / 余白用コンテキストメニュー:**
  * 表示、並び替え、最新の情報に更新 (F5)、貼り付け (Ctrl+V)、新規作成（フォルダー/各種ファイル）、ターミナルで開く、プロパティ。

---

### 4.4 アクションツールバー & 新規作成 (ShellNew)

* **動的テンプレート新規作成 (`ShellNewService`):**
  * Windows レジストリ (`HKEY_CLASSES_ROOT\.ext\ShellNew` 等) を走査し、OS にインストールされている Office ドキュメントや各種アプリの新規作成テンプレート（Word, Excel, PowerPoint, 各種テキスト等）を動的に検出。
  * 作成直後、リスト上で対象項目を自動選択し、拡張子手前まで選択された状態でインライン名前変更モードへ即時遷移。

---

### 4.5 詳細プロパティウィンドウ (`PropertiesWindow`)

WinUI 3 ネイティブのダイアログウィンドウとして実装され、OS 標準プロパティ以上の詳細情報を提供：

* **一般タブ:** アイコン、項目名、ファイル種類、関連付けプログラム、場所、サイズ、ディスク上のサイズ、作成/更新/アクセス日時、属性切り替え。
* **ドライブ専用ビュー:** ドライブ名、種類、ファイルシステム、使用領域・空き領域円グラフ。
* **セキュリティタブ:** ファイル / フォルダーの ACL（アクセス制御リスト）を読み取り、プリンシパルごとの権限一覧を表示。
* **詳細メタデータタブ:** 画像 EXIF、音声 / 動画メタデータ、オフィスドキュメント情報。
* **デジタル署名タブ:** 実行ファイルの Authenticode デジタル署名（署名者、アルゴリズム、タイムスタンプ）を解析表示。
* **ハッシュ / チェックサムタブ:** SHA-256、MD5、SHA-1 ハッシュ値を非同期計算しワンクリックコピー。

---

### 4.6 設定画面 (`SettingsControl`)

タブ型 UI 内で `FastExplorer://Settings` としてシームレスに動作する、Windows 11 Fluent Design に準拠した高機能設定管理インターフェース：

* **モダン 6 カテゴリ ナビゲーション:**
  1. 🎨 **外観と表示:** テーマ選択（システム標準 / ダーク / ライト）、カスタム壁紙設定（透過度・フィット・ティント）、項目チェックボックス、隠しファイル表示、削除確認ダイアログ。
  2. ⌨️ **キーボードショートカット (`ShortcutService`):** 全キーバインドのカスタマイズ、リアルタイム検索、キー入力レコーダー、一括リセット。
  3. 📋 **基本コンテキストメニュー:** 標準項目の表示/非表示、ZIP/7-Zip 規定圧縮レベル。
  4. 🧩 **右クリック拡張:** OS 拡張項目のアプリ別自動グループ化、折りたたみアコーディオン、ドラッグ＆ドロップ並び替え。
  5. 🛠️ **外部ツール & システム統合 (`SystemIntegrationService`):** 外部エディタ・ターミナル設定、**「FastExplorer を既定のエクスプローラーにする」ワンクリック切り替え**。
  6. ℹ️ **詳細とシステム情報:** キャッシュクリア、全設定初期化リセット。

---

### 4.7 クリップボード & アーカイブ & ファイル操作

* **Win32 ネイティブ `CF_HDROP` クリップボード連携:** 外部エクスプローラーとの相互ファイルコピー / 切り取り / 貼り付け（`Preferred DropEffect` 対応）。
* **安全なゴミ箱移動 & 完全削除 (`RecycleBinService`):** `SHFileOperationW` によるゴミ箱移動、`Shift + Delete` 完全削除。
* **ZIP / 7z 圧縮 & 展開 (`ArchiveService`):** 4段階圧縮レベル（無圧縮/高速/標準/最高圧縮）、フォルダー展開。

---

### 4.8 バックグラウンド常駐 & Windows 11 完全統合システム

Windows 11 のあらゆるフォルダーオープン要求を FastExplorer にシームレス転送するデュアルプロセス・アーキテクチャ：

```
[ ユーザー操作 / OS イベント ]
  │
  ├── 1. [Win + E] キー押下
  │      └──> FastExplorerWatcher.exe (LowLevelKeyboardProc)
  │             └──> Named Pipe 経由で FastExplorer に転送
  │
  ├── 2. スタートメニュー「ファイルの場所を開く」 / Windows 検索
  │      └──> AppContainer が DelegateExecute ({11dbb47c-a525-400b-9e80-a54615a090c0}) を経由して explorer.exe 起動
  │             └──> FastExplorerWatcher.exe (SetWinEventHook) が CabinetWClass を検知
  │                    ├── 1フレーム目で画面外退避 (-32000, -32000) & 完全透明化 (Alpha=0) & SW_HIDE (ゼロチラつき)
  │                    ├── NtQueryInformationProcess (PEB) または IShellWindows から対象パスを 0〜10ms で抽出
  │                    ├── explorer.exe を即座にクローズ (WM_CLOSE)
  │                    └── Named Pipe 経由で FastExplorer にパスを転送
  │
  └── 3. フォルダーのダブルクリック / 外部アプリからの呼び出し
         └──> レジストリ (Directory\shell\open\command) 経由で FastExplorer.exe "パス" が直接起動
```

* **レジストリ統合 (`SystemIntegrationService`):**
  * `HKLM` および `HKCU` の `Software\Classes\Directory\shell\open\command` および `Drive\shell\open\command` に FastExplorer を登録。
  * `DelegateExecute` に正規の Explorer GUID `{11dbb47c-a525-400b-9e80-a54615a090c0}` を設定し、Windows 11 の UWP / AppContainer サンドボックスからのフォルダーオープン要求のブロックを完全回避。

---

### 4.9 単一インスタンス制御 & 名前付きパイプ通信

* **カスタム Main エントリポイント (`Program.cs`):**
  * `Local\FastExplorer_SingleInstance_Mutex` による単一インスタンス管理。
  * 既存インスタンスが存在する場合、名前付きパイプ（`FastExplorer_SingleInstance_Pipe`）へコマンドライン引数を送信して即時終了。
* **引数の徹底正規化 (`CleanArgumentPath`):**
  * Win32 コマンドラインの末尾バックスラッシュ引用符エスケープバグ（`"C:\"` → `C:"`）を C# / C++ の両面で完全修復。
  * `/select,"パス"` 形式の引数を自動解析し、対象フォルダーを開いて指定ファイルを自動選択。
* **高優先度 UI ディスパッチ (`App.xaml.cs`):**
  * パイプ受信時、`DispatcherQueuePriority.High` で UI スレッドに即時ディスパッチし、最小化解除（`Restore`）＋ 最前面化（`ForceForegroundWindow`）＋ 新規タブ作成（`CreateNewTab`）を超低遅延で実行。

---

## 5. アーキテクチャ & データフロー

```
[ OS / Windows Shell / ショートカット ]
   │
   ├── Win+E / CabinetWClass 生成
   │     ▼
   │  [ FastExplorerWatcher.exe (C++ Native Win32 Core) ]
   │     ├── WH_KEYBOARD_LL (Win+E 横取り)
   │     ├── SetWinEventHook (CabinetWClass ゼロチラつき捕捉 & 画面外退避)
   │     └── NtQueryInformationProcess (PEB コマンドライン抽出)
   │           │
   │           ▼ (Named Pipe: FastExplorer_SingleInstance_Pipe)
   └── Directory\shell\open (レジストリ直起動)
         │
         ▼
[ FastExplorer.exe (Program.cs: Main & IPC サーバー) ]
   ├── Global Mutex 単一インスタンス制御
   └── 引数正規化 (CleanArgumentPath / /select 抽出)
         │
         ▼ (DispatcherQueuePriority.High)
[ WinUI 3 UI レイヤー (x:Bind 最適化) ]
   ├── タイトルバー統合 & TabView (タブ & ドラッグ＆ドロップ分離 & 独立履歴)
   ├── パンくずアドレスバー (BreadcrumbBar & サブフォルダー展開 & 手動履歴記憶)
   ├── アクションツールバー (新規作成 / 編集 / 削除 / プロパティ / 表示スライダー)
   ├── 画面仮想化 FileListView (ソート / インライン名前変更 / リアルタイム検索フィルター)
   ├── 設定タブホスト (SettingsControl: 外観 / ショートカット / 右クリック拡張 / 既定化)
   ├── 詳細プロパティウィンドウ (PropertiesWindow: 一般 / ドライブ / セキュリティ / 詳細 / 署名 / ハッシュ)
   └── Windows 11 風コンテキストメニュー (MenuFlyout / Flyout)
         │
         ├─────────────────────────────────────────────────┐
         ▼                                                 ▼
[ コアサービスレイヤー (Services/) ]               [ Win32 Native エンジン (Core/) ]
   ├── SystemIntegration/ (既定エクスプローラー登録)  ├── NativeFileScanner (FindFirstFileExW 高速走査)
   ├── Shell/ (ActiveShellMenuSession COM抽出)        ├── Win32Interop (P/Invoke 定義 & シェル API)
   ├── Icon/ (IconThumbnailService 2段階遅延読み込み) └── Win32Interop.Shell (シェル COM インターフェース)
   ├── Archive/ (ArchiveService ZIP/7z 圧縮展開)
   ├── RecycleBin/ (RecycleBinService ゴミ箱管理)
   ├── FileOperation/ (CF_HDROP クリップボード操作)
   ├── QuickAccess/ (クイックアクセス同期)
   ├── TypedPathsService (手動入力パス MRU 記憶)
   ├── ShortcutService (キーバインドカスタマイズ)
   ├── TabDragDropService (タブドラッグ分離)
   └── ConfigService (JSON 設定保存)
```

---

## 6. `config.json` スキーマ仕様

```json
{
  "editor": {
    "path": "notepad.exe",
    "args": [
      "{filePath}"
    ]
  },
  "terminal": {
    "path": "wt.exe",
    "args": [
      "-d",
      "{dirPath}"
    ]
  },
  "startup": {
    "residentOnBoot": true,
    "defaultPath": "ThisPC"
  },
  "cache": {
    "maxEntries": 2000,
    "maxMemoryMB": 50
  },
  "ui": {
    "theme": "system",
    "showHiddenFiles": false,
    "showItemCheckBoxes": true,
    "confirmDelete": true,
    "defaultViewMode": "Details",
    "backgroundImagePath": "C:\\path\\to\\wallpaper.png",
    "backgroundOpacity": 0.35,
    "backgroundFit": "UniformToFill",
    "backgroundTintOpacity": 0.3
  },
  "shellMenu": {
    "showOpenWith": true,
    "showEditWithEditor": true,
    "showOpenInTerminal": true,
    "showCopyPath": true,
    "showZipOptions": true,
    "showProperties": true,
    "showOsStandardOption": true,
    "showAllShellItems": true,
    "showGoogleDrive": true,
    "showPeaZip": true,
    "showSevenZip": true,
    "showQuickShare": true,
    "showPowerRename": true,
    "showRotateImage": true,
    "showPhotoEdit": true,
    "showThirdPartyArchiver": true,
    "showDefenderScan": true,
    "showPrint": true,
    "showSetDesktopBackground": true,
    "showSendTo": true,
    "showGoogleSearch": true,
    "customShellKeywords": "",
    "excludedShellKeywords": "",
    "itemVisibilityState": {},
    "menuOrder": []
  }
}
```

---

## 7. ディレクトリ構成 & ファイル責務一覧

```
FastExplorer/
├── FastExplorer.csproj               # プロジェクト定義 (.NET 10, WinUI 3 設定)
├── app.manifest                      # アプリケーションマニフェスト (DPI Awareness, OS 互換性)
├── Package.appxmanifest              # MSIX マニフェスト (パッケージング宣言)
├── config.json                       # 永続化設定ファイル (JSON 形式)
├── Program.cs                        # カスタム Main エントリポイント & Named Pipe IPC サーバー
├── App.xaml / App.xaml.cs            # アプリケーション初期化 & 高優先度 UI ディスパッチ
├── MainWindow.xaml / MainWindow.xaml.cs # メインウィンドウ XAML レイアウト & 初期化
├── installer.iss                     # Inno Setup 7 インストーラースクリプト
├── build_installer.ps1               # .NET 発行 + C++ Watcher ビルド + Inno Setup 一括自動化スクリプト
├── build_watcher.ps1                 # FastExplorerWatcher.exe 個別ビルドスクリプト
│
├── Watcher/                          # C++ ネイティブ常駐コア
│   └── FastExplorerWatcher.cpp       # Win+E 横取り / CabinetWClass ゼロチラつき捕捉 & パイプ転送
│
├── Views/                            # ビューおよびウィンドウ実装
│   ├── MainWindow/                   # MainWindow の機能別 partial class 群
│   │   ├── MainWindow.Window.cs          # ウィンドウ設定・タイトルバー Mica 統合・グローバルキーバインド
│   │   ├── MainWindow.Sidebar.cs         # サイドバー構築 (ドライブ/特殊フォルダー/クラウド/WSL 自動検出)
│   │   ├── MainWindow.Tabs.cs            # タブの作成・削除・切り替え・設定タブ連携・ツールバー同期
│   │   ├── MainWindow.Breadcrumbs.cs     # パンくずバー・サブフォルダー展開ドロップダウン・アドレス直接入力
│   │   ├── MainWindow.FileList.cs        # 仮想化リスト操作・キーボードショートカット・ソート・インラインリネーム
│   │   ├── MainWindow.ContextMenu.cs     # Win11 風メニュー動的構築・OS 拡張メニュー抽出統合・新規作成メニュー
│   │   └── MainWindow.ContextMenuActions.cs # メニューアクション実装 (CF_HDROP/ZIP/ゴミ箱/プロパティ呼び出し)
│   ├── Properties/                   # 詳細プロパティウィンドウ
│   │   ├── PropertiesWindow.xaml         # プロパティダイアログ XAML (一般/ドライブ/セキュリティ/詳細/署名/ハッシュ)
│   │   └── PropertiesWindow.xaml.cs      # ダイアログウィンドウ制御・テーマ同期・DPI スケール調整
│   └── Settings/                     # 設定画面
│       ├── SettingsControl.xaml          # 設定タブ XAML (縦タブ・トグルカード・ドラッグ並び替えパネル)
│       └── SettingsControl.xaml.cs       # 設定ロード/保存・ドラッグ＆ドロップ並び替え処理 (60FPS オートスクロール)
│
├── Core/                             # ネイティブ Win32 / COM I/O エンジン
│   ├── NativeFileScanner.cs          # FindFirstFileExW による超高速ファイル走査 & ドライブ列挙
│   ├── Win32Interop.cs               # Win32 P/Invoke 構造体・定数・API 関数定義 (kernel32/user32/shell32)
│   └── Win32Interop.Shell.cs         # シェル COM インターフェース定義 (IContextMenu, IShellFolder, PIDL 関連)
│
├── Services/                         # アプリケーション & バックグラウンドサービス群
│   ├── SystemIntegration/            # 既定エクスプローラー & レジストリ統合
│   │   └── SystemIntegrationService.cs   # HKLM/HKCU レジストリ登録・DelegateExecute 設定・既定化切り替え
│   ├── Archive/                      # アーカイブ圧縮・展開サービス
│   │   └── ArchiveService.cs             # ZIP / 7z 圧縮・展開・4段階圧縮レベル制御
│   ├── FileOperation/                # ファイル操作サービス
│   │   └── FileOperationService.cs       # CF_HDROP クリップボード連携・プログラム起動
│   ├── Icon/                         # アイコン & サムネイルサービス
│   │   └── IconThumbnailService.cs       # 2段階遅延読み込みワーカースレッド & 2,000件 LRU キャッシュ管理
│   ├── QuickAccess/                  # クイックアクセス同期サービス
│   │   └── QuickAccessService.cs         # 標準 Explorer ピン留めフォルダー完全同期
│   ├── RecycleBin/                   # ごみ箱管理サービス
│   │   └── RecycleBinService.cs          # SHFileOperationW による安全なゴミ箱移動 & 完全削除
│   ├── Shell/                        # シェルメニュー & COM 拡張サービス
│   │   ├── ActiveShellMenuSession.cs     # IContextMenu バックグラウンドセッション構築 & サブメニュー展開
│   │   ├── ExtractedShellItem.cs         # 抽出シェルメニュー項目データモデル
│   │   ├── ShellContextMenuService.cs    # OS ネイティブ TrackPopupMenuEx 呼び出し & ウィンドウサブクラス化
│   │   ├── ShellMenuFilter.cs            # シェルメニュー項目のフィルタリング・除外・アイコングリフ判定
│   │   ├── ShellCommandExecutor.cs       # シェルコマンド実行 (InvokeCommand / 直接プロセス起動)
│   │   ├── ShellComHelper.cs             # PIDL 解決 & IShellFolder / IContextMenu 取得ヘルパー
│   │   └── ShellNewService.cs            # レジストリ ShellNew テンプレート走査 & 新規ファイル生成
│   ├── Update/                       # アップデート確認サービス
│   │   └── UpdateService.cs              # GitHub Releases 等の更新確認
│   ├── ShortcutService.cs            # キーボードショートカットカスタマイズサービス
│   ├── TabDragDropService.cs         # タブのドラッグ＆ドロップ並べ替え & 新規ウィンドウ分離
│   ├── TypedPathsService.cs          # アドレスバー手動入力パス MRU 記憶サービス
│   └── ConfigService.cs              # JSON 設定の読み込み・保存
│
├── Models/                           # データモデル & データコンテキスト
│   ├── AppConfig.cs                  # 設定データ構造体
│   ├── FileItem.cs                   # ファイル/フォルダー項目モデル (INotifyPropertyChanged・アイコンバインディング)
│   ├── NavigationTabItem.cs          # タブ状態・閲覧履歴スタック・FileSystemWatcher 変更監視
│   ├── BreadcrumbItem.cs             # パンくずリスト項目モデル
│   ├── FilePropertiesInfo.cs         # 詳細プロパティ情報モデル (一般/ACL/EXIF/ハッシュ/デジタル署名)
│   └── FilePropertiesInfo.Loaders.cs # プロパティ情報のバックグラウンド非同期ローダー群
│
└── Helpers/                          # 共通ユーティリティ & 拡張メソッド
    └── VisualTreeExtensions.cs       # VisualTreeHelper 拡張メソッド (親/子 XAML 要素探索)
```