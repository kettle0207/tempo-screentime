# Tempo のリリース手順（GitHub Releases + アプリ内更新）

アプリ内更新は、GitHub Releases に置いた 2 つのファイルだけを読みます。専用サーバーは不要です。

| ファイル | 役割 |
| --- | --- |
| `Tempo-Setup-x64.exe` | インストーラー本体 |
| `tempo-update.txt` | 署名付きの更新情報（バージョン・ファイル名・サイズ・SHA-256・変更内容） |
| `Tempo-Windows-x64.zip` | ZIP 版（任意。ZIP 版はアプリ内更新の対象外） |

Tempo は「アップデートを確認」が押されたときだけ、次の URL を取得します。

- `https://github.com/<OWNER>/<REPO>/releases/latest/download/tempo-update.txt`
- `https://github.com/<OWNER>/<REPO>/releases/download/v<バージョン>/<ファイル名>`（「ダウンロードして更新」を押したとき）

更新情報は、アプリに埋め込んだ公開鍵（RSA-3072 / RSA-PSS / SHA-256）で検証します。インストーラーは、署名済みの更新情報にあるサイズと SHA-256 で検証してから実行します。

## いちばん簡単な方法（推奨）

公開先は `<あなたの GitHub アカウント>/tempo-screentime`（公開リポジトリ）です。`scripts/publish.ps1` が次の作業をまとめて行います：アカウント名を `UpdateKey.cs` に書き込む、テスト、ビルド、署名、検証、初回のみのリポジトリ作成と push、Release の作成。

```powershell
gh auth login                                   # 初回だけ。ブラウザで GitHub にサインイン
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1 -DryRun   # 公開せずにビルドまで確認
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1           # 公開
```

コミットの作者には、GitHub の no-reply アドレス（`<id>+<アカウント>@users.noreply.github.com`）を使います。メールアドレスは公開されません。`dist/`・`release-keys/`・実機の画面例（`docs/screens/real-*.png`）・実機の履歴スナップショットは `.gitignore` で除外しています。

以下は、手作業で行う場合の手順です。

## 初回だけ行う準備

1. **配布リポジトリを決める。** `src/Core/UpdateKey.cs` の `Repository = "OWNER/tempo-screentime"` の `OWNER` を、自分の GitHub アカウント名に書き換えます（`publish.ps1` を使う場合は自動）。リポジトリは **公開（public）** にしてください。非公開リポジトリの Releases は、認証なしではダウンロードできません。
2. **署名鍵を保管する。** 秘密鍵は `release-keys/update-signing-key.xml` にあります（`.gitignore` 済み）。
   - パスワード管理ツールや暗号化した外部メディアなど、2 か所以上に保管してください。**リポジトリには絶対にコミットしないでください。**
   - この鍵をなくすと、配布済みの Tempo に新しい版を届けられなくなります。その場合は、新しい鍵を埋め込んだ版を「手動でインストールしてもらう」しかありません。
   - 漏えいした場合は、ただちに新しい鍵で版を作り直し、利用者に手動での再インストールを案内してください。
   - 鍵を作り直すコマンドは `dotnet run --project tools/Release/Tempo.Release.csproj -c Release -- keygen <新しい鍵.xml>` です。`UpdateKey.cs` の公開鍵が書き換わります。既存の鍵ファイルは上書きしません。
3. **ビルド環境**（いずれもユーザー単位で winget から導入できます）
   - `Microsoft.DotNet.SDK.8`、`BrechtSanders.WinLibs.POSIX.UCRT`（MinGW-w64）、`NSIS.NSIS`、`Python.Python.3.12`
   - UI テスト用に `python -m pip install playwright==1.49.1`。ブラウザーはインストール済みの Microsoft Edge を使うため、追加のダウンロードはありません。
   - プロジェクトのパスが長い（目安で 190 文字超）と、.NET Framework のビルドが MAX_PATH 制限で失敗します。そのときは `subst T: <プロジェクトのフォルダー>` で短いドライブ名を割り当て、`T:\` からビルドしてください。

## 毎回のリリース手順

以下は PowerShell で、プロジェクトのルートから実行します。

### 1. バージョンと変更内容を決める

- `src/ScreenTime.csproj` の `<Version>` を上げます（例: `1.2.1`）。形式は `X.Y.Z` に限ります。**アプリは、今の版より大きい番号しか提示しません。**
- 必要に応じて、`extension/manifest.json` の `version` と `native/measure.cpp` の表示文字列も同じ番号にします。
- `release-notes.txt` に変更内容を UTF-8 で書きます。`- ` で始まる行は、アプリ内で箇条書きとして表示されます。

```text
- 週の表示で日を選べるようになりました
- 起動時間を短縮しました
```

### 2. テストする

```powershell
dotnet run --project tests/Tempo.Tests.csproj -c Release
python tests/dashboard.py
node --test tests/extension.test.js
```

### 3. ビルドして署名する

```powershell
$env:CXX='g++'; $env:WINDRES='windres'; $env:DLLTOOL='dlltool'
$env:MAKENSIS='C:\Program Files (x86)\NSIS\makensis.exe'
$env:TEMPO_SIGNING_KEY="$PWD\release-keys\update-signing-key.xml"
$env:TEMPO_NOTES="$PWD\release-notes.txt"
python scripts/package.py
```

`dist/` に `Tempo-Setup-x64.exe`、`Tempo-Windows-x64.zip`、`tempo-update.txt` ができます。署名ツールは最後に、アプリに埋め込んだ公開鍵で `tempo-update.txt` を検証します。鍵が一致しなければ、エラーで止まります。

### 4. 公開前に検証する

```powershell
dotnet run --project tools/Release/Tempo.Release.csproj -c Release -- verify dist/tempo-update.txt dist/Tempo-Setup-x64.exe
```

`OK <バージョン> Tempo-Setup-x64.exe` と表示されれば、更新情報とインストーラーは一致しています。

### 5. GitHub Releases に公開する

タグは必ず `v<バージョン>` にします。アプリは、このタグ名からダウンロード先を組み立てます。

```powershell
gh release create v1.2.1 dist/Tempo-Setup-x64.exe dist/tempo-update.txt dist/Tempo-Windows-x64.zip `
  --repo OWNER/tempo-screentime --title "Tempo 1.2.1" --notes-file release-notes.txt --latest
```

- **Pre-release や Draft にしないでください。** `releases/latest` が指すのは、最新の正式リリースだけです。
- アセット名は変えないでください（`tempo-update.txt` と、更新情報に書いたインストーラー名）。
- 公開後に `curl -sIL https://github.com/OWNER/tempo-screentime/releases/latest/download/tempo-update.txt` を実行し、最終応答が `200` になることを確認します。

### 6. 公開後に確認する

1. 1 つ前の版を入れた PC で、「設定」→「アップデートを確認」を押し、新しい版と変更内容が表示されることを確認します。
2. 「ダウンロードして更新」を押し、Tempo が自動で再起動して「Tempo X.Y.Z に更新しました」と表示されることを確認します。
3. 「概要」で、今日の合計が更新前の値から続いていることを確認します。

## 問題が起きたとき

- **配布した版に不具合があった場合**：GitHub で古い版を「latest」に戻しても、利用者の版は下がりません（ダウングレードは提示しない仕様）。修正版を、より大きい番号で公開してください。
- **古い版からは自動更新させたくない場合**：`scripts/package.py` が呼ぶ `sign` の最後の引数に最低バージョンを渡します（`... sign key installer 2.0.0 notes dist 1.2.0`）。それより古い版には「インストーラーを直接実行してください」と表示されます。
- **署名検証に失敗すると表示された場合**：`tempo-update.txt` が、別の鍵で署名されたか、公開後に編集されています。手順 3 から作り直してください。

## テスト用の更新元

`TEMPO_UPDATE_FEED` 環境変数を設定すると、更新元の URL をローカルの模擬サーバー（`http://127.0.0.1:<port>/releases/`）に差し替えられます。HTTPS 以外は、ループバックのときだけ許可されます。差し替えた場合も、埋め込みの公開鍵による署名検証は省略されません。手順は `tests/windows/update_server.py` と `VERIFICATION.md` を参照してください。
