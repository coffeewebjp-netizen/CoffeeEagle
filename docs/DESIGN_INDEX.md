# Design Index

CoffeeEagle の設計情報を読むための入口です。
毎回すべての docs やビルド生成物を読まず、触る面だけに進みます。

## 読む順番

1. `docs/DESIGN_INDEX.md`
2. `DESIGN.md`
3. 触る機能の `docs/*.md` または `README.md`
4. 関連する C# だけ

Do not search `bin/`, `obj/`, `.tools/`, `.nuget/`, or `dist/`.
CoffeeEagle is Android-only. Do not look for a Studio/PC app.

| 作業 | 先に読む文書 | 主な実装入口 |
| --- | --- | --- |
| いまの方針 | `DESIGN.md` | `src/CoffeeEagle.Reader/` |
| 起動と機能一覧 | `README.md` | `MauiProgram.cs`, `Pages/BookshelfPage.cs` |
| `.library` 索引 | `DESIGN.md` 対象データ | `Services/EagleLibraryIndexer.cs` |
| Google Drive 同期 | `docs/GOOGLE_DRIVE_CREDENTIALS.md` | `Services/GoogleDriveLibraryService.cs` |
| 本棚 | `DESIGN.md` | `Pages/BookshelfPage.cs` |
| 画像ビューア | `DESIGN.md` | `Pages/ViewerPage.cs` |
| 動画 / 音声 | `DESIGN.md` | `Platforms/Android/VideoPlayerActivity.cs`, `Pages/AudioPlayerPage.cs` |
| Android 署名・更新 | `docs/ANDROID_RELEASE.md` | `.tools/`（Git 外）。鍵は再生成しない |

## 文書の役割

- `README.md` は起動・ビルド案内。
- `DESIGN.md` は Android-only 方針とデータ所有。
- `docs/ANDROID_RELEASE.md` は署名と cross-PC 更新の正本。
- `BookshelfPage.cs` / Drive / indexer の分割は、機能改修が必要になるまでしない。
