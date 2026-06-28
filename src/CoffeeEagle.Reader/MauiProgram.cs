using CoffeeEagle.Reader.Pages;
using CoffeeEagle.Reader.Services;
using Microsoft.Extensions.Logging;

namespace CoffeeEagle.Reader;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        builder.Services.AddSingleton<AndroidDocumentTreeService>();
        builder.Services.AddSingleton<EagleLibraryIndexer>();
        builder.Services.AddSingleton<EagleLibraryStore>();
        builder.Services.AddSingleton<GoogleDriveLibraryService>();
        builder.Services.AddSingleton<EagleImageSourceService>();
        builder.Services.AddTransient<BookshelfPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}