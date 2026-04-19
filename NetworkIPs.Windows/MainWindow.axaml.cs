using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace NetworkIPs.Windows;

public partial class MainWindow : Window
{
    private TextBlock? _publicIPText;
    private TextBlock? _publicStatusText;
    private TextBlock? _localIPText;
    private TextBlock? _tailscaleText;
    private TextBox? _tracerouteText;
    private Button? _refreshButton;
    private CancellationTokenSource? _refreshCts;
    private Process? _tracerouteProcess;

    public MainWindow()
    {
        InitializeComponent();
        AttachControls();
        Opened += (_, _) => _ = RefreshAsync();
    }

    private void AttachControls()
    {
        _publicIPText = this.FindControl<TextBlock>("PublicIPText");
        _publicStatusText = this.FindControl<TextBlock>("PublicStatusText");
        _localIPText = this.FindControl<TextBlock>("LocalIPText");
        _tailscaleText = this.FindControl<TextBlock>("TailscaleText");
        _tracerouteText = this.FindControl<TextBox>("TracerouteText");
        _refreshButton = this.FindControl<Button>("RefreshButton");
    }

    private async void RefreshButton_OnClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        CancelRefresh();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        var token = cts.Token;

        SetRefreshingState(true);
        SetText(_publicIPText, "...");
        SetText(_publicStatusText, "Fetching public IP...");
        SetText(_localIPText, "Detecting...");
        SetText(_tailscaleText, "Detecting...");
        SetText(_tracerouteText, "Waiting for public IP...");

        try
        {
            var snapshot = await NetworkProbe.CreateSnapshotAsync(token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            SetText(_publicIPText, snapshot.PublicIP);
            SetText(_publicStatusText, snapshot.PublicStatus);
            SetText(_localIPText, snapshot.LocalIP);
            SetText(_tailscaleText, snapshot.Tailscale);

            if (snapshot.ShouldRunTraceroute)
            {
                await RunTracerouteAsync(snapshot.PublicIP, token);
            }
            else
            {
                SetText(_tracerouteText, "Traceroute was not started.");
            }
        }
        catch (OperationCanceledException)
        {
            SetText(_publicStatusText, "Refresh canceled.");
        }
        catch (Exception ex)
        {
            SetText(_publicIPText, "n/a");
            SetText(_publicStatusText, $"Error: {ex.Message}");
            SetText(_tracerouteText, "Traceroute was not started.");
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
            }

            cts.Dispose();
            SetRefreshingState(false);
        }
    }

    private async Task RunTracerouteAsync(string host, CancellationToken token)
    {
        SetText(_tracerouteText, $"Running tracert to {host}...");
        StopTraceroute();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c tracert -d -h 12 {host}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        _tracerouteProcess = process;

        try
        {
            process.Start();
        }
        catch (Exception)
        {
            _tracerouteProcess = null;
            SetText(_tracerouteText, "Could not start tracert.");
            return;
        }

        using var registration = token.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);

        await process.WaitForExitAsync(token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        _tracerouteProcess = null;

        var output = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(value => !string.IsNullOrWhiteSpace(value))
        );

        SetText(
            _tracerouteText,
            string.IsNullOrWhiteSpace(output) ? "Traceroute returned no output." : output
        );
    }

    private void CancelRefresh()
    {
        _refreshCts?.Cancel();
        _refreshCts?.Dispose();
        _refreshCts = null;
        StopTraceroute();
    }

    private void StopTraceroute()
    {
        try
        {
            if (_tracerouteProcess is { HasExited: false })
            {
                _tracerouteProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
        finally
        {
            _tracerouteProcess?.Dispose();
            _tracerouteProcess = null;
        }
    }

    private void SetRefreshingState(bool isRefreshing)
    {
        if (_refreshButton is not null)
        {
            _refreshButton.IsEnabled = !isRefreshing;
        }
    }

    private static void SetText(TextBlock? target, string value)
    {
        if (target is not null)
        {
            target.Text = value;
        }
    }

    private static void SetText(TextBox? target, string value)
    {
        if (target is not null)
        {
            target.Text = value;
            target.CaretIndex = 0;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelRefresh();
        base.OnClosed(e);
    }
}
