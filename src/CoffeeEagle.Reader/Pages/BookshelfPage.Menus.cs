using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage
{

    private async Task ShowFolderMenuAsync()
    {
        if (_activeLibrary is null)
        {
            await AddLibraryAsync();
            return;
        }

        var items = BuildFolderMenuItems(_activeLibrary);
        var options = new List<PickerOption>
        {
            new(AllFoldersId, "すべて", $"{_activeLibrary.Assets.Count:N0} items", IsSelectedFolder(AllFoldersId), "フォルダ"),
            new(UnfiledFoldersId, "未分類", $"{_activeLibrary.Assets.Count(asset => asset.FolderIds.Count == 0):N0} items", IsSelectedFolder(UnfiledFoldersId), "フォルダ")
        };
        options.AddRange(items.Select(item => new PickerOption(
            item.Folder.Id,
            item.DisplayName,
            item.Count > 0 ? $"{item.Count:N0} items" : "空のフォルダ",
            IsSelectedFolder(item.Folder.Id),
            item.Depth == 0 ? "フォルダ" : $"階層 {item.Depth + 1}",
            item.Depth)));

        var selected = await ShowOptionPickerAsync("フォルダ", "階層から選択", options, searchPlaceholder: "フォルダを検索");
        if (selected is null)
        {
            return;
        }

        _state.SelectedFolderId = selected.Id;
        await SaveStateAsync();
        RefreshVisibleAssets();
    }


    private async Task ShowTagMenuAsync()
    {
        if (_activeLibrary is null)
        {
            await AddLibraryAsync();
            return;
        }

        var tagCounts = _activeLibrary.Assets
            .SelectMany(asset => asset.Tags)
            .GroupBy(tag => tag, StringComparer.CurrentCultureIgnoreCase)
            .Select(group => new TagCount(group.Key, group.Count()))
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var selectedTags = await ShowTagPopupAsync(tagCounts, _state.SelectedTags, _activeLibrary.Assets.Count);
        if (selectedTags is null)
        {
            return;
        }

        _state.SelectedTags = selectedTags;
        _state.SelectedTag = _state.SelectedTags.FirstOrDefault();
        await SaveStateAsync();
        RefreshVisibleAssets();
    }


    private Task<List<string>?> ShowTagPopupAsync(
        IReadOnlyList<TagCount> tagCounts,
        IEnumerable<string> selectedTags,
        int assetCount)
    {
        var popup = new TagPopupView(tagCounts, selectedTags, assetCount);
        return ShowBottomSheetAsync(popup, popup.Completion, popup.Cancel);
    }


    private Task<PickerOption?> ShowOptionPickerAsync(
        string title,
        string subtitle,
        IReadOnlyList<PickerOption> options,
        string searchPlaceholder)
    {
        var popup = new OptionPickerView(title, subtitle, options, searchPlaceholder);
        return ShowBottomSheetAsync(popup, popup.Completion, popup.Cancel);
    }


    private async Task<TResult?> ShowBottomSheetAsync<TResult>(
        View popup,
        Task<TResult?> completion,
        Action cancel)
    {
        if (Content is not Grid root)
        {
            return default;
        }

        var previousCancel = _activeSheetCancel;
        _activeSheetCancel = cancel;
        var backdrop = new Grid
        {
            BackgroundColor = Color.FromArgb("#01000000"),
            InputTransparent = false
        };
        var panel = new Border
        {
            BackgroundColor = Color.FromArgb("#660B0E12"),
            Stroke = Color.FromArgb("#80334150"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 12 },
            Content = popup,
            VerticalOptions = LayoutOptions.End,
            HeightRequest = Math.Min(Math.Max(420, Height * 0.72), 620),
            Margin = new Thickness(10, 0, 10, 10)
        };
        var overlay = new Grid
        {
            Opacity = 0,
            InputTransparent = false,
            Children = { backdrop, panel }
        };
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));
        overlay.ZIndex = 1000;

        var backdropTap = new TapGestureRecognizer();
        backdropTap.Tapped += (_, _) => cancel();
        backdrop.GestureRecognizers.Add(backdropTap);

        TResult? result = default;
        root.Children.Add(overlay);
        panel.TranslationY = 32;
        try
        {
            await Task.WhenAll(
                overlay.FadeToAsync(1, 140, Easing.CubicOut),
                panel.TranslateToAsync(0, 0, 180, Easing.CubicOut));

            result = await completion;

            await Task.WhenAll(
                overlay.FadeToAsync(0, 120, Easing.CubicIn),
                panel.TranslateToAsync(0, 32, 120, Easing.CubicIn));
        }
        finally
        {
            root.Children.Remove(overlay);
            if (ReferenceEquals(_activeSheetCancel, cancel))
            {
                _activeSheetCancel = previousCancel;
            }
        }

        return result;
    }


    private bool IsSelectedFolder(string folderId)
    {
        return string.Equals(_state.SelectedFolderId ?? AllFoldersId, folderId, StringComparison.OrdinalIgnoreCase);
    }


    private List<FolderMenuItem> BuildFolderMenuItems(EagleLibrary library)
    {
        var foldersById = library.Folders.ToDictionary(folder => folder.Id, StringComparer.OrdinalIgnoreCase);
        var childrenByParent = library.Folders
            .GroupBy(folder => NormalizeParentId(folder.ParentId, foldersById), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(folder => folder.SortOrder)
                    .ThenBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList(),
                StringComparer.OrdinalIgnoreCase);
        var items = new List<FolderMenuItem>();
        AppendFolderMenuItems(library, childrenByParent, parentId: string.Empty, depth: 0, items);
        return items;
    }


    private void AppendFolderMenuItems(
        EagleLibrary library,
        IReadOnlyDictionary<string, List<EagleFolder>> childrenByParent,
        string parentId,
        int depth,
        List<FolderMenuItem> items)
    {
        if (!childrenByParent.TryGetValue(parentId, out var folders))
        {
            return;
        }

        foreach (var folder in folders)
        {
            var count = CountAssetsInFolderTree(library, folder.Id);
            items.Add(new FolderMenuItem(folder.Name, folder, depth, count));
            AppendFolderMenuItems(library, childrenByParent, folder.Id, depth + 1, items);
        }
    }


    private static string NormalizeParentId(string? parentId, IReadOnlyDictionary<string, EagleFolder> foldersById)
    {
        return !string.IsNullOrWhiteSpace(parentId) && foldersById.ContainsKey(parentId)
            ? parentId
            : string.Empty;
    }


    private static int CountAssetsInFolderTree(EagleLibrary library, string folderId)
    {
        return library.Assets.Count(asset => asset.FolderIds.Any(assetFolderId => IsFolderOrDescendant(library, assetFolderId, folderId)));
    }
}
