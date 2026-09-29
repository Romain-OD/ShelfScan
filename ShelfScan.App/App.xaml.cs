namespace ShelfScan.App;

public partial class App : Application
{
    private readonly MainPage mainPage;

    public App(MainPage mainPage)
    {
        InitializeComponent();
        this.mainPage = mainPage;
    }

    // Two pages don't need Shell: a NavigationPage pushes the scan page and pops back.
    protected override Window CreateWindow(IActivationState? activationState) => new(new NavigationPage(mainPage));
}
