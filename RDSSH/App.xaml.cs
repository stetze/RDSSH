using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Xaml;
using Windows.Storage;

using RDSSH.Activation;
using RDSSH.Contracts.Services;
using RDSSH.Core.Contracts.Services;
using RDSSH.Core.Services;
using RDSSH.Helpers;
using RDSSH.Models;
using RDSSH.Services;
using RDSSH.ViewModels;
using RDSSH.Views;

using Microsoft.Windows.AppLifecycle;

using System.Threading;

using System.Threading.Tasks;

using Microsoft.UI.Dispatching;

namespace RDSSH;

// To learn more about WinUI 3, see https://docs.microsoft.com/windows/apps/winui/winui3/.
public partial class App : Application
{
    private readonly SemaphoreSlim _activationGate = new(1, 1);
    private bool _initialAppLifecycleActivationHandled;

    // The .NET Generic Host provides dependency injection, configuration, logging, and other services.
    // https://docs.microsoft.com/dotnet/core/extensions/generic-host
    // https://docs.microsoft.com/dotnet/core/extensions/dependency-injection
    // https://docs.microsoft.com/dotnet/core/extensions/configuration
    // https://docs.microsoft.com/dotnet/core/extensions/logging
    public IHost Host
    {
        get;
    }

    public static T GetService<T>()
        where T : class
    {
        if ((App.Current as App)!.Host.Services.GetService(typeof(T)) is not T service)
        {
            throw new ArgumentException($"{typeof(T)} needs to be registered in ConfigureServices within App.xaml.cs.");
        }

        return service;
    }

    private static WindowEx? _mainWindow;

    public static WindowEx MainWindow
    {
        get
        {
            if (_mainWindow is null)
            {
                _mainWindow = new MainWindow();
            }

            return _mainWindow;
        }
    }

    public static UIElement? AppTitlebar { get; set; }

    public static Views.SessionsHostWindow? SessionsWindow { get; private set; }

    public static Views.SessionsHostWindow GetOrCreateSessionsWindow()
    {
        if (SessionsWindow == null)
        {
            SessionsWindow = new Views.SessionsHostWindow();
            SessionsWindow.Closed += (_, __) => SessionsWindow = null;
        }

        SessionsWindow.Activate();
        SessionsWindow.BringToFront();
        return SessionsWindow;
    }

    public App()
    {
        InitializeComponent();


        // Handle redirected activations (e.g., rdssh://...) while app is running
        AppInstance.GetCurrent().Activated += async (_, e) =>
        {
            await HandleAppLifecycleActivationAsync(e);
        };

        Host = Microsoft.Extensions.Hosting.Host.
                CreateDefaultBuilder().
                UseContentRoot(AppContext.BaseDirectory).
                ConfigureServices((context, services) =>
                {
                    // Default Activation Handler
                    services.AddTransient<ActivationHandler<LaunchActivatedEventArgs>, DefaultActivationHandler>();

                    // Other Activation Handlers

                    // Services
                    services.AddSingleton<ILocalSettingsService, LocalSettingsService>();
                    services.AddSingleton<IThemeSelectorService, ThemeSelectorService>();
                    services.AddTransient<INavigationViewService, NavigationViewService>();

                    // Localization service
                    services.AddSingleton<ILocalizationService, LocalizationService>();

                    services.AddSingleton<IActivationService, ActivationService>();
                    services.AddSingleton<IPageService, PageService>();
                    services.AddSingleton<INavigationService, NavigationService>();

                    // Core Services
                    services.AddSingleton<IFileService, FileService>();

                    // Credential service (used by SettingsPage)
                    services.AddSingleton<CredentialService>();

                    // Hostlist service (stores connections)
                    services.AddSingleton<HostlistService>();

                    // Connection launcher (opens/focuses tabs)
                    services.AddSingleton<ConnectionLauncherService>();

                    // Views and ViewModels
                    services.AddTransient<SettingsViewModel>();
                    services.AddTransient<SettingsPage>();
                    services.AddTransient<SessionsViewModel>();
                    services.AddTransient<SessionsPage>();
                    services.AddTransient<ShellPage>();
                    services.AddTransient<ShellViewModel>();

                    // Configuration
                    services.Configure<LocalSettingsOptions>(context.Configuration.GetSection(nameof(LocalSettingsOptions)));
                }).
                Build();

        // Wire language change to update main window title and refresh current page
        var localization = GetService<ILocalizationService>();
        localization.LanguageChanged += (s, e) =>
        {
            if (MainWindow != null)
            {
                MainWindow.Title = "AppDisplayName".GetLocalized();
            }

            // Try to refresh the currently displayed page so XAML x:Uid resources reapply.
            try
            {
                var navigationService = GetService<INavigationService>();
                var frame = navigationService.Frame;
                var vm = frame?.GetPageViewModel();
                if (vm != null)
                {
                    var vmKey = vm.GetType().FullName!;
                    // Use a changing parameter to force the NavigationService to recreate the page
                    navigationService.NavigateTo(vmKey, parameter: System.Guid.NewGuid().ToString());
                }
            }
            catch
            {
                // ignore refresh failures
            }
        };

        // Apply previously saved language setting on startup (if present)
        try
        {
            if (ApplicationData.Current?.LocalSettings?.Values != null && ApplicationData.Current.LocalSettings.Values.TryGetValue("LanguageSetting", out var langObj) && langObj is string langStr && !string.IsNullOrWhiteSpace(langStr))
            {
                localization.ApplyLanguage(langStr);
            }
            else
            {
                // If setting is absent or empty, ensure LocalizationService uses system default (no override)
                localization.ApplyLanguage(string.Empty);
            }
        }
        catch
        {
            // ignore failures applying saved language
        }

        UnhandledException += App_UnhandledException;
    }

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        // TODO: Log and handle exceptions as appropriate.
        // https://docs.microsoft.com/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.application.unhandledexception.
    }

    protected async override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // MSIX/Store: protocol activations often start a secondary process.
        // Redirect to the primary instance BEFORE creating any UI.
        var mainInstance = AppInstance.FindOrRegisterForKey("RDSSH_MAIN");
        if (!mainInstance.IsCurrent)
        {
            try
            {
                var activatedArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
                await mainInstance.RedirectActivationToAsync(activatedArgs);
            }
            catch
            {
                // ignore redirect failures
            }

            Exit();
            return;
        }

        base.OnLaunched(args);

        // Normal activation (creates MainWindow etc.)
        await GetService<IActivationService>().ActivateAsync(args);

        // Handle initial activation (e.g., cold-start via rdssh://...)
        if (!_initialAppLifecycleActivationHandled)
        {
            _initialAppLifecycleActivationHandled = true;
            await HandleAppLifecycleActivationAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
        }
    }

    private async Task HandleAppLifecycleActivationAsync(AppActivationArguments? act)
    {
        if (act == null || act.Kind != ExtendedActivationKind.Protocol)
            return;

        await _activationGate.WaitAsync();
        try
        {
            if (act.Data is Windows.ApplicationModel.Activation.ProtocolActivatedEventArgs p)
            {
                var uri = p.Uri;
                if (uri == null)
                    return;

                // rdssh://connect?id=<guid>
                string? idRaw = null;
                try
                {
                    var decoder = new Windows.Foundation.WwwFormUrlDecoder(uri.Query ?? string.Empty);
                    idRaw = decoder.GetFirstValueByName("id");
                }
                catch
                {
                    // ignore malformed query
                }

                if (!Guid.TryParse(idRaw, out var connectionId))
                    return;

                // Ensure we switch to the UI thread before touching windows/controls.
                var dq = MainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
                dq.TryEnqueue(async () =>
                {
                    try
                    {
                        // Make sure a window is visible (protocol activation while app is minimized/background)
                        MainWindow.Activate();
                        MainWindow.BringToFront();

                        var launcher = GetService<ConnectionLauncherService>();
                        await launcher.StartRdpAsync(connectionId);
                    }
                    catch
                    {
                        // ignore activation errors
                    }
                });
            }
        }
        finally
        {
            _activationGate.Release();
        }
    }

}
