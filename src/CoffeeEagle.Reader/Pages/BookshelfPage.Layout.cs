using CoffeeEagle.Reader.Models;
using CoffeeEagle.Reader.Services;
using Microsoft.Maui.Controls.Shapes;

namespace CoffeeEagle.Reader.Pages;

public sealed partial class BookshelfPage
{

    private View CreateLayout()
    {
        var topBar = new Grid
        {
            Padding = new Thickness(14, 12, 14, 6),
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { _titleLabel, _busyIndicator, _refreshButton }
        };
        Grid.SetColumn(_busyIndicator, 1);
        Grid.SetColumn(_refreshButton, 2);

        var controls = new Grid
        {
            Padding = new Thickness(14, 4, 14, 8),
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            },
            RowSpacing = 8
        };

        var actionGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Auto)
            },
            ColumnSpacing = 8,
            Children = { _libraryButton, _folderButton, _tagButton, _densityButton }
        };
        Grid.SetColumn(_folderButton, 1);
        Grid.SetColumn(_tagButton, 2);
        Grid.SetColumn(_densityButton, 3);

        controls.Children.Add(actionGrid);
        controls.Children.Add(_searchBar);
        controls.Children.Add(_summaryLabel);
        controls.Children.Add(_syncStatusPanel);
        Grid.SetRow(_searchBar, 1);
        Grid.SetRow(_summaryLabel, 2);
        Grid.SetRow(_syncStatusPanel, 3);

        var listLayer = new Grid
        {
            Children = { _assetsView, _emptyActions }
        };

        _startupLayer.Children.Add(_startupIcon);

        var root = new Grid
        {
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star)
            },
            Children = { topBar, controls, listLayer, _startupLayer }
        };
        Grid.SetRow(controls, 1);
        Grid.SetRow(listLayer, 2);
        Grid.SetRowSpan(_startupLayer, 3);
        return root;
    }


    private async Task PlayStartupAnimationAsync()
    {
        if (_playedStartupAnimation)
        {
            return;
        }

        _playedStartupAnimation = true;
        _startupLayer.IsVisible = true;
        _startupLayer.InputTransparent = false;
        _startupLayer.Opacity = 1;
        _startupIcon.Opacity = 0;
        _startupIcon.Scale = 0.9;

        await Task.Delay(220);
        await _startupIcon.FadeToAsync(1, 520, Easing.CubicOut);
        await Task.Delay(120);
        await RunStartupExitAnimationAsync();
        _startupLayer.IsVisible = false;
        _startupLayer.InputTransparent = true;
    }


    private Task RunStartupExitAnimationAsync()
    {
        var completed = new TaskCompletionSource();
        var animation = new Animation();
        animation.Add(0, 1, new Animation(
            value => _startupIcon.Scale = value,
            0.9,
            5.6,
            Easing.CubicIn));
        animation.Add(0, 1, new Animation(
            value => _startupIcon.Opacity = value,
            1,
            0,
            Easing.CubicIn));
        animation.Add(0, 1, new Animation(
            value => _startupLayer.Opacity = value,
            1,
            0,
            Easing.CubicIn));
        animation.Commit(
            this,
            "StartupExit",
            rate: 16,
            length: 1280,
            finished: (_, _) => completed.TrySetResult());
        return completed.Task;
    }


    private View CreateAssetCard()
    {
        var image = new Image
        {
            Aspect = Aspect.AspectFill,
            BackgroundColor = Color.FromArgb("#171D25"),
            HeightRequest = 112
        };

        var title = new Label
        {
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White,
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var meta = new Label
        {
            FontSize = 11,
            TextColor = Color.FromArgb("#98A4B5"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var tag = new Label
        {
            FontSize = 10,
            TextColor = Color.FromArgb("#D6A73B"),
            LineBreakMode = LineBreakMode.TailTruncation,
            MaxLines = 1
        };

        var stack = new VerticalStackLayout
        {
            Spacing = 5,
            Children = { image, title, meta, tag }
        };

        var card = new Border
        {
            Padding = new Thickness(7),
            Margin = new Thickness(5),
            Stroke = Color.FromArgb("#273241"),
            StrokeThickness = 1,
            StrokeShape = new RoundRectangle { CornerRadius = 8 },
            BackgroundColor = Color.FromArgb("#12171D"),
            Content = stack,
            HeightRequest = GetCardHeight()
        };

        card.BindingContextChanged += (_, _) =>
        {
            if (card.BindingContext is not EagleAsset asset)
            {
                return;
            }

            image.HeightRequest = GetImageHeight();
            card.HeightRequest = GetCardHeight();
            image.Source = _imageSources.CreateThumbnailSource(asset);
            title.Text = asset.Name;
            meta.Text = CreateAssetMetaText(asset);
            tag.Text = asset.Tags.Count == 0 ? string.Empty : string.Join("  ", asset.Tags.Take(3));
        };

        return card;
    }


    private static Button CreateSheetButton(string text, Color background, Color textColor)
    {
        return new Button
        {
            Text = text,
            FontSize = 13,
            FontAttributes = FontAttributes.Bold,
            TextColor = textColor,
            BackgroundColor = background,
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 38,
            Padding = new Thickness(14, 0)
        };
    }


    private VerticalStackLayout CreateEmptyActions()
    {
        return new VerticalStackLayout
        {
            Padding = new Thickness(28, 0),
            Spacing = 12,
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            WidthRequest = 320,
            Children = { _emptyLabel, _googleDriveSelectButton, _deviceFolderSelectButton }
        };
    }


    private static Button CreatePrimaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 15,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#06130F"),
            BackgroundColor = Color.FromArgb("#21C7A8"),
            CornerRadius = 8,
            HeightRequest = 48,
            Padding = new Thickness(14, 0)
        };
    }


    private static Button CreateSecondaryButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 14,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#EDF5F3"),
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            HeightRequest = 46,
            Padding = new Thickness(14, 0)
        };
    }

    private static Button CreateHeaderButton(string text)
    {
        return new Button
        {
            Text = text,
            FontSize = 12,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#EDF5F3"),
            BackgroundColor = Color.FromArgb("#172029"),
            BorderColor = Color.FromArgb("#2B3948"),
            BorderWidth = 1,
            CornerRadius = 8,
            Padding = new Thickness(10, 0),
            HeightRequest = 38,
            MinimumWidthRequest = 54
        };
    }
}
