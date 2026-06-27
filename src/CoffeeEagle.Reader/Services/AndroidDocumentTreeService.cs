using Android.Content;
using Android.Database;
using Android.Provider;
using AndroidUri = Android.Net.Uri;

namespace CoffeeEagle.Reader.Services;

public sealed class AndroidDocumentTreeService
{
    private static readonly string[] DocumentProjection =
    [
        DocumentsContract.Document.ColumnDocumentId,
        DocumentsContract.Document.ColumnDisplayName,
        DocumentsContract.Document.ColumnMimeType,
        DocumentsContract.Document.ColumnLastModified,
        DocumentsContract.Document.ColumnSize
    ];

    private ContentResolver Resolver => MainActivity.Current?.ContentResolver
        ?? throw new InvalidOperationException("Android activity is not ready.");

    public DocumentEntry GetRoot(string treeUriString)
    {
        var treeUri = ParseUri(treeUriString);
        var documentId = DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new InvalidOperationException("選択されたフォルダを読み取れません。");
        var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId)
            ?? throw new InvalidOperationException("Document URIを作成できませんでした。");
        return QuerySingle(treeUri, documentUri, fallbackDocumentId: documentId, fallbackName: "EAGLE Library");
    }

    public IReadOnlyList<DocumentEntry> ListChildren(string treeUriString, string documentId)
    {
        var treeUri = ParseUri(treeUriString);
        var childrenUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, documentId)
            ?? throw new InvalidOperationException("子フォルダを読み取れませんでした。");
        using var cursor = Resolver.Query(childrenUri, DocumentProjection, null, null, null);
        if (cursor is null)
        {
            return [];
        }

        var entries = new List<DocumentEntry>();
        while (cursor.MoveToNext())
        {
            entries.Add(ReadEntry(treeUri, cursor));
        }

        return entries;
    }

    public Stream OpenRead(string uriString)
    {
        var uri = ParseUri(uriString);
        return Resolver.OpenInputStream(uri)
            ?? throw new InvalidOperationException("ファイルを開けませんでした。");
    }

    public string GetSourceKind(string treeUriString)
    {
        var authority = ParseUri(treeUriString).Authority ?? string.Empty;
        return authority.Contains("com.google.android.apps.docs", StringComparison.OrdinalIgnoreCase)
            ? CoffeeEagle.Reader.Models.EagleLibrarySourceKind.GoogleDrive
            : CoffeeEagle.Reader.Models.EagleLibrarySourceKind.DocumentTree;
    }

    public string GetSourceLabel(string treeUriString)
    {
        return GetSourceKind(treeUriString) == CoffeeEagle.Reader.Models.EagleLibrarySourceKind.GoogleDrive
            ? "Google Drive"
            : "端末フォルダ";
    }

    public bool IsGoogleDriveTree(string treeUriString)
    {
        return GetSourceKind(treeUriString) == CoffeeEagle.Reader.Models.EagleLibrarySourceKind.GoogleDrive;
    }

    public AndroidUri BuildDocumentUri(string treeUriString, string documentId)
    {
        var treeUri = ParseUri(treeUriString);
        return DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId)
            ?? throw new InvalidOperationException("Document URIを作成できませんでした。");
    }

    private DocumentEntry QuerySingle(AndroidUri treeUri, AndroidUri documentUri, string fallbackDocumentId, string fallbackName)
    {
        using var cursor = Resolver.Query(documentUri, DocumentProjection, null, null, null);
        if (cursor is not null && cursor.MoveToFirst())
        {
            return ReadEntry(treeUri, cursor);
        }

        return new DocumentEntry(
            fallbackDocumentId,
            fallbackName,
            DocumentsContract.Document.MimeTypeDir,
            0,
            0,
            documentUri.ToString() ?? string.Empty);
    }

    private static DocumentEntry ReadEntry(AndroidUri treeUri, ICursor cursor)
    {
        var documentId = GetString(cursor, DocumentsContract.Document.ColumnDocumentId) ?? string.Empty;
        var name = GetString(cursor, DocumentsContract.Document.ColumnDisplayName) ?? documentId;
        var mimeType = GetString(cursor, DocumentsContract.Document.ColumnMimeType) ?? string.Empty;
        var lastModified = GetLong(cursor, DocumentsContract.Document.ColumnLastModified);
        var size = GetLong(cursor, DocumentsContract.Document.ColumnSize);
        var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, documentId);
        var uri = documentUri?.ToString() ?? string.Empty;

        return new DocumentEntry(
            documentId,
            name,
            mimeType,
            lastModified,
            size,
            uri);
    }

    private static string? GetString(ICursor cursor, string columnName)
    {
        var index = cursor.GetColumnIndex(columnName);
        return index >= 0 && !cursor.IsNull(index) ? cursor.GetString(index) : null;
    }

    private static long GetLong(ICursor cursor, string columnName)
    {
        var index = cursor.GetColumnIndex(columnName);
        return index >= 0 && !cursor.IsNull(index) ? cursor.GetLong(index) : 0;
    }

    private static AndroidUri ParseUri(string uriString)
    {
        return AndroidUri.Parse(uriString) ?? throw new InvalidOperationException("URIを読み取れませんでした。");
    }
}

public sealed record DocumentEntry(
    string DocumentId,
    string Name,
    string MimeType,
    long LastModified,
    long Size,
    string Uri)
{
    public bool IsDirectory => string.Equals(MimeType, DocumentsContract.Document.MimeTypeDir, StringComparison.Ordinal);
}

