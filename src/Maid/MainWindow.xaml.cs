using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Maid;

public partial class MainWindow : Window
{
    /// <summary>The UI is served from wwwroot under this origin. ".example" is reserved, so it never hits DNS.</summary>
    private const string UiHost = "maid.example";

    /// <summary>Starts the window title while the sessions run as administrator, as in cmd and PowerShell.</summary>
    private static readonly string TitlePrefix = Elevation.IsElevated ? "Administrator: " : "";

    /// <summary>Maid's version as written in Maid.csproj (<c>&lt;Version&gt;</c>), without the commit the SDK appends after '+'.</summary>
    internal static readonly string AppVersion = typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";

    /// <summary>"Administrator: Maid 1.1": what the window title starts with, whatever the page adds.</summary>
    private static readonly string AppTitle = (TitlePrefix + "Maid " + AppVersion).TrimEnd();

    private readonly StateStore _store;
    private Dashboard? _dashboard;

    internal MainWindow(StateStore store)
    {
        _store = store;
        InitializeComponent();
        Title = AppTitle;
        WebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x11, 0x14, 0x1A);
        RestorePlacement();
        Loaded += async (_, _) => await InitializeWebViewAsync();
        Activated += (_, _) => { if (WebView.CoreWebView2 is not null) WebView.Focus(); };
    }

    /// <summary>
    /// The page's title is "Maid" and what it adds (" — 2 sessions · 1 waiting for you", see updateSummary in app.js):
    /// the window shows that after its own start, which carries the prefix and the version.
    /// </summary>
    private static string WindowTitle(string? pageTitle) =>
        AppTitle + (pageTitle is not null && pageTitle.StartsWith("Maid", StringComparison.Ordinal) ? pageTitle[4..] : "");

    private async Task InitializeWebViewAsync()
    {
        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(_store.DataDirectory, "WebView2"));
            await WebView.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "Maid needs the Microsoft Edge WebView2 Runtime, which was not found on this machine.\n\n" +
                "Install it from https://developer.microsoft.com/microsoft-edge/webview2/ and start Maid again.",
                "Maid", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        var core = WebView.CoreWebView2;

        var settings = core.Settings;
        // Ctrl+R, Ctrl+F, F5 and friends belong to the terminal, not to the browser.
        settings.AreBrowserAcceleratorKeysEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPinchZoomEnabled = false;
        settings.IsSwipeNavigationEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif

        _dashboard = new Dashboard(_store, Dispatcher, PostToPage)
        {
            BrowseForFolder = BrowseForFolder,
        };
        _dashboard.SyncProjects();
        _dashboard.StartVoicePipe();
        _dashboard.StartApiPipe();

        core.SetVirtualHostNameToFolderMapping(UiHost, Path.Combine(AppContext.BaseDirectory, "wwwroot"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.WebMessageReceived += (_, e) => _dashboard.HandleMessage(e.WebMessageAsJson);
        core.DocumentTitleChanged += (_, _) => Title = WindowTitle(core.DocumentTitle);
        core.NavigationStarting += (_, e) =>
        {
            // The dashboard is the only page this window ever shows; links open in the real browser.
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Host != UiHost)
                e.Cancel = true;
        };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.ProcessFailed += (_, e) =>
        {
            // Sessions live in this process, not in the page: reload and the UI re-attaches to them.
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                core.Reload();
        };

        core.Navigate($"https://{UiHost}/index.html");
        WebView.Focus();
    }

    private void PostToPage(string json)
    {
        try
        {
            WebView.CoreWebView2?.PostWebMessageAsJson(json);
        }
        catch (Exception e) when (e is InvalidOperationException or COMException or ObjectDisposedException)
        {
            // The browser side is gone (control disposed, or its process died and is being restarted).
            // Sessions keep running regardless; the page asks for the current state when it comes back.
        }
    }

    private string? BrowseForFolder(string? initialDirectory, string title)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
            dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;

        int running = _dashboard?.RunningCount ?? 0;
        if (running > 0)
        {
            var answer = MessageBox.Show(this,
                running == 1
                    ? "1 session is still running.\n\nClose Maid and stop it?"
                    : $"{running} sessions are still running.\n\nClose Maid and stop them?",
                "Maid", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        SavePlacement();
        // Stopping the sessions and saving their conversations can take a few seconds; the window
        // should not sit there unresponsive meanwhile.
        Hide();
        _dashboard?.Shutdown();
    }

    // ---- window placement -------------------------------------------------------------------------

    private void RestorePlacement()
    {
        if (_store.State.Window is not { Width: >= 400, Height: >= 300 } saved) return;

        // Ignore a placement that is no longer on any screen (monitor unplugged, resolution changed).
        var screens = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        bounds.Intersect(screens);
        if (!bounds.IsEmpty && bounds.Width >= 200 && bounds.Height >= 100)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
        }
        if (saved.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (bounds.IsEmpty) return;
        _store.State.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        _store.Save();
    }

    // ---- native window touches --------------------------------------------------------------------

    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_KEYMENU = 0xF100;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;

        int enabled = 1;
        DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, sizeof(int));
        int captionColor = 0x001A1411; // COLORREF (0x00BBGGRR) of the UI's top bar, #11141A
        DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

        HwndSource.FromHwnd(handle)?.AddHook(WindowProc);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // Tapping Alt would put the window into menu mode, which swallows the next keystroke. Alt is a
        // regular modifier inside a terminal (Alt+Enter, Alt+P...), so only Alt+Space keeps its meaning.
        if (message == WM_SYSCOMMAND && ((int)wParam & 0xFFF0) == SC_KEYMENU && lParam == 0)
            handled = true;
        return 0;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
