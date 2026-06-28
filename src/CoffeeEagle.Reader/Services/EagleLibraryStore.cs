using System.Text.Json;
using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public sealed class EagleLibraryStore
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };

    private string StatePath => Path.Combine(FileSystem.AppDataDirectory, "coffeeeagle-state.json");

    public async Task<EagleReaderState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            if (!File.Exists(StatePath))
            {
                return new EagleReaderState();
            }

            await using var stream = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var state = await JsonSerializer.DeserializeAsync<EagleReaderState>(stream, _jsonOptions, cancellationToken);
            return Normalize(state ?? new EagleReaderState());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(EagleReaderState state, CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(FileSystem.AppDataDirectory);
            var normalized = Normalize(state);
            var tempPath = StatePath + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, normalized, _jsonOptions, cancellationToken);
            }

            File.Move(tempPath, StatePath, overwrite: true);
        }
        finally
        {
            _lock.Release();
        }
    }

    private static EagleReaderState Normalize(EagleReaderState state)
    {
        state.GridSpan = Math.Clamp(state.GridSpan <= 0 ? 3 : state.GridSpan, 2, 5);
        state.SearchText ??= string.Empty;
        state.SelectedTags ??= [];
        if (string.IsNullOrWhiteSpace(state.GoogleDriveClientId))
        {
            state.GoogleDriveClientId = GoogleDriveLibraryService.DefaultClientId;
        }

        if (!string.IsNullOrWhiteSpace(state.SelectedTag)
            && !state.SelectedTags.Contains(state.SelectedTag, StringComparer.CurrentCultureIgnoreCase))
        {
            state.SelectedTags.Add(state.SelectedTag);
        }

        state.SelectedTag = state.SelectedTags.FirstOrDefault();
        state.Libraries ??= [];
        foreach (var library in state.Libraries)
        {
            if (string.IsNullOrWhiteSpace(library.SourceKind))
            {
                library.SourceKind = EagleLibrarySourceKind.DocumentTree;
            }

            if (string.IsNullOrWhiteSpace(library.SourceLabel))
            {
                library.SourceLabel = library.SourceKind switch
                {
                    EagleLibrarySourceKind.GoogleDriveApi => "Google Drive API",
                    EagleLibrarySourceKind.GoogleDrive => "Google Drive",
                    _ => "端末フォルダ"
                };
            }

            library.IndexMessage ??= string.Empty;
            library.Folders ??= [];
            library.Assets ??= [];
            foreach (var asset in library.Assets)
            {
                asset.FolderIds ??= [];
                asset.Tags ??= [];
                if (string.IsNullOrWhiteSpace(asset.MediaKind))
                {
                    asset.MediaKind = EagleAssetMediaKind.Image;
                }

                if (string.IsNullOrWhiteSpace(asset.SourceInfoId))
                {
                    asset.SourceInfoId = asset.Id;
                }
            }
        }

        if (state.ActiveLibraryId is not null
            && state.Libraries.All(library => !string.Equals(library.Id, state.ActiveLibraryId, StringComparison.Ordinal)))
        {
            state.ActiveLibraryId = state.Libraries.FirstOrDefault()?.Id;
        }

        state.ActiveLibraryId ??= state.Libraries.FirstOrDefault()?.Id;
        return state;
    }
}