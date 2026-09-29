using Microsoft.Extensions.Logging;
using ShelfScan.Core;

namespace ShelfScan.App;

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

#if DEBUG
		builder.Logging.AddDebug();
#endif

		builder.Services.AddSingleton(_ => new Library(Path.Combine(FileSystem.AppDataDirectory, "books.json")));
		builder.Services.AddSingleton(_ => new OpenLibraryClient(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }));
		builder.Services.AddSingleton<MainPage>();

		return builder.Build();
	}
}
