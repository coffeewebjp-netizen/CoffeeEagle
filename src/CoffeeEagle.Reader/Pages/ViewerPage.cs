using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed class ViewerPage : ContentPage
{
    private readonly IReadOnlyList<EagleAsset> _assets;
    private readonly EagleImageSourceService _imageSources;
    private readonly Image _image = new()
    {
        Aspect = Aspect.AspectFit,
        BackgroundColor = Colors.Black,
        HorizontalOptions = LayoutOptions.Fill,
        VerticalOptions = LayoutOptions.Fill
    };
    private readonly Grid _chrome = new()
    {
        BackgroundColor = Color.FromArgb("#CC0B0E12"),
        Padding = new Thickness(12, 8)
    };
    private readonly Label _titleLabel = new()
    {
        TextColor = Colors.White,
        FontAttributes = FontAttributes.Bold,
        FontSize = 15,
        LineBreakMode = LineBreakMode.TailTruncation,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly Label _counterLabel = new()
    {
        TextColor = Color.FromArgb("#B7C1CE"),
        FontSize = 12,
        HorizontalTextAlignment = TextAlignment.Center,
        VerticalTextAlignment = TextAlignment.Center
    };
    private readonly Label _tagLabel = new()
    {
        TextColor = Color.FromArgb("#D6A73B"),
        FontSize = 12,
        LineBreakMode = LineBreakMode.TailTruncation,
        MaxLines = 1
    };

    private int _index;
    private bool _chromeVisible = true;

    public ViewerPage(IReadOnlyList<EagleAsset> assets, int startIndex, EagleImageSourceService imageSources)
    {
        _assets = assets;
        _imageSources = imageSources;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, assets.Count - 1));

        BackgroundColor = Colors.Black;
        NavigationPage.SetHasNavigationBar(this, false);
        Content = CreateLayout();
        AddGestures();
        ShowAsset();
    }

    private View CreateLayout()
    {
        var backButton = CreateIconButton("←");
        backButton.Clicked += async (_, _) => await Navigation.PopAsync();

        var previousButton = CreateIconButton("‹");
        previousButton.Clicked += (_, _) => Move(-1);

        var nextButton = CreateIconButton("›");
        nextButton.Clicked += (_, _) => Move(1);

        var top = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10,
            Children = { backButton, _titleLabel, _counterLabel }
        };
        Grid.SetColumn(_titleLabel, 1);
        Grid.SetColumn(_counterLabel, 2);

        var bottom = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 10,
            Children = { previousButton, _tagLabel, nextButton }
        };
        Grid.SetColumn(_tagLabel, 1);
        Grid.SetColumn(nextButton, 2);

        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        _chrome.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        _chrome.Children.Add(top);
        _chrome.Children.Add(bottom);
        Grid.SetRow(bottom, 2);

        return new Grid
        {
            Children = { _image, _chrome }
        };
    }

    private void AddGestures()
    {
        if (Content is View root)
        {
            root.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Left, 1));
            root.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Right, -1));
            root.GestureRecognizers.Add(CreateTapGesture());
        }
        _image.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Left, 1));
        _image.GestureRecognizers.Add(CreateSwipeGesture(SwipeDirection.Right, -1));
        _image.GestureRecognizers.Add(CreateTapGesture());
    }

    private SwipeGestureRecognizer CreateSwipeGesture(SwipeDirection direction, int delta)
    {
        var gesture = new SwipeGestureRecognizer { Direction = direction };
        gesture.Swiped += (_, _) => Move(delta);
        return gesture;
    }

    private TapGestureRecognizer CreateTapGesture()
    {
        var gesture = new TapGestureRecognizer();
        gesture.Tapped += (_, _) => ToggleChrome();
        return gesture;
    }

    private void Move(int delta)
    {
        if (_assets.Count == 0)
        {
            return;
        }

        _index = Math.Clamp(_index + delta, 0, _assets.Count - 1);
        ShowAsset();
    }

    private void ShowAsset()
    {
        if (_assets.Count == 0)
        {
            _image.Source = null;
            _titleLabel.Text = string.Empty;
            _counterLabel.Text = "0 / 0";
            _tagLabel.Text = string.Empty;
            return;
        }

        var asset = _assets[_index];
        _image.Source = _imageSources.CreateFullSource(asset);
        _titleLabel.Text = asset.Name;
        _counterLabel.Text = $"{_index + 1:N0} / {_assets.Count:N0}";
        _tagLabel.Text = asset.Tags.Count == 0 ? string.Empty : string.Join("  ", asset.Tags);
    }

    private void ToggleChrome()
    {
        _chromeVisible = !_chromeVisible;
        _chrome.IsVisible = _chromeVisible;
    }

    private static Button CreateIconButton(string text)
    {
        return new Button
        {
            Text = text,
            WidthRequest = 42,
            HeightRequest = 38,
            Padding = 0,
            FontSize = 24,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8
        };
    }
}




