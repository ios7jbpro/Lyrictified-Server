using System.Diagnostics;
using System.Drawing;

namespace Lyrictified.Server;

public sealed class TrayIconService : IHostedService, IDisposable
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly LyrictifiedSettings _settings;
    private readonly ILogger<TrayIconService> _logger;
    private Thread? _uiThread;
    private CancellationTokenSource? _cts;
    private NotifyIcon? _notifyIcon;
    private Icon? _icon;
    private bool _disposed;

    public TrayIconService(IHostApplicationLifetime lifetime, LyrictifiedSettings settings, ILogger<TrayIconService> logger)
    {
        _lifetime = lifetime;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _uiThread = new Thread(RunTrayIcon)
        {
            IsBackground = false,
            Name = "TrayIconThread"
        };
        _uiThread.SetApartmentState(ApartmentState.STA);
        _uiThread.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        if (_uiThread != null && _uiThread.IsAlive)
        {
            _uiThread.Join(TimeSpan.FromSeconds(3));
        }
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Dispose();
        _icon?.Dispose();
        _notifyIcon?.Dispose();
    }

    private void RunTrayIcon()
    {
        try
        {
            using var form = new Form();
            _ = form.Handle;

            var iconPath = Path.Combine(AppContext.BaseDirectory, "lyrictified-server.png");
            if (File.Exists(iconPath))
            {
                using var bitmap = new Bitmap(iconPath);
                var hIcon = bitmap.GetHicon();
                _icon = Icon.FromHandle(hIcon);
            }

            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = "Lyrictified Server",
                Visible = true
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Open Admin", null, (_, _) => OpenAdmin());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => _lifetime.StopApplication());
            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (_, _) => OpenAdmin();

            using (_cts!.Token.Register(() => form.Invoke(new Action(Application.Exit))))
            {
                Application.Run();
            }

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;

            _icon?.Dispose();
            _icon = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray icon failed to start.");
        }
    }

    private void OpenAdmin()
    {
        try
        {
            var host = _settings.BindAddress is "0.0.0.0" or "+" or "*" ? "localhost" : _settings.BindAddress;
            var url = $"http://{host}:{_settings.Port}/admin";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to open admin page.");
        }
    }
}
