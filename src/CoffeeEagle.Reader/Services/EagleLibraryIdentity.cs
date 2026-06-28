using CoffeeEagle.Reader.Models;

namespace CoffeeEagle.Reader.Services;

public static class EagleLibraryIdentity
{
    public static int FindMatchingIndex(IReadOnlyList<EagleLibrary> libraries, EagleLibrary target)
    {
        for (var index = 0; index < libraries.Count; index++)
        {
            if (IsSameLibrary(libraries[index], target))
            {
                return index;
            }
        }

        return -1;
    }

    public static int FindMatchingIndex(
        IReadOnlyList<EagleLibrary> libraries,
        string treeUri,
        string? sourceKind = null,
        string? rootDocumentId = null)
    {
        var target = new EagleLibrary
        {
            SourceKind = string.IsNullOrWhiteSpace(sourceKind) ? EagleLibrarySourceKind.DocumentTree : sourceKind,
            TreeUri = treeUri,
            RootDocumentId = rootDocumentId ?? string.Empty
        };
        return FindMatchingIndex(libraries, target);
    }

    public static List<EagleLibrary> Deduplicate(IEnumerable<EagleLibrary> libraries, string? activeLibraryId = null)
    {
        var result = new List<EagleLibrary>();
        foreach (var library in libraries)
        {
            var existingIndex = FindMatchingIndex(result, library);
            if (existingIndex < 0)
            {
                result.Add(library);
                continue;
            }

            result[existingIndex] = ChoosePreferred(result[existingIndex], library, activeLibraryId);
        }

        return result;
    }

    private static EagleLibrary ChoosePreferred(EagleLibrary existing, EagleLibrary incoming, string? activeLibraryId)
    {
        if (!string.IsNullOrWhiteSpace(activeLibraryId))
        {
            if (string.Equals(incoming.Id, activeLibraryId, StringComparison.Ordinal)
                && !string.Equals(existing.Id, activeLibraryId, StringComparison.Ordinal))
            {
                return incoming;
            }

            if (string.Equals(existing.Id, activeLibraryId, StringComparison.Ordinal))
            {
                return existing;
            }
        }

        if (incoming.IndexedAt > existing.IndexedAt)
        {
            return incoming;
        }

        return existing;
    }

    private static bool IsSameLibrary(EagleLibrary left, EagleLibrary right)
    {
        if (!string.IsNullOrWhiteSpace(left.Id)
            && string.Equals(left.Id, right.Id, StringComparison.Ordinal))
        {
            return true;
        }

        foreach (var leftKey in CreateSourceKeys(left))
        {
            foreach (var rightKey in CreateSourceKeys(right))
            {
                if (string.Equals(leftKey, rightKey, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static IEnumerable<string> CreateSourceKeys(EagleLibrary library)
    {
        var treeUri = NormalizeText(library.TreeUri);
        if (!string.IsNullOrWhiteSpace(treeUri))
        {
            yield return "tree:" + treeUri;
        }

        var rootDocumentId = NormalizeText(library.RootDocumentId);
        if (!string.IsNullOrWhiteSpace(rootDocumentId))
        {
            yield return $"source:{NormalizeText(library.SourceKind)}:root:{rootDocumentId}";
        }

        var driveFolderId = GetDriveFolderId(library);
        if (!string.IsNullOrWhiteSpace(driveFolderId))
        {
            yield return "drive-folder:" + driveFolderId;
        }
    }

    private static string GetDriveFolderId(EagleLibrary library)
    {
        if (string.Equals(library.SourceKind, EagleLibrarySourceKind.GoogleDriveApi, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(library.RootDocumentId))
        {
            return NormalizeText(library.RootDocumentId);
        }

        if (GoogleDriveLibraryService.IsDriveFolderUri(library.TreeUri))
        {
            return NormalizeText(GoogleDriveLibraryService.ExtractFolderId(library.TreeUri));
        }

        return string.Empty;
    }

    private static string NormalizeText(string? value)
    {
        return (value ?? string.Empty).Trim().TrimEnd('/');
    }
}