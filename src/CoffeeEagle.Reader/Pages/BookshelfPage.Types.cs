using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage
{

    private sealed record FolderMenuItem(string DisplayName, EagleFolder Folder, int Depth, int Count);


    private sealed record PickerOption(
        string Id,
        string Title,
        string Detail,
        bool IsSelected = false,
        string Eyebrow = "",
        int Depth = 0,
        bool IsDestructive = false);


    private sealed record TagCount(string Name, int Count);


    private sealed class OptionPickerView : ContentView
    {
        private readonly TaskCompletionSource<PickerOption?> _completion = new();
        private readonly IReadOnlyList<PickerOption> _options;
        private readonly VerticalStackLayout _list = new() { Padding = new Thickness(14, 8, 14, 18), Spacing = 8 };
        private readonly string _emptyText;

        public Task<PickerOption?> Completion => _completion.Task;

        public OptionPickerView(string title, string subtitle, IReadOnlyList<PickerOption> options, string searchPlaceholder)
        {
            _options = options;
            _emptyText = title + "がありません";
            BackgroundColor = Colors.Transparent;

            var closeButton = CreateSheetButton("閉じる", Color.FromArgb("#172029"), Colors.White);
            closeButton.Clicked += (_, _) => Complete(null);

            var header = new Grid
            {
                Padding = new Thickness(16, 16, 16, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            new Label { Text = title, TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold },
                            new Label { Text = subtitle, TextColor = Color.FromArgb("#98A4B5"), FontSize = 12 }
                        }
                    },
                    closeButton
                }
            };
            Grid.SetColumn(closeButton, 1);

            var search = new SearchBar
            {
                Placeholder = searchPlaceholder,
                TextColor = Colors.White,
                PlaceholderColor = Color.FromArgb("#667386"),
                CancelButtonColor = Color.FromArgb("#21C7A8"),
                BackgroundColor = Color.FromArgb("#4012171D"),
                Margin = new Thickness(14, 0, 14, 8)
            };
            search.TextChanged += (_, e) => Render(e.NewTextValue ?? string.Empty);

            var scroll = new ScrollView { BackgroundColor = Colors.Transparent, Content = _list };
            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star)
                },
                Children = { header, search, scroll }
            };
            Grid.SetRow(search, 1);
            Grid.SetRow(scroll, 2);
            Content = root;
            Render(string.Empty);
        }

        private void Render(string filter)
        {
            _list.Children.Clear();
            var visibleOptions = _options
                .Where(option => string.IsNullOrWhiteSpace(filter)
                    || option.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || option.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
                    || option.Eyebrow.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            if (visibleOptions.Count == 0)
            {
                _list.Children.Add(new Label
                {
                    Text = _emptyText,
                    TextColor = Color.FromArgb("#98A4B5"),
                    FontSize = 14,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 0)
                });
                return;
            }

            foreach (var option in visibleOptions)
            {
                _list.Children.Add(CreateOptionRow(option));
            }
        }

        private View CreateOptionRow(PickerOption option)
        {
            var title = new Label
            {
                Text = option.Title,
                TextColor = Colors.White,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var detail = new Label
            {
                Text = option.Detail,
                TextColor = Color.FromArgb("#98A4B5"),
                FontSize = 12,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var eyebrow = new Label
            {
                Text = option.Eyebrow,
                TextColor = option.IsDestructive ? Color.FromArgb("#FF8A8A") : option.IsSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#667386"),
                FontSize = 11,
                FontAttributes = FontAttributes.Bold,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var selected = new Label
            {
                Text = option.IsDestructive ? "削除" : option.IsSelected ? "選択中" : string.Empty,
                TextColor = option.IsDestructive ? Color.FromArgb("#FF8A8A") : Color.FromArgb("#21C7A8"),
                FontSize = 12,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center
            };
            var content = new VerticalStackLayout
            {
                Spacing = 2,
                Children = { eyebrow, title, detail }
            };
            var row = new Grid
            {
                Padding = new Thickness(12 + Math.Min(option.Depth, 8) * 16, 10, 12, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Children = { content, selected }
            };
            Grid.SetColumn(selected, 1);

            var border = new Border
            {
                BackgroundColor = option.IsDestructive ? Color.FromArgb("#50371C20") : option.IsSelected ? Color.FromArgb("#60162B29") : Color.FromArgb("#4010161D"),
                Stroke = option.IsDestructive ? Color.FromArgb("#88553A42") : option.IsSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#6623303C"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = row
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => Complete(option);
            border.GestureRecognizers.Add(tap);
            return border;
        }

        public void Cancel()
        {
            Complete(null);
        }

        private void Complete(PickerOption? selected)
        {
            _completion.TrySetResult(selected);
        }
    }


    private sealed class TagPopupView : ContentView
    {
        private readonly TaskCompletionSource<List<string>?> _completion = new();
        private readonly HashSet<string> _selected;
        private readonly IReadOnlyList<TagCount> _tags;
        private readonly VerticalStackLayout _list = new() { Padding = new Thickness(14, 8, 14, 86), Spacing = 8 };
        private readonly Label _selectedLabel = new()
        {
            TextColor = Color.FromArgb("#98A4B5"),
            FontSize = 12,
            VerticalTextAlignment = TextAlignment.Center
        };

        public Task<List<string>?> Completion => _completion.Task;

        public TagPopupView(IReadOnlyList<TagCount> tags, IEnumerable<string> selectedTags, int assetCount)
        {
            _tags = tags;
            _selected = new HashSet<string>(selectedTags, StringComparer.CurrentCultureIgnoreCase);
            BackgroundColor = Colors.Transparent;

            var closeButton = CreateSheetButton("閉じる", Color.FromArgb("#172029"), Colors.White);
            closeButton.Clicked += (_, _) => Complete(null);

            var header = new Grid
            {
                Padding = new Thickness(16, 16, 16, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                Children =
                {
                    new VerticalStackLayout
                    {
                        Spacing = 2,
                        Children =
                        {
                            new Label { Text = "タグ", TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold },
                            new Label { Text = $"複数選択  {assetCount:N0} items", TextColor = Color.FromArgb("#98A4B5"), FontSize = 12 }
                        }
                    },
                    closeButton
                }
            };
            Grid.SetColumn(closeButton, 1);

            var search = new SearchBar
            {
                Placeholder = "タグを検索",
                TextColor = Colors.White,
                PlaceholderColor = Color.FromArgb("#667386"),
                CancelButtonColor = Color.FromArgb("#21C7A8"),
                BackgroundColor = Color.FromArgb("#4012171D"),
                Margin = new Thickness(14, 0, 14, 8)
            };
            search.TextChanged += (_, e) => Render(e.NewTextValue ?? string.Empty);

            var clearButton = CreateSheetButton("クリア", Color.FromArgb("#172029"), Colors.White);
            clearButton.Clicked += (_, _) =>
            {
                _selected.Clear();
                Render(search.Text ?? string.Empty);
            };
            var doneButton = CreateSheetButton("完了", Color.FromArgb("#21C7A8"), Color.FromArgb("#06130F"));
            doneButton.Clicked += (_, _) => Complete(_selected.OrderBy(tag => tag, StringComparer.CurrentCultureIgnoreCase).ToList());

            var footer = new Grid
            {
                Padding = new Thickness(14, 10),
                BackgroundColor = Color.FromArgb("#560B0E12"),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 8,
                Children = { _selectedLabel, clearButton, doneButton }
            };
            Grid.SetColumn(clearButton, 1);
            Grid.SetColumn(doneButton, 2);

            var scroll = new ScrollView { BackgroundColor = Colors.Transparent, Content = _list };
            var root = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Auto),
                    new RowDefinition(GridLength.Star),
                    new RowDefinition(GridLength.Auto)
                },
                Children = { header, search, scroll, footer }
            };
            Grid.SetRow(search, 1);
            Grid.SetRow(scroll, 2);
            Grid.SetRow(footer, 3);
            Content = root;
            Render(string.Empty);
        }

        private void Render(string filter)
        {
            _list.Children.Clear();
            var visibleTags = _tags
                .Where(tag => string.IsNullOrWhiteSpace(filter) || tag.Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase))
                .ToList();
            foreach (var tag in visibleTags)
            {
                _list.Children.Add(CreateTagRow(tag));
            }

            if (visibleTags.Count == 0)
            {
                _list.Children.Add(new Label
                {
                    Text = "タグがありません",
                    TextColor = Color.FromArgb("#98A4B5"),
                    FontSize = 14,
                    HorizontalTextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 28, 0, 0)
                });
            }

            UpdateSelectedLabel();
        }

        private View CreateTagRow(TagCount tag)
        {
            var isSelected = _selected.Contains(tag.Name);
            var checkBox = new CheckBox
            {
                IsChecked = isSelected,
                Color = Color.FromArgb("#21C7A8"),
                VerticalOptions = LayoutOptions.Center
            };
            var name = new Label
            {
                Text = tag.Name,
                TextColor = Colors.White,
                FontSize = 16,
                FontAttributes = FontAttributes.Bold,
                VerticalTextAlignment = TextAlignment.Center,
                LineBreakMode = LineBreakMode.TailTruncation
            };
            var count = new Label
            {
                Text = tag.Count.ToString("N0"),
                TextColor = Color.FromArgb("#98A4B5"),
                FontSize = 13,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.End
            };
            checkBox.CheckedChanged += (_, e) =>
            {
                if (e.Value)
                {
                    _selected.Add(tag.Name);
                }
                else
                {
                    _selected.Remove(tag.Name);
                }

                UpdateSelectedLabel();
            };

            var row = new Grid
            {
                Padding = new Thickness(12, 10),
                ColumnDefinitions =
                {
                    new ColumnDefinition(GridLength.Auto),
                    new ColumnDefinition(GridLength.Star),
                    new ColumnDefinition(GridLength.Auto)
                },
                ColumnSpacing = 10,
                Children = { checkBox, name, count }
            };
            Grid.SetColumn(name, 1);
            Grid.SetColumn(count, 2);
            var border = new Border
            {
                BackgroundColor = isSelected ? Color.FromArgb("#60162B29") : Color.FromArgb("#4010161D"),
                Stroke = isSelected ? Color.FromArgb("#21C7A8") : Color.FromArgb("#6623303C"),
                StrokeThickness = 1,
                StrokeShape = new RoundRectangle { CornerRadius = 8 },
                Content = row
            };
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => checkBox.IsChecked = !checkBox.IsChecked;
            border.GestureRecognizers.Add(tap);
            return border;
        }

        private void UpdateSelectedLabel()
        {
            _selectedLabel.Text = _selected.Count == 0 ? "タグ未選択" : $"{_selected.Count:N0} selected";
        }

        public void Cancel()
        {
            Complete(null);
        }

        private void Complete(List<string>? selectedTags)
        {
            _completion.TrySetResult(selectedTags);
        }
    }
}
