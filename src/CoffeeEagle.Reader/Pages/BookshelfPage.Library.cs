using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage
{

    private async Task AddLibraryAsync(bool preferGoogleDrive = false)
    {
        var uri = await MainActivity.PickDocumentTreeAsync();
        if (uri is null)
        {
            return;
        }

        var treeUri = uri.ToString() ?? string.Empty;
        if (preferGoogleDrive && !_indexer.IsGoogleDriveTree(treeUri))
        {
            var continueAdd = await DisplayAlertAsync(
                "Google Driveではありません",
                "選択されたフォルダはGoogle Drive Providerではありません。",
                "このまま追加",
                "選び直す");
            if (!continueAdd)
            {
                return;
            }
        }

        await IndexLibraryAsync(treeUri, previous: null);
    }


    private async Task AddGoogleDriveApiLibraryAsync()
    {
        if (_isBusy)
        {
            return;
        }

        var clientId = await DisplayPromptAsync(
            "Google Drive API",
            "Google OAuth Client IDを入力してください。",
            initialValue: string.IsNullOrWhiteSpace(_state.GoogleDriveClientId) ? GoogleDriveLibraryService.DefaultClientId : _state.GoogleDriveClientId,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var folderInput = await DisplayPromptAsync(
            "Google Drive API",
            "EAGLE .libraryフォルダのURLまたはフォルダIDを入力してください。",
            initialValue: _state.GoogleDriveFolderId ?? string.Empty,
            keyboard: Keyboard.Text);
        if (string.IsNullOrWhiteSpace(folderInput))
        {
            return;
        }

        var folderId = GoogleDriveLibraryService.ExtractFolderId(folderInput);
        if (string.IsNullOrWhiteSpace(folderId))
        {
            await DisplayAlertAsync("Google Drive API", "フォルダIDを読み取れませんでした。", "OK");
            return;
        }

        _state.GoogleDriveClientId = clientId.Trim();
        _state.GoogleDriveClientSecret = null;
        _state.GoogleDriveFolderId = folderId;
        await SaveStateAsync();

        if (!await _drive.HasRefreshTokenAsync())
        {
            var connect = await DisplayAlertAsync(
                "Google Drive接続",
                "Googleログインを開き、Driveの読み取りを許可します。",
                "接続",
                "キャンセル");
            if (!connect)
            {
                return;
            }

            BeginSyncStatus("Google Drive認証中", "Googleアカウント");
            var progress = CreateSyncProgressReporter();
            SetBusy(true);
            try
            {
                await _drive.AuthorizeWithBrowserAsync(_state, progress);
                await SaveStateAsync();
                EndSyncStatus();
            }
            catch (Exception ex)
            {
                FailSyncStatus(ex);
                await DisplayAlertAsync("Google Driveに接続できません", CreateSyncFailureMessage(ex), "OK");
                return;
            }
            finally
            {
                SetBusy(false);
            }
        }

        var existingIndex = EagleLibraryIdentity.FindMatchingIndex(
            _libraries,
            GoogleDriveLibraryService.BuildFolderUri(folderId),
            EagleLibrarySourceKind.GoogleDriveApi,
            folderId);
        var existing = existingIndex >= 0 ? _libraries[existingIndex] : null;
        await IndexGoogleDriveApiLibraryAsync(existing);
    }


    private void UpsertLibrary(EagleLibrary library)
    {
        var existingIndex = EagleLibraryIdentity.FindMatchingIndex(_libraries, library);
        if (existingIndex >= 0)
        {
            _libraries[existingIndex] = library;
        }
        else
        {
            _libraries.Insert(0, library);
        }

        var deduplicated = EagleLibraryIdentity.Deduplicate(_libraries, library.Id);
        _libraries.Clear();
        _libraries.AddRange(deduplicated);
    }


    private async Task ShowLibraryMenuAsync()
    {
        var options = _libraries
            .Select(library => new PickerOption(
                library.Id,
                library.Name,
                $"{library.SourceLabel}  {library.Assets.Count:N0} items",
                string.Equals(library.Id, _activeLibrary?.Id, StringComparison.Ordinal),
                "ライブラリ"))
            .Concat([
                new PickerOption("__add_drive_api__", "Google Drive APIフォルダ追加", "Drive APIでEAGLE .libraryを追加", false, "追加"),
                new PickerOption("__add_drive_provider__", "Google Drive Providerフォルダ追加", "AndroidのGoogle Drive Providerから追加", false, "追加"),
                new PickerOption("__add_device__", "端末/同期フォルダ追加", "端末または同期フォルダから追加", false, "追加")
            ])
            .Concat(_libraries.Select(library => new PickerOption(
                RemoveLibraryPrefix + library.Id,
                "削除: " + library.Name,
                "アプリの登録から外します。元ファイルは削除しません",
                false,
                "登録削除",
                IsDestructive: true)))
            .ToList();

        var selected = await ShowOptionPickerAsync("ライブラリ", "切り替え / 追加 / 登録削除", options, searchPlaceholder: "ライブラリを検索");
        if (selected is null)
        {
            return;
        }

        if (selected.Id.StartsWith(RemoveLibraryPrefix, StringComparison.Ordinal))
        {
            await RemoveLibraryAsync(selected.Id[RemoveLibraryPrefix.Length..]);
            return;
        }

        if (selected.Id == "__add_drive_api__")
        {
            await AddGoogleDriveApiLibraryAsync();
            return;
        }

        if (selected.Id == "__add_drive_provider__")
        {
            await AddLibraryAsync(preferGoogleDrive: true);
            return;
        }

        if (selected.Id == "__add_device__")
        {
            await AddLibraryAsync();
            return;
        }

        var library = _libraries.FirstOrDefault(item => string.Equals(item.Id, selected.Id, StringComparison.Ordinal));
        if (library is null)
        {
            return;
        }

        _activeLibrary = library;
        _state.ActiveLibraryId = _activeLibrary.Id;
        _state.SelectedFolderId = AllFoldersId;
        _state.SelectedTags.Clear();
        _state.SelectedTag = null;
        await SaveStateAsync();
        RefreshVisibleAssets();
    }


    private async Task RemoveLibraryAsync(string libraryId)
    {
        var library = _libraries.FirstOrDefault(item => string.Equals(item.Id, libraryId, StringComparison.Ordinal));
        if (library is null)
        {
            return;
        }

        var remove = await DisplayAlertAsync(
            "ライブラリ登録を削除",
            $"{library.Name} をこのアプリの一覧から削除します。Google Drive/端末上の元ファイルは削除しません。",
            "削除",
            "キャンセル");
        if (!remove)
        {
            return;
        }

        var wasActive = string.Equals(_activeLibrary?.Id, library.Id, StringComparison.Ordinal);
        _libraries.RemoveAll(item => string.Equals(item.Id, library.Id, StringComparison.Ordinal));
        if (wasActive)
        {
            _activeLibrary = _libraries.FirstOrDefault();
            _state.ActiveLibraryId = _activeLibrary?.Id;
            _state.SelectedFolderId = AllFoldersId;
            _state.SelectedTags.Clear();
            _state.SelectedTag = null;
        }
        else if (_state.ActiveLibraryId is not null
            && _libraries.All(item => !string.Equals(item.Id, _state.ActiveLibraryId, StringComparison.Ordinal)))
        {
            _activeLibrary = _libraries.FirstOrDefault();
            _state.ActiveLibraryId = _activeLibrary?.Id;
        }

        await SaveStateAsync();
        RefreshVisibleAssets();
    }
}
