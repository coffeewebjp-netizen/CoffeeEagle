using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage
{

    private Border CreateSyncStatusPanel()
    {
        var phaseRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { _syncPhaseLabel, _syncPercentLabel }
        };
        Grid.SetColumn(_syncPercentLabel, 1);

        return new Border
        {
            IsVisible = false,
            Margin = new Thickness(0, 2, 0, 0),
            Padding = new Thickness(11, 9),
            Stroke = Color.FromArgb("#246A60"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#10201F"),
            Content = new VerticalStackLayout
            {
                Spacing = 5,
                Children = { phaseRow, _syncTargetLabel, _syncProgressBar, _syncMetaLabel }
            }
        };
    }


    private IProgress<LibrarySyncProgress> CreateSyncProgressReporter()
    {
        var generation = _syncGeneration;
        return new UiProgress<LibrarySyncProgress>(progress =>
        {
            if (_acceptSyncProgress && generation == _syncGeneration)
            {
                UpdateSyncStatus(progress);
            }
        });
    }


    private void BeginSyncStatus(string phase, string? target = null)
    {
        _syncGeneration++;
        _acceptSyncProgress = true;
        _syncStartedAt = DateTimeOffset.Now;
        _syncLastUpdatedAt = _syncStartedAt;
        _syncStatusPanel.Stroke = Color.FromArgb("#246A60");
        _syncStatusPanel.BackgroundColor = Color.FromArgb("#10201F");
        _syncPhaseLabel.TextColor = Color.FromArgb("#21C7A8");
        _syncTargetLabel.TextColor = Colors.White;
        _syncProgressBar.ProgressColor = Color.FromArgb("#21C7A8");
        _syncProgressBar.Progress = 0;
        _syncProgressBar.IsVisible = false;
        _syncPercentLabel.TextColor = Color.FromArgb("#21C7A8");
        _syncPercentLabel.IsVisible = false;
        _syncStatusPanel.IsVisible = true;
        UpdateSyncStatus(new LibrarySyncProgress(phase, target));

        if (_syncTimer is null)
        {
            _syncTimer = Dispatcher.CreateTimer();
            _syncTimer.Interval = TimeSpan.FromSeconds(1);
            _syncTimer.Tick += (_, _) => RefreshSyncMeta();
        }

        if (!_syncTimer.IsRunning)
        {
            _syncTimer.Start();
        }
    }


    private void UpdateSyncStatus(LibrarySyncProgress progress)
    {
        _lastSyncProgress = progress;
        _syncLastUpdatedAt = DateTimeOffset.Now;
        _syncPhaseLabel.Text = progress.Phase;
        _syncTargetLabel.Text = string.IsNullOrWhiteSpace(progress.Target)
            ? "取得対象: --"
            : $"取得対象: {progress.Target}";
        var hasDeterminateProgress = progress.Completed.HasValue && progress.Total is > 0;
        _syncProgressBar.IsVisible = hasDeterminateProgress;
        _syncPercentLabel.IsVisible = hasDeterminateProgress;
        if (hasDeterminateProgress)
        {
            var fraction = Math.Clamp(
                (double)progress.Completed!.Value / progress.Total!.Value,
                0,
                1);
            _syncProgressBar.Progress = fraction;
            _syncPercentLabel.Text = $"{fraction:P0}";
        }

        _syncStatusPanel.IsVisible = true;
        RefreshSyncMeta();
    }


    private void RefreshSyncMeta()
    {
        if (_lastSyncProgress is null || !_syncStatusPanel.IsVisible)
        {
            return;
        }

        var parts = new List<string>();
        if (_lastSyncProgress.Completed.HasValue)
        {
            parts.Add(_lastSyncProgress.Total is > 0
                ? $"{_lastSyncProgress.Completed.Value:N0} / {_lastSyncProgress.Total.Value:N0} 件"
                : $"{_lastSyncProgress.Completed.Value:N0} 件");
        }

        if (!string.IsNullOrWhiteSpace(_lastSyncProgress.Detail))
        {
            parts.Add(_lastSyncProgress.Detail);
        }

        var elapsed = DateTimeOffset.Now - _syncStartedAt;
        parts.Add($"最終更新 {_syncLastUpdatedAt:HH:mm:ss}");
        parts.Add($"経過 {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}");
        _syncMetaLabel.Text = string.Join("  |  ", parts);
    }


    private void EndSyncStatus(string phase = "同期完了", string? detail = null)
    {
        _syncTimer?.Stop();
        var total = _lastSyncProgress?.Total;
        var completed = total ?? _lastSyncProgress?.Completed;
        UpdateSyncStatus(new LibrarySyncProgress(
            phase,
            _lastSyncProgress?.Target,
            completed,
            total,
            detail ?? _lastSyncProgress?.Detail));
        _acceptSyncProgress = false;
    }


    private void FailSyncStatus(Exception exception)
    {
        _acceptSyncProgress = false;
        _syncTimer?.Stop();
        _syncStatusPanel.Stroke = Color.FromArgb("#8D3F48");
        _syncStatusPanel.BackgroundColor = Color.FromArgb("#28171B");
        _syncPhaseLabel.TextColor = Color.FromArgb("#FF8A96");
        _syncPercentLabel.TextColor = Color.FromArgb("#FF8A96");
        _syncProgressBar.ProgressColor = Color.FromArgb("#FF8A96");
        _syncPhaseLabel.Text = $"同期停止: {_lastSyncProgress?.Phase ?? "不明な処理"}";
        _syncTargetLabel.TextColor = Color.FromArgb("#FFD5D9");
        _syncMetaLabel.Text = $"{exception.Message}  |  最終更新 {_syncLastUpdatedAt:HH:mm:ss}";
        _syncStatusPanel.IsVisible = true;
    }


    private string CreateSyncFailureMessage(Exception exception)
    {
        var phase = _lastSyncProgress?.Phase ?? "不明な処理";
        var target = _lastSyncProgress?.Target ?? "不明";
        return $"{exception.Message}\n\n停止位置: {phase}\n取得対象: {target}\n最終更新: {_syncLastUpdatedAt:HH:mm:ss}";
    }


    private static string GetSyncCompletionDetail(EagleLibrary library)
    {
        const string startMarker = "追加 ";
        const string endMarker = " / dirs ";
        var start = library.IndexMessage.IndexOf(startMarker, StringComparison.Ordinal);
        if (start < 0)
        {
            return library.IndexMessage;
        }

        var end = library.IndexMessage.IndexOf(endMarker, start, StringComparison.Ordinal);
        return end > start
            ? library.IndexMessage[start..end]
            : library.IndexMessage[start..];
    }

    private async Task RefreshActiveLibraryAsync()
    {
        if (_activeLibrary is null)
        {
            await AddGoogleDriveApiLibraryAsync();
            return;
        }

        if (_activeLibrary.SourceKind == EagleLibrarySourceKind.GoogleDriveApi
            || GoogleDriveLibraryService.IsDriveFolderUri(_activeLibrary.TreeUri))
        {
            await IndexGoogleDriveApiLibraryAsync(_activeLibrary);
            return;
        }

        await IndexLibraryAsync(_activeLibrary.TreeUri, _activeLibrary);
    }


    private async Task IndexGoogleDriveApiLibraryAsync(EagleLibrary? previous, bool allowAssetReuse = true)
    {
        if (_isBusy)
        {
            return;
        }

        var succeeded = false;
        string? completionDetail = null;
        BeginSyncStatus("Google Drive同期を開始中", previous?.Name ?? "EAGLEライブラリ");
        var progress = CreateSyncProgressReporter();
        SetBusy(true);
        try
        {
            var library = await _drive.IndexAsync(_state, previous, progress, allowAssetReuse: allowAssetReuse);
            UpsertLibrary(library);

            _activeLibrary = library;
            _state.ActiveLibraryId = library.Id;
            _state.SelectedFolderId = AllFoldersId;
            _state.SelectedTags.Clear();
            _state.SelectedTag = null;
            await SaveStateAsync();
            RefreshVisibleAssets();
            if (library.Assets.Count == 0)
            {
                await DisplayAlertAsync("画像が見つかりません", library.IndexMessage, "OK");
            }

            completionDetail = GetSyncCompletionDetail(library);
            succeeded = true;
        }
        catch (GoogleDriveReconnectRequiredException ex)
        {
            FailSyncStatus(ex);
            var reconnect = await DisplayAlertAsync("Google Drive再接続", CreateSyncFailureMessage(ex), "接続", "キャンセル");
            if (reconnect)
            {
                BeginSyncStatus("Google Drive再接続中", "Googleアカウント");
                var reconnectProgress = CreateSyncProgressReporter();
                try
                {
                    await _drive.AuthorizeWithBrowserAsync(_state, reconnectProgress);
                    await SaveStateAsync();
                }
                catch (Exception authException)
                {
                    FailSyncStatus(authException);
                    await DisplayAlertAsync("Google Driveに再接続できません", CreateSyncFailureMessage(authException), "OK");
                    return;
                }

                SetBusy(false);
                await IndexGoogleDriveApiLibraryAsync(previous, allowAssetReuse);
            }
        }
        catch (Exception ex)
        {
            FailSyncStatus(ex);
            await DisplayAlertAsync("Drive APIで索引化できません", CreateSyncFailureMessage(ex), "OK");
        }
        finally
        {
            SetBusy(false);
            if (succeeded)
            {
                EndSyncStatus(detail: completionDetail);
            }
        }
    }


    private async Task IndexLibraryAsync(string treeUri, EagleLibrary? previous, bool allowAssetReuse = true)
    {
        if (_isBusy)
        {
            return;
        }

        var succeeded = false;
        string? completionDetail = null;
        BeginSyncStatus("フォルダ同期を開始中", previous?.Name ?? "EAGLEライブラリ");
        var progress = CreateSyncProgressReporter();
        SetBusy(true);
        try
        {
            var library = await Task.Run(() =>
                _indexer.IndexAsync(treeUri, previous, progress, allowAssetReuse: allowAssetReuse));
            UpsertLibrary(library);

            _activeLibrary = library;
            _state.ActiveLibraryId = library.Id;
            _state.SelectedFolderId = AllFoldersId;
            _state.SelectedTags.Clear();
            _state.SelectedTag = null;
            await SaveStateAsync();
            RefreshVisibleAssets();
            if (library.Assets.Count == 0)
            {
                await DisplayAlertAsync("画像が見つかりません", library.IndexMessage, "OK");
            }

            completionDetail = GetSyncCompletionDetail(library);
            succeeded = true;
        }
        catch (Exception ex)
        {
            FailSyncStatus(ex);
            await DisplayAlertAsync("索引化できません", CreateSyncFailureMessage(ex), "OK");
        }
        finally
        {
            SetBusy(false);
            if (succeeded)
            {
                EndSyncStatus(detail: completionDetail);
            }
        }
    }


    private sealed class UiProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value)
        {
            if (MainThread.IsMainThread)
            {
                handler(value);
                return;
            }

            MainThread.BeginInvokeOnMainThread(() => handler(value));
        }
    }
}
