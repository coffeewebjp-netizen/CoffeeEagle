using Microsoft.Maui.Controls.Shapes;
using System.ComponentModel;

namespace CoffeeEagle.Reader.Pages;

internal sealed record PhoneCachePreferenceItem(string Key, string Title, string Detail);

internal sealed class PhoneCachePreferenceView : ContentView
{
    private readonly TaskCompletionSource<HashSet<string>?> _completion = new();
    private readonly IReadOnlyList<PhoneCachePreferenceRow> _items;
    private readonly HashSet<string> _excluded;
    private readonly CollectionView _list = new()
    {
        Margin = new Thickness(14, 4, 14, 12),
        ItemSizingStrategy = ItemSizingStrategy.MeasureFirstItem,
        SelectionMode = SelectionMode.None,
        ItemTemplate = CreateItemTemplate(),
        EmptyView = new Label { Text = "ファイルがありません", TextColor = Color.FromArgb("#98A4B5"), HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(0, 24) }
    };

    public Task<HashSet<string>?> Completion => _completion.Task;

    public PhoneCachePreferenceView(
        IReadOnlyList<PhoneCachePreferenceItem> items,
        IEnumerable<string> excluded)
    {
        _excluded = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
        _items = items.Select(item => new PhoneCachePreferenceRow(item, !_excluded.Contains(item.Key))).ToList();
        foreach (var item in _items)
        {
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(PhoneCachePreferenceRow.IsIncluded)) return;
                if (item.IsIncluded) _excluded.Remove(item.Key);
                else _excluded.Add(item.Key);
            };
        }
        BackgroundColor = Colors.Transparent;

        var header = new VerticalStackLayout
        {
            Padding = new Thickness(16, 16, 16, 8),
            Spacing = 3,
            Children =
            {
                new Label { Text = "端末キャッシュ", TextColor = Colors.White, FontSize = 22, FontAttributes = FontAttributes.Bold },
                new Label { Text = "チェックしたファイルを次回の同期から自動保存します。チェックを外すと対象外になり、保存済みコピーは残ります。", TextColor = Color.FromArgb("#98A4B5"), FontSize = 12 }
            }
        };
        var search = new SearchBar
        {
            Placeholder = "ファイルを検索",
            TextColor = Colors.White,
            PlaceholderColor = Color.FromArgb("#667386"),
            BackgroundColor = Color.FromArgb("#4012171D"),
            Margin = new Thickness(14, 0, 14, 6)
        };
        search.TextChanged += (_, e) => Filter(e.NewTextValue ?? string.Empty);

        var save = CreateButton("保存", "#21C7A8", "#06130F");
        save.Clicked += (_, _) => Complete(_excluded);
        var cancel = CreateButton("キャンセル", "#172029", "#FFFFFF");
        cancel.Clicked += (_, _) => Complete(null);
        var footer = new Grid
        {
            Padding = new Thickness(14, 8),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 8,
            Children = { cancel, save }
        };
        Grid.SetColumn(save, 1);
        var root = new Grid
        {
            RowDefinitions = { new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto) },
            Children = { header, search, _list, footer }
        };
        Grid.SetRow(search, 1);
        Grid.SetRow(_list, 2);
        Grid.SetRow(footer, 3);
        Content = root;
        Filter(string.Empty);
    }

    public void Cancel() => Complete(null);

    private void Filter(string filter)
    {
        var visible = _items.Where(item => string.IsNullOrWhiteSpace(filter)
            || item.Title.Contains(filter, StringComparison.CurrentCultureIgnoreCase)
            || item.Detail.Contains(filter, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _list.ItemsSource = visible;
    }

    private static DataTemplate CreateItemTemplate() => new(() =>
    {
        var check = new CheckBox { Color = Color.FromArgb("#21C7A8"), VerticalOptions = LayoutOptions.Center };
        check.SetBinding(CheckBox.IsCheckedProperty, new Binding(nameof(PhoneCachePreferenceRow.IsIncluded), BindingMode.TwoWay));
        var title = new Label { TextColor = Colors.White, FontSize = 14, FontAttributes = FontAttributes.Bold, LineBreakMode = LineBreakMode.TailTruncation };
        title.SetBinding(Label.TextProperty, nameof(PhoneCachePreferenceRow.Title));
        var detail = new Label { TextColor = Color.FromArgb("#98A4B5"), FontSize = 11, LineBreakMode = LineBreakMode.TailTruncation };
        detail.SetBinding(Label.TextProperty, nameof(PhoneCachePreferenceRow.Detail));
        var text = new VerticalStackLayout { Spacing = 2, VerticalOptions = LayoutOptions.Center, Children = { title, detail } };
        var row = new Grid
        {
            Padding = new Thickness(8, 6),
            ColumnDefinitions = { new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star) },
            ColumnSpacing = 8,
            Children = { check, text }
        };
        Grid.SetColumn(text, 1);
        return new Border
        {
            Margin = new Thickness(0, 0, 0, 7),
            BackgroundColor = Color.FromArgb("#4010161D"),
            Stroke = Color.FromArgb("#6623303C"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            Content = row
        };
    });

    private static Button CreateButton(string text, string background, string foreground) => new()
    {
        Text = text,
        BackgroundColor = Color.FromArgb(background),
        TextColor = Color.FromArgb(foreground),
        CornerRadius = 8,
        HeightRequest = 42
    };

    private void Complete(HashSet<string>? excluded) => _completion.TrySetResult(excluded is null ? null : new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase));
}

internal sealed class PhoneCachePreferenceRow(PhoneCachePreferenceItem item, bool isIncluded) : INotifyPropertyChanged
{
    private bool _isIncluded = isIncluded;
    public string Key => item.Key;
    public string Title => item.Title;
    public string Detail => item.Detail;
    public bool IsIncluded
    {
        get => _isIncluded;
        set
        {
            if (_isIncluded == value) return;
            _isIncluded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsIncluded)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
