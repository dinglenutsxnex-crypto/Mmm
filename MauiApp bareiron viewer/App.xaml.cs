namespace MauiApp_bareiron_viewer
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new MainPage()) { Title = "MauiApp bareiron viewer" };
        }
    }
}
