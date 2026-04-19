using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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

        var tracertPath = Path.Combine(Environment.SystemDirectory, "tracert.exe");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = File.Exists(tracertPath) ? tracertPath : "tracert",
                Arguments = $"-d -h 12 -w 1000 {host}",
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

        var output = new StringBuilder();

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(token);
            if (line is null)
            {
                break;
            }

            AppendTracerouteLine(output, line);
        }

        await process.WaitForExitAsync(token);
        var stderr = (await process.StandardError.ReadToEndAsync(token)).Trim();

        _tracerouteProcess = null;

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            AppendTracerouteLine(output, stderr);
        }

        SetText(
            _tracerouteText,
            output.Length == 0 ? "Traceroute returned no output." : output.ToString().TrimEnd()
        );
    }

    private void AppendTracerouteLine(StringBuilder output, string line)
    {
        output.AppendLine(line);
        SetText(_tracerouteText, output.ToString().TrimEnd());
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
            target.CaretIndex = value.Length;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        CancelRefresh();
        base.OnClosed(e);
    }
}
