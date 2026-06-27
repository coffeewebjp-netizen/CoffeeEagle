using CoffeeEagle.Reader.Pages;

namespace CoffeeEagle.Reader;

public partial class App : Application
{
    private readonly BookshelfPage _bookshelfPage;

    public App(BookshelfPage bookshelfPage)
    {
        _bookshelfPage = bookshelfPage;
        InitializeComponent();
        UserAppTheme = AppTheme.Dark;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var navigationPage = new NavigationPage(_bookshelfPage)
        {
            BarBackgroundColor = Color.FromArgb("#0B0E12"),
            BarTextColor = Colors.White
        };

        return new Window(navigationPage);
    }
}
