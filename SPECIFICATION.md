# FastExplorer 仕様書

## 1. 概要 & システム目標
FastExplorer は、標準の Windows 11 エクスプローラー特有の遅延やオーバーヘッドを克服するために設計された、軽量・超高速な次世代タブ型ファイルマネージャーです。
**WinUI 3 (Windows App SDK)** と **.NET 10 (Native AOT)** を採用し、OS カーネル API (`kernel32.dll`) および Windows Shell COM インターフェースと直接連携することで、**バックグラウンド常駐による実質 0 秒の即時起動**、ガベージコレクション（GC）のオーバーヘッド最小化、遅延のないフォルダー移動、そして Windows 11 のデザイン言語（Mica / Fluent Design）に完全準拠したモダン UI を提供します。

---

## 2. 目標スペック & パフォーマンス指標

| 指標 | 実装仕様 / 目標値 | 実装戦略 |
| :--- | :--- | :--- |
| **起動時間** | **実質 0 秒** (初回起動 約0.3〜0.5秒) | Native AOT コンパイル + **バックグラウンド常駐によるウィンドウ即時表示** |
| **フォルダー走査速度** | **15ms 未満 / 1万ファイル** | `FindFirstFileExW`（`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`） |
| **UI レンダリング** | **60 / 120 FPS 維持** | WinUI 画面仮想化 (`ListView`) + `{x:Bind}` 最適化 |
| **メモリ消費 (操作時)** | 100MB 未満 | Win32 構造体の直接割り当て、不要な `System.IO` 重複オブジェクトの排除 |
| **メモリ消費 (待機時)** | 30MB 未満 | ウィンドウ非表示時のグラフィックリソースおよびキャッシュ解放 |
| **アイコン / サムネイル描画** | UI 遅延 0ms | 2段階遅延読み込み（バックグラウンドワーカースレッド + 2000件 LRU キャッシュ） |
| **コンテキストメニュー応答** | 即時表示 (0ms) | WinUI ネイティブ `Flyout` + バックグラウンド COM 抽出セッション |

---

## 3. 技術スタック & ビルド構成

* **UI Framework:** WinUI 3 (Windows App SDK 2.3.1 / Microsoft.Windows.SDK.BuildTools 10.0.28000.2526)
  * Mica 背景マテリアル (`MicaBackdrop`)
  * タイトルバー統合 (`ExtendsContentIntoTitleBar = true`)
  * `{x:Bind}` コンパイル時バインディングによる高速化 & AOT 適合
* **ランタイム / コンパイラ:** .NET 10 (`net10.0-windows10.0.19041.0`)
  * Native AOT 有効 (`<PublishAot>true</PublishAot>`)
  * COM 相互運用サポート (`<BuiltInComInteropSupport>true</BuiltInComInteropSupport>`)
  * トリムアナライザー有効 (`<EnableTrimAnalyzer>true</EnableTrimAnalyzer>`)
  * アンセーフコード許可 (`<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`)
  * 対応プラットフォーム: `win-x64`, `win-arm64`
* **コアファイル I/O:** Win32 API (`kernel32.dll`, `shell32.dll`, `user32.dll`, `uxtheme.dll`) の P/Invoke
* **ファイルシステム変更監視:** `System.IO.FileSystemWatcher` (Debounce 制御による連続イベント統合)
* **クリップボード連携:** Win32 `CF_HDROP` + `Preferred DropEffect` による外部エクスプローラーとの双方向カット/コピー/貼り付け
* **設定ファイル管理:** JSON 形式 (`config.json`)。`System.Text.Json` の AOT ソースジェネレーター (`AppConfigJsonContext`) によるリフレクション不要な高速シリアライズ
* **パッケージング / 配布:** MSIX パッケージング対応 (`windows.startupTask` による OS ログオン時自動常駐)

---

## 4. 主要機能仕様

### 4.1 UI & ナビゲーションシステム

* **タイトルバー一体型タブインターフェース:**
  * WinUI 3 の `AppTitleBar` / `TabView` により、タイトルバー領域に直接タブを配置。
  * 右端に Windows 11 ネイティブキャプションボタン（最小化・最大化・閉じる）を配置し、Snap Layouts にも完全対応。
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
  * **画像向きモード限定プレビュー & 高精細アイコン抽出:** グリッド表示やタイル表示の時のみ画像・動画サムネイルを生成。また Google ドライブ（`G:\` など）や各ドライブ、フォルダー、アプリに対しては `IShellItemImageFactory`（`SIIGBF_ICONONLY`）による 256×256 ピクセルの高画質ベクター/高解像度アイコンを優先取得。従来 Win32 API の 32×32 ピクセル制限によるボヤけを防止し、くっきり高画質なアイコンを表示。
  * **フォルダー別表示モード・サイズ記憶:** 各フォルダーで変更した表示モードや無段階サイズ値（CustomSize）を自動記憶。ピクチャフォルダー（自動で大アイコン）やドキュメントフォルダー（詳細）など、フォルダーごとに最適な表示形式を永続化。
  * **シームレス・ズーム (Ctrl + マウスホイール / Ctrl + + / Ctrl + - / Ctrl + 0):** 全 21 段階のきめ細やかなスケール遷移により、同一表示形式内のサイズ微調整から形式間のスムーズな移行までを完全サポート。
  * **インタラクティブ・パンくずアドレスバー:**
    * 各階層フォルダーのボタンをクリックして瞬時に上位階層へジャンプ。
    * 各階層の右側にある矢印（`>`）をクリックすると、**同階層のサブフォルダー一覧がドロップダウン表示**され、直接子フォルダーへジャンプ可能。「PC」階層では全ドライブ一覧がドロップダウンに展開。
    * パンくずバーのクリックまたは `Ctrl+L` で直接パス入力（TextBox）モードに即時切り替え。
    * **手動入力パス履歴の記憶 (Typed Paths):** アドレスバーに手動で打ち込んだパスのみを記憶・保存（MRU 形式で最大20件永続化）。アドレスバー右端のドロップダウンボタン（▼）や入力時の `Down` キーで過去の入力履歴一覧を展開し、ワンクリックで移動可能。履歴クリア機能も搭載。
    * パス直打ち入力時、フォルダーだけでなくファイルパスが入力された場合は、関連付けられたデフォルトアプリでファイルを開き、親フォルダーへ自動ナビゲート。
* **サイドバー (ナビゲーションペイン):**
  * **ホーム (最上部):** システムルート・ピン留めフォルダー・最近使った項目へのクイックアクセス。
  * **ピン留めセクション (標準 Explorer 連携):** 標準 Windows Explorer のクイックアクセス / ホームにピン留めされたフォルダー群と完全同期。右クリックからピン留め / ピン留め解除が可能。
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
  * **一括全選択チェックボックス (Select All):** 「名前」ヘッダー列の左端に全選択/選択解除チェックボックスを配置。ワンクリックで表示中アイテムの全選択・全解除が可能。アイテムの選択状態に応じて「全選択（✓）」「一部選択（−）」「未選択（空）」の 3 状態がリアルタイムに自動連動。
  * **インクリメンタル検索フィルター (Ctrl+F):** 入力と同時にファイル一覧をリアルタイム絞り込み。
  * **インライン名前変更 (F2):** リスト内およびグリッド内で直接ファイル名を編集可能。拡張子を除いたファイル名部分のみを初期選択。
* **完全なマウス & キーボードショートカット操作:**

| ショートカット | 機能 |
| :--- | :--- |
| **Enter** | 選択フォルダーへ移動 / デフォルトアプリでファイルを開く |
| **Alt + Enter** | 選択項目（または現在フォルダー）の詳細プロパティウィンドウを表示 |
| **Alt + P** | 画面右プレビュー & 簡易プロパティペインの表示 / 非表示切り替え |
| **Backspace** | 親フォルダーへ移動 |
| **F2** | 選択項目のインライン名前変更 |
| **Delete** | 選択項目をゴミ箱へ移動 |
| **Shift + Delete** | 選択項目を完全に削除（確認ダイアログ表示） |
| **F5** | 現在のフォルダーを最新の情報に更新 |
| **Ctrl + C / Ctrl + X / Ctrl + V** | コピー / 切り取り / 貼り付け |
| **Ctrl + Shift + N** | 新規フォルダー作成（作成後自動的に名前変更モードに移行） |
| **Ctrl + T / Ctrl + W** | 新規タブ作成 / タブを閉じる |
| **Ctrl + F** | フィルター検索ボックスにフォーカス |
| **Ctrl + L** | アドレス直接入力モードへ切り替え |
| **Ctrl + H** | 隠しファイルの表示 / 非表示切り替え |
| **Ctrl + A** | 全ての項目を選択 |
| **Ctrl + + / Ctrl + -** | 表示サイズの拡大 / 縮小 |
| **Ctrl + 0** | 表示サイズを標準にリセット |
| **Ctrl + マウスホイール** | 連続的な表示サイズズーム拡大・縮小 |
| **Ctrl + ,** | 設定タブを開く |
| **Alt + Left / Alt + Right** | 履歴の「戻る」/「進む」 |
| **Shift + 右クリック** | OS 標準のネイティブコンテキストメニューを直接呼び出し |

---

### 4.2 高速ファイル走査 & 2段階遅延読み込み

大量のファイルを含むフォルダーでも UI スレッドを一切ブロックしない 2 段階の非同期パイプラインを採用：

1. **Phase 1 (最優先テキスト描画):**
   * Win32 の `FindFirstFileExW` (`FindExInfoBasic`) を使用し、ファイル名、サイズ、属性、タイムスタンプを C# レベルで極限まで高速走査。1万件規模のフォルダーでも 15ms 未満で取得し、リストを即座に描画。
2. **Phase 2 (バックグラウンド非同期アイコン & サムネイル生成):**
   * UI スレッドの負荷を防ぐため、専用ワーカースレッド (`IconThumbnailService`) に `BlockingCollection<FileItem>` 経由でリクエストをキューイング。
   * `SHGetFileInfoW` / `SHGetStockIconInfo` によるシステムアイコン取得、および画像サムネイルの生成とデコードを実行。
   * 結果はメモリ上の **LRU キャッシュ (最大 2,000 件 / `ConcurrentDictionary` + `LinkedList`)** に保持。
   * **厳密なキャッシュキー分離:** 特殊 PC、各ドライブ、個別フォルダー、拡張子別アイコン、個別画像/EXE/LNK ファイルを厳密にキー分離。
   * **スレッド初期化同期:** `ManualResetEventSlim` により UI `DispatcherQueue` の初期化完了を待機し、アイコンの取りこぼしを完全に排除。

---

### 4.3 高速コンテキストメニュー & OS シェルメニュー統合

標準 Windows 11 の遅延原因であるシェル拡張の読み込みオーバーヘッドを排除し、独自の高速メニューを提供：

* **選択アイテム用コンテキストメニュー (上部アイコンバー付き):**
  * **最上部アイコンバー:** 切り取り、コピー、貼り付け、名前の変更、削除 (ゴミ箱)、プロパティ。
  * **標準機能エリア:**
    * 「開く」
    * 「プログラムから開く...」（OS のアプリ選択ダイアログ呼び出し）
    * 「テキストで編集」（設定したエディタで開く）
    * 「ターミナルで開く」（Windows Terminal / PowerShell）
    * 「ZIP ファイルに圧縮 (.zip)」/「7z ファイルに圧縮 (.7z)」/「圧縮オプション (4段階レベル: 無圧縮/高速/標準/最高圧縮)」
    * 「ここに展開」/「"{フォルダー名}" に展開」(ZIP, 7-Zip, RAR, TAR, GZ, BZ2, XZ 等のアーカイブ選択時)
  * **動的 OS シェルメニュー抽出 & 統合 (中央エリア):**
    * `IContextMenu` / `IContextMenu2` / `IContextMenu3` をバックグラウンド COM セッション (`ActiveShellMenuSession`) で初期化。
    * `WM_INITMENUPOPUP` メッセージの送信により、PeaZip や Google ドライブ等の遅延生成サブメニューも完全展開。
    * サードパーティ拡張（7-Zip, PeaZip, WinRAR, Google ドライブ, Quick Share, PowerRename, Defender スキャン, デスクトップ背景設定, 送る 等）を自動検出し、WinUI メニューとして統合。
    * 各項目の表示/非表示トグル、および設定画面でのドラッグ並び替え（`MenuOrder`）に完全連動。
  * **最下部固定エリア:** 「その他のオプションを表示 (Shift+右クリック)」から OS 標準コンテキストメニューを呼び出し可能。
* **背景 / 余白用コンテキストメニュー:**
  * 表示（詳細、隠しファイルの表示トグル）
  * 並び替え（名前、更新日時、種類、サイズ）
  * 最新の情報に更新 (F5)
  * 貼り付け (Ctrl+V)
  * 新規作成（フォルダー、テキストドキュメント、レジストリ `ShellNew` テンプレート）
  * ターミナルで開く / プロパティ

---

### 4.4 アクションツールバー & 新規作成 (ShellNew)

* **動的テンプレート新規作成 (`ShellNewService`):**
  * Windows レジストリ (`HKEY_CLASSES_ROOT\.ext\ShellNew` 等) を走査し、OS にインストールされている Office ドキュメントや各種アプリの新規作成テンプレート（Word, Excel, PowerPoint, 各種テキスト等）を動的に検出。
  * アクションツールバーの「新規作成」ボタンおよび右クリックメニューから直接テンプレートファイルを新規作成可能。
  * 作成直後、リスト上で対象項目を自動選択し、拡張子手前まで選択された状態でインライン名前変更モードへ即時遷移。

---

### 4.5 詳細プロパティウィンドウ (`PropertiesWindow`)

WinUI 3 ネイティブのダイアログウィンドウとして実装され、OS 標準プロパティ以上の詳細情報を提供：

* **対象別の最適化表示:**
  * 単一ファイル / 単一フォルダー / 複数項目選択 / ドライブ の 4 パターンに自動適応。
* **一般タブ:**
  * アイコン、項目名（編集可能）、ファイルの種類、関連付けプログラム（「変更」ボタンでアプリ選択ダイアログ呼び出し）、場所、サイズ、ディスク上のサイズ。
  * フォルダー / 複数選択時: 含まれるファイル数・フォルダー数のバックグラウンド非同期計算。
  * 作成日時、更新日時、アクセス日時。
  * 属性（「読み取り専用」「隠しファイル」）のチェックボックス切り替えと「適用」/「OK」時のディスク書き込み。
* **ドライブ専用ビュー:**
  * ドライブ名、種類、ファイルシステム（NTFS, FAT32 等）、使用領域・空き領域・合計容量、使用率円グラフ表示。
* **セキュリティタブ:**
  * ファイル / フォルダーの ACL（アクセス制御リスト）を読み取り、ユーザーおよびグループプリンシパル（SYSTEM, Administrators, Users 等）ごとの権限（フルコントロール、変更、読み取りと実行、読み取り、書き込み、特殊）を一覧表示。
* **詳細メタデータタブ:**
  * 画像 EXIF（カメラ機種、絞り値、ISO、露出時間、焦点距離、解像度等）。
  * 音声 / 動画メタデータ（再生時間、ビットレート、オーディオ/ビデオコーデック等）。
  * オフィスドキュメント情報、実行ファイルのバージョン情報等をカテゴリ別に表示。
* **デジタル署名タブ:**
  * 実行ファイルやインストーラーの Authenticode デジタル署名（署名者名、ダイジェストアルゴリズム、タイムスタンプ）を解析表示。
* **ハッシュ / チェックサムタブ:**
  * ファイルの SHA-256、MD5、SHA-1 ハッシュ値をバックグラウンドで非同期計算し、ワンクリックでクリップボードへコピー可能。

---

### 4.6 設定画面 (`SettingsControl`)

タブ型 UI 内で `FastExplorer://Settings` としてシームレスに動作する、Windows 11 Fluent Design に準拠した高機能設定管理インターフェース：

* **モダン 6 カテゴリ ナビゲーション:**
  1. 🎨 **外観と表示:**
     * テーマ選択（システム標準 / ダーク / ライト）。
     * **カスタム壁紙設定 (透過度・フィット・ティント調整):** お好みの画像（PNG/JPG/WebP/BMP）を背景に設定可能。不透明度（5%〜100%）、背景ティント（0%〜80%）、配置方法（アスペクト比維持全体 / 全体収める / 引き伸ばし / 等倍）をリアルタイム調整。
     * 項目チェックボックス表示トグル。
     * 隠しファイルの表示 / 非表示トグル。
     * ファイル削除時の確認ダイアログ表示トグル。
  2. ⌨️ **キーボードショートカット (カスタマイズ & 録音入力):**
     * すべての主要操作（ファイル操作、ナビゲーション、タブ、表示ズーム等）のキーバインドを自由に変更可能。
     * 操作名・キーでのリアルタイム検索およびカテゴリ別絞り込み。
     * **キー入力レコーダー (Key Capture Flyout):** キーを押すだけで自動キャプチャし、重複競合時はアラート警告。
     * 個別および全体の「初期値に戻す」ワンクリックリセット対応。
  3. 📋 **基本コンテキストメニュー (標準機能):**
     * 標準メニュー項目の表示 / 非表示トグル（プログラムから開く、テキストで編集、ターミナルで開く、パスのコピー、ZIP/7-Zip 圧縮・展開、プロパティ、OS 標準メニュー）。
     * ZIP および 7-Zip の規定圧縮レベル（Ultra, Normal, Fast, Store）個別設定。
  4. 🧩 **右クリック拡張 (Shift+右 検出項目 & アプリ別グループ化):**
     * OS 右クリックから自動検出された項目をアプリ・ベンダー別に自動グループ化（7-Zip, PeaZip, Google ドライブ, WinRAR, 画像ツール等）。
     * アプリごとの折りたたみアコーディオン & 有効バッジ表示（例: `4/4 有効`）により長大スクロールを完全解消。
     * 状態フィルター（すべて / 有効のみ / 無効のみ）とリアルタイム検索ボックス。
     * 「すべてON」「すべてOFF」「おすすめ構成」「リセット」一括メンテナンス操作。
     * **ドラッグ＆ドロップ並び替え:** 検出項目の表示順序（`MenuOrder`）をドラッグ操作で直感的に変更可能。
  5. 🛠️ **外部ツール設定:**
     * 「テキストで編集」で使用する外部エディタの実行ファイルパス（.exe ピッカー参照機能付き）。
     * 「ターミナルで開く」で使用するターミナルアプリ（Windows Terminal, PowerShell 等）の実行ファイルパス。
  6. ℹ️ **詳細とシステム情報:**
     * サムネイル・アイコンメモリキャッシュのクリアおよび最大メモリ容量設定。
     * FastExplorer アプリケーション情報、全設定初期化リセット機能。

---

### 4.7 クリップボード & ファイル操作

* **Win32 ネイティブ `CF_HDROP` クリップボード連携:**
  * アプリ内だけでなく、標準 Windows エクスプローラーや外部ツールとの間で完全な相互ファイルコピー / 切り取り / 貼り付けをサポート。
  * `Preferred DropEffect`（1 = Copy, 2 = Move）を登録し、切り取り後の貼り付けによる移動処理を確実に実行。
* **安全なゴミ箱移動 & 完全削除:**
  * `SHFileOperationW` (`FO_DELETE`, `FOF_ALLOWUNDO`) を使用した OS ゴミ箱への安全な移動。
  * `Shift + Delete` による即時完全削除（確認ダイアログ付き）。
* **ZIP 圧縮 & 展開:**
  * 選択した複数ファイル / フォルダーの単一 ZIP アーカイブへの圧縮。
  * ZIP ファイルの現在のフォルダーへの一括展開。

---

### 4.8 バックグラウンド常駐 & システム連携

* **システムトレイ常駐:**
  * ウィンドウを閉じてもプロセスはバックグラウンドで待機し、ホットキーやトレイアイコンから呼び出された際にウィンドウを再表示することで、**実質 0 秒の起動**を実現。
* **OS 起動時自動起動:**
  * MSIX の `windows.startupTask` 拡張により、Windows ログオン時に自動でバックグラウンド常駐を開始。

---

## 5. アーキテクチャ & データフロー

```
[ システムトレイ常駐マネージャー ]
   ├── OS 起動時の自動起動 (startupTask)
   └── トレイアイコン / ウィンドウ表示・非表示制御
         │
         ▼
[ WinUI 3 UI レイヤー (x:Bind による AOT 最適化) ]
   ├── タイトルバー統合 & TabView (タブ & 独立した履歴管理)
   ├── パンくずアドレスバー (BreadcrumbBar & サブフォルダー展開 & 直接入力)
   ├── アクションツールバー (新規作成 / 編集 / 削除 / プロパティ)
   ├── 画面仮想化 FileListView (ソート / インライン名前変更 / フィルター)
   ├── 設定タブホスト (SettingsControl: 外観 / メニュー並び替え / 外部連携)
   ├── 詳細プロパティウィンドウ (PropertiesWindow: 一般 / セキュリティ / 詳細 / ハッシュ)
   └── Windows 11 風コンテキストメニュー (MenuFlyout / Flyout)
         │
         ├─────────────────────────────────────────────────┐
         ▼                                                 ▼
[ コアサービスレイヤー (Services) ]                [ Win32 Native エンジン (Core) ]
   ├── ActiveShellMenuSession (COM抽出 & サブメニュー展開)├── NativeFileScanner (FindFirstFileExW 高速走査)
   ├── IconThumbnailService (2段階遅延読み込み & LRUキャッシュ)├── Win32Interop (P/Invoke 定義 & シェル API)
   ├── FileOperationService (CF_HDROP / ゴミ箱 / ZIP / 実行) └── Win32Interop.Shell (シェル COM インターフェース)
   ├── ShellNewService (レジストリ走査 & テンプレート作成)
   ├── ShellContextMenuService (ネイティブメニュー委譲)
   └── ConfigService (AOT ソースジェネレーター JSON 保存)
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

## 7. ディレクトリ構成 & ファイル分割方針

### 7.1 ファイル分割 & アーキテクチャ設計原則

保守性・可読性および Native AOT ビルドの最適化を担保するため、以下のファイル分割・設計方針を採用しています：

1. **単一責任の原則 (SRP) とファイル規模の抑制:**
   * 各ソースファイルは原則として **最大 600 行以内** に収まるよう責務ごとに分割。
   * クラスが肥大化しやすい UI やデータローダーは `partial class` や専用サービスクラスに分離。
2. **MainWindow の機能別 `partial class` 分割 (`Views/MainWindow/`):**
   * ウィンドウ制御、サイドバー、タブ管理、パンくずアドレスバー、ファイルリスト操作、コンテキストメニュー構築、メニューアクションの 7 つのファイルに責務を分割。
3. **ネイティブ Win32 / COM I/O のカプセル化 (`Core/` & `Services/`):**
   * 生のポインタ操作や Win32 P/Invoke 構造体は `Core/` に集約。
   * シェル COM (`IContextMenu`) 処理は `ShellComHelper` (PIDL・インターフェース取得)、`ActiveShellMenuSession` (セッション・サブメニュー展開)、`ShellMenuFilter` (フィルタ・グリフ判定)、`ShellCommandExecutor` (コマンド実行)、`ShellContextMenuService` (委譲・ネイティブ呼び出し) のように細分化。
4. **モデル定義と非同期ロード処理の分離 (`Models/`):**
   * プロパティ情報のように多岐にわたるメタデータ処理は、プロパティ定義・変更通知を行う `FilePropertiesInfo.cs` と、ACL/ハッシュ/EXIF/署名等を非同期取得する `FilePropertiesInfo.Loaders.cs` に分割。

---

### 7.2 ディレクトリ構成 & ファイル責務一覧

```
FastExplorer/
├── FastExplorer.csproj               # プロジェクト定義 (.NET 10, WinUI 3, Native AOT 設定)
├── app.manifest                      # アプリケーションマニフェスト (DPI Awareness, OS 互換性)
├── Package.appxmanifest              # MSIX パッケージ宣言 (windows.startupTask 自動常駐等)
├── config.json                       # 永続化設定ファイル (JSON 形式)
├── App.xaml / App.xaml.cs            # アプリケーションエントリポイント & ライフサイクル管理
├── MainWindow.xaml / MainWindow.xaml.cs # メインウィンドウ XAML レイアウト & 初期化
│
├── Views/                            # ビューおよびウィンドウ実装
│   ├── MainWindow/                   # MainWindow の機能別 partial class 群 (各ファイル 80〜500 行)
│   │   ├── MainWindow.Window.cs          # ウィンドウ設定・タイトルバー Mica 統合・グローバルキーバインド
│   │   ├── MainWindow.Sidebar.cs         # サイドバー構築 (ドライブ/特殊フォルダー/クラウド/WSL 自動検出)
│   │   ├── MainWindow.Tabs.cs            # タブの作成・削除・切り替え・設定タブ連携・ツールバー同期
│   │   ├── MainWindow.Breadcrumbs.cs     # パンくずバー・サブフォルダー展開ドロップダウン・アドレス直接入力
│   │   ├── MainWindow.FileList.cs        # 仮想化リスト操作・キーボードショートカット・ソート・インラインリネーム
│   │   ├── MainWindow.ContextMenu.cs     # Win11 風メニュー動的構築・OS 拡張メニュー抽出統合・新規作成メニュー
│   │   └── MainWindow.ContextMenuActions.cs # メニューアクション実装 (CF_HDROP/ZIP圧縮展開/ゴミ箱/プロパティ呼び出し)
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
├── Services/                         # アプリケーション & バックグラウンドサービス
│   ├── ConfigService.cs              # JSON 設定の読み込み・保存・AOT シリアライズ
│   ├── IconThumbnailService.cs       # 2段階遅延読み込みワーカースレッド & 2,000件 LRU キャッシュ管理
│   ├── FileOperationService.cs       # CF_HDROP クリップボード連携・安全なゴミ箱移動・ZIP 圧縮展開・プログラム起動
│   ├── ShellNewService.cs            # レジストリ ShellNew テンプレート走査 & 新規ファイル生成
│   ├── ActiveShellMenuSession.cs     # IContextMenu バックグラウンドセッション構築 & サブメニュー展開
│   ├── ExtractedShellItem.cs         # 抽出シェルメニュー項目データモデル
│   ├── ShellContextMenuService.cs    # OS ネイティブ TrackPopupMenuEx 呼び出し & ウィンドウサブクラス化
│   ├── ShellMenuFilter.cs            # シェルメニュー項目のフィルタリング・除外・アイコングリフ判定
│   ├── ShellCommandExecutor.cs       # シェルコマンド実行 (InvokeCommand / 直接プロセス起動)
│   └── ShellComHelper.cs             # PIDL 解決 & IShellFolder / IContextMenu 取得ヘルパー
│
├── Models/                           # データモデル & データコンテキスト
│   ├── AppConfig.cs                  # 設定データ構造体 & AOT JsonSourceGenerator (AppConfigJsonContext)
│   ├── FileItem.cs                   # ファイル/フォルダー項目モデル (INotifyPropertyChanged・アイコンバインディング)
│   ├── NavigationTabItem.cs          # タブ状態・閲覧履歴スタック・FileSystemWatcher 変更監視
│   ├── BreadcrumbItem.cs             # パンくずリスト項目モデル
│   ├── FilePropertiesInfo.cs         # 詳細プロパティ情報モデル (一般/ACL/EXIF/ハッシュ/デジタル署名)
│   └── FilePropertiesInfo.Loaders.cs # プロパティ情報のバックグラウンド非同期ローダー群
│
├── Helpers/                          # 共通ユーティリティ & 拡張メソッド
│   └── VisualTreeExtensions.cs       # VisualTreeHelper 拡張メソッド (親/子 XAML 要素探索)
│
├── Controls/                         # カスタム UI コントロール用 (拡張用)
├── ViewModels/                       # MVVM ViewModel 用 (拡張用)
├── Plugins/                          # 拡張プラグイン用 (拡張用)
└── Resources/                        # スタイル・リソース辞書用 (拡張用)
```