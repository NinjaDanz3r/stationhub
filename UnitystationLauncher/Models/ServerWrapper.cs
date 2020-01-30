using System;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Serilog;
using System.Diagnostics;
using System.Reactive.Subjects;
using Avalonia;
using Reactive.Bindings;
using System.Threading;
using Humanizer.Bytes;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Avalonia.Controls.ApplicationLifetimes;
using Humanizer;

namespace UnitystationLauncher.Models
{
    public class ServerWrapper : Server
    {
        private readonly AuthManager authManager;
        private readonly InstallationManager installManager;
        private CancellationTokenSource? cancelSource;
        public ServerWrapper(Server server, AuthManager authManager, InstallationManager installManager)
        {
            this.authManager = authManager;
            this.installManager = installManager;
            ServerName = server.ServerName;
            ForkName = server.ForkName;
            BuildVersion = server.BuildVersion;
            CurrentMap = server.CurrentMap;
            GameMode = server.GameMode;
            IngameTime = server.IngameTime;
            PlayerCount = server.PlayerCount;
            ServerIP = server.ServerIP;
            ServerPort = server.ServerPort;
            WinDownload = server.WinDownload;
            OSXDownload = server.OSXDownload;
            LinuxDownload = server.LinuxDownload;

            CanPlay.Subscribe(OnCanPlayChange);
            CheckIfCanPlay();
            Start = ReactiveUI.ReactiveCommand.Create(StartImp, null);
            Ping pingSender = new Ping();
            pingSender.PingCompleted += new PingCompletedEventHandler(PingCompletedCallback);
            pingSender.SendAsync(ServerIP, 7);
        }

        public ReactiveProperty<bool> CanPlay { get; } = new ReactiveProperty<bool>();
        public ReactiveProperty<bool> IsDownloading { get; } = new ReactiveProperty<bool>();
        public ReactiveProperty<string> ButtonText { get; } = new ReactiveProperty<string>();
        public ReactiveProperty<string> DownloadProgText { get; } = new ReactiveProperty<string>();
        public ReactiveProperty<string> RoundTrip { get; } = new ReactiveProperty<string>();
        public Subject<int> Progress { get; set; } = new Subject<int>();
        public ReactiveUI.ReactiveCommand<Unit, Unit> Start { get; }

        bool ClientInstalled => Installation.FindExecutable(InstallationPath) != null;

        public void PingCompletedCallback(object sender, PingCompletedEventArgs e)
        {
            // If an error occurred, display the exception to the user.  
            if (e.Error != null)
            {
                Log.Information("Ping failed:");
                Log.Information(e.Error.ToString());
                return;
            }
            var tripTime = e.Reply.RoundtripTime;
            if(tripTime == 0)
            {
                RoundTrip.Value = "null";
            }
            else
            {
                RoundTrip.Value = $"{e.Reply.RoundtripTime}ms";
            }   
        }

        public void CheckIfCanPlay()
        {
            CanPlay.Value = ClientInstalled;
            OnCanPlayChange(CanPlay.Value);
        }

        private void OnCanPlayChange(bool canPlay)
        {
            ButtonText.Value = canPlay ? "PLAY" : "DOWNLOAD";
        }

        public async Task DownloadAsync(CancellationToken cancelToken)
        {
            ButtonText.Value = "CANCEL";
            DownloadProgText.Value = "Connecting..";
            IsDownloading.Value = true;
            Log.Information("Download requested...");
            Log.Information("Installation path: \"{Path}\"", InstallationPath);

            if (Directory.Exists(InstallationPath))
            {
                Log.Information("Installation path already occupied");
                return;
            }

            Log.Information("Download URL: \"{URL}\"", DownloadUrl);

            if (DownloadUrl is null)
            {
                Log.Error("OS download is null");
                return;
            }

            Log.Information("Download started...");
            var webRequest = WebRequest.Create(DownloadUrl);
            var webResponse = await webRequest.GetResponseAsync();
            var responseStream = webResponse.GetResponseStream();
            Log.Information("Download connection established");
            var length = webResponse.ContentLength;
            using var progStream = new ProgressStream(responseStream);
            var throttledProgress = progStream.Progress
                .DistinctUntilChanged(p => p * 100 / length);

            throttledProgress
                .Subscribe(p =>
                {
                    DownloadProgText.Value = $"{p.Bytes().ToString("###")} / {length.Bytes().ToString("###")}";
                    Log.Information("Progress: {prog}", p);
                });
            throttledProgress.Subscribe(p => Progress.OnNext((int)(p * 100 / length)));

            await Task.Run(() =>
            {
                Log.Information("Extracting...");
                try
                {
                    var archive = new ZipArchive(progStream);
                    archive.ExtractToDirectory(InstallationPath, true);

                    Log.Information("Download completed");
                    var exe = Installation.FindExecutable(InstallationPath);
                    Config.SetPermissions(exe);
                }
                catch
                {
                    Log.Information("Extracting stopped");
                }
            }, cancelToken);
            IsDownloading.Value = false;
        }

        private async void StartImp()
        {
            if (IsDownloading.Value)
            {
                cancelSource?.Cancel();
                if (Directory.Exists(InstallationPath))
                {
                    Directory.Delete(InstallationPath, true);
                }
                Log.Information("User cancelled download");
                return;
            }

            if (CanPlay.Value)
            {
                var exe = Installation.FindExecutable(InstallationPath);
                if (exe != null)
                {
                    if (Application.Current.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
                    {
                        desktopLifetime.MainWindow.WindowState = Avalonia.Controls.WindowState.Minimized;
                    }
                    ProcessStartInfo startInfo;

                    if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) || RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    {
                        startInfo = new ProcessStartInfo("open", $"-a {exe} --args --server {ServerIP} --port {ServerPort} --refreshtoken {authManager.CurrentRefreshToken} --uid {authManager.UID}");
                        Log.Information("Start osx | linux");
                    } else
                    {
                        startInfo = new ProcessStartInfo(exe, $"--server {ServerIP} --port {ServerPort} --refreshtoken {authManager.CurrentRefreshToken} --uid {authManager.UID}");
                    }
                    startInfo.UseShellExecute = false;
                    var process = new Process();
                    process.StartInfo = startInfo;
                                        
                    process.Start();
                }
            }
            else
            {
                //DO DOWNLOAD
                cancelSource = new CancellationTokenSource();
                await DownloadAsync(cancelSource.Token);
                CheckIfCanPlay();
                installManager.TryAutoRemove();
            }
        }
    }
}