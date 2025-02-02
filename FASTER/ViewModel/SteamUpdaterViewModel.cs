using BytexDigital.Steam.ContentDelivery;
using BytexDigital.Steam.ContentDelivery.Exceptions;
using BytexDigital.Steam.ContentDelivery.Models;
using BytexDigital.Steam.ContentDelivery.Models.Downloading;
using BytexDigital.Steam.Core;
using BytexDigital.Steam.Core.Enumerations;
using BytexDigital.Steam.Core.Structs;

using FASTER.Models;

using MahApps.Metro.Controls.Dialogs;

using Microsoft.AppCenter.Analytics;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace FASTER.ViewModel
{
    public sealed class SteamUpdaterViewModel : INotifyPropertyChanged
    {
        public SteamUpdaterViewModel()
        {
            Parameters = new SteamUpdaterModel();
        }
        
        private static readonly Lazy<SteamUpdaterViewModel>
            lazy =
                new(() => new SteamUpdaterViewModel(new SteamUpdaterModel()));

        public static SteamUpdaterViewModel Instance => lazy.Value;

        private SteamUpdaterViewModel(SteamUpdaterModel model)
        {
            Parameters                =  model;
            DownloadTasks.ListChanged += (_, _) => RaisePropertyChanged(nameof(IsDownloading));
            var timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            timer.Tick += Timer_Tick;
            timer.IsEnabled = true;
           
            
        }

        private bool _isLoggingIn;
        private bool _isDlOverride;
        private bool _updaterOnline;
        private bool _updaterFaulted;

        public SteamUpdaterModel            Parameters        { get; set; }
        
        
        public  IDialogCoordinator      DialogCoordinator { get; set; }
        private CancellationTokenSource tokenSource = new();

        public bool IsDownloading => DownloadTasks.Count > 0 || IsLoggingIn || IsDlOverride;

        private BindingList<Task> DownloadTasks { get; } = new BindingList<Task>();
        
        
        public bool UpdaterOnline
        {
            get => _updaterOnline;
            set
            {
                _updaterOnline = value;
                RaisePropertyChanged(nameof(UpdaterOnline));
            }
        }

        public bool UpdaterFaulted
        {
            get => _updaterFaulted;
            set
            {
                _updaterFaulted = value;
                RaisePropertyChanged(nameof(UpdaterFaulted));
            }
        }

        public bool IsLoggingIn
        {
            get => _isLoggingIn;
            set
            {
                _isLoggingIn = value;
                RaisePropertyChanged(nameof(IsLoggingIn));
                RaisePropertyChanged(nameof(IsDownloading));
            }
        }

        public bool IsDlOverride
        {
            get => _isDlOverride;
            set
            {
                _isDlOverride = value;
                RaisePropertyChanged(nameof(IsDlOverride));
                RaisePropertyChanged(nameof(IsDownloading));
            }
        }

        internal SteamClient SteamClient;
        internal SteamContentClient SteamContentClient;
        private SteamCredentials   _steamCredentials;

        public void PasswordChanged(string password)
        {
            Parameters.Password = Encryption.Instance.EncryptData(password);
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (SteamClient == null)
            {
                UpdaterFaulted = false;
                UpdaterOnline = false;
                return;
            }

            UpdaterFaulted = SteamClient.IsFaulted;
            UpdaterOnline = SteamClient.IsConnected;
        }

        internal string GetPw()
        { return Encryption.Instance.DecryptData(Parameters.Password); }

        public async Task UpdateClick()
        {
            Analytics.TrackEvent("Updater - Clicked Update", new Dictionary<string, string>
            {
                {"Name", Properties.Settings.Default.steamUserName},
                {"DLCs", $"{(Parameters.UsingGMDlc ? "GM " : "")}{(Parameters.UsingCSLADlc? "CSLA " : "")}{(Parameters.UsingPFDlc ? "SOG " : "")}{(Parameters.UsingWSDlc ? "WS " : "")}"},
                {"Branch", $"{(Parameters.UsingPerfBinaries? "Profiling" : "Public")}"}
            });

            Parameters.IsUpdating = true;
            Parameters.Output     = "Starting Update...";
            Parameters.Output += "\nPlease don't quit this page or cancel the download\nThis might take a while...";
            
            uint appId = 233780;

            IReadOnlyList<Depot> depotsList;
                
            try
            { depotsList = await GetAppDepots(appId); }
            catch
            {
                Parameters.Output += "\n\n /!\\ Something went wrong while getting the depots list. Check login/password and your internet connexion.\nAlternatively, clear the sentry folder and try again.";
                return;
            }
                
            
            if(depotsList == null || depotsList.Count == 0)
            {
                Parameters.Output += "\n\n /!\\ Could not retrieve depots list. PLease retry later or check your internet connection\nAlternatively, clear the sentry folder and try again.";
                return;
            }
            
            List<(uint id, string branch, string pass)> depotsDownload = new();

            Parameters.Output += "\nChecking Shared Content...";
            //Downloading Depot 233781 from either branch contact or public
            depotsDownload.Add((
                depotsList.FirstOrDefault(d => d.Name == "Arma 3 Alpha Dedicated Server Content (internal)")!.Id.Id, 
                Parameters.UsingContactDlc ? "contact" : "public", 
                null));

            Parameters.Output += "\nChecking Executables...";
            //Either downloading depot 233782 fow Windows from branch public or 233784 for windows in branch profiling
            if (Parameters.UsingPerfBinaries)
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Server Profiling - WINDOWS Depot")!.Id.Id,
                    "profiling",
                    "CautionSpecialProfilingAndTestingBranchArma3"));
            else
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Alpha Dedicated Server binary Windows (internal)")!.Id.Id,
                    "public",
                    null));

            //Downloading mods
            if (Parameters.UsingGMDlc)
            {
                Parameters.Output += "\nChecking Arma 3 Server Creator DLC - GM...";
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Server Creator DLC - GM")!.Id.Id,
                    "creatordlc",
                    null));
            }

            if (Parameters.UsingCSLADlc)
            {
                Parameters.Output += "\nChecking Arma 3 Server Creator DLC - CSLA...";
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Server Creator DLC - CSLA")!.Id.Id,
                    "creatordlc",
                    null));
            }

            if (Parameters.UsingPFDlc)
            {
                Parameters.Output += "\nChecking Arma 3 Server Creator DLC - SOGPF...";
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Server Creator DLC - SOGPF")!.Id.Id,
                    "creatordlc",
                    null));
            }

            if (Parameters.UsingWSDlc)
            {
                Parameters.Output += "\nChecking Arma 3 Server Creator DLC - Western Sahara...";
                depotsDownload.Add((
                    depotsList.FirstOrDefault(d => d.Name == "Arma 3 Server Creator DLC - WS")!.Id.Id,
                    "creatordlc",
                    null));
            }

            await RunServerUpdater(Parameters.InstallDirectory, appId, depotsDownload);

            Parameters.Output += "\n\nAll Done ! ";
        }

        private async Task<IReadOnlyList<Depot>> GetAppDepots(uint appId)
        {
            if (!await SteamLogin())
                return null;
            
            return await SteamContentClient.GetDepotsAsync(appId);
        }

        public void UpdateCancelClick()
        {
            Parameters.Output     += "\nUpdate Cancelled.";
            Parameters.IsUpdating =  false;

            tokenSource.Cancel();
        }

        public void ModStagingDirClick()
        {
            string path = MainWindow.Instance.SelectFolder(Parameters.ModStagingDirectory);

            if (path == null) 
                return;

            Parameters.ModStagingDirectory = path;
        }

        public void ServerDirClick()
        {
            string path = MainWindow.Instance.SelectFolder(Parameters.InstallDirectory);

            if (path == null)
                return;

            Parameters.InstallDirectory = path;
        }

        internal async Task<int> RunServerUpdater(string path, uint appId, List<(uint id, string branch, string pass)> depots)
        {
            if (string.IsNullOrWhiteSpace(path))
                return UpdateState.Cancelled;

            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            tokenSource = new CancellationTokenSource();

            if (!await SteamLogin())
                return UpdateState.LoginFailed;

            var _OS = SteamClient?.GetSteamOs().Identifier;
            Stopwatch sw = Stopwatch.StartNew();

            foreach (var depot in depots)
            {
                try
                {
                    SteamOs steamOs = new(_OS);
                    ManifestId manifestId;

                    manifestId = await SteamContentClient.GetDepotDefaultManifestIdAsync(appId, depot.id, depot.branch, depot.pass);

                    Parameters.Output += $"\nAttempting to start download of app {appId}, depot {depot.id}  ({depots.IndexOf(depot)+1}/{depots.Count})... ";
                    var downloadHandler = await SteamContentClient.GetAppDataAsync(appId, depot.id, manifestId, depot.branch, depot.pass, steamOs);
                    await Download(downloadHandler, path);
                }
                catch (Exception ex)
                {
                    Parameters.Output += $"\nError: {ex.Message}{(ex.InnerException != null ? $" Inner Exception: {ex.InnerException.Message}" : "")}";
                    return UpdateState.Error;
                }
            }
            sw.Stop();
            Parameters.Output += $"\nDone in {sw.Elapsed.Hours}h {sw.Elapsed.Minutes}m {sw.Elapsed.Seconds}s {sw.Elapsed.Milliseconds}ms";

            return 0;
        }

        public async Task<int> RunModUpdater(ulong modId, string path)
        {
            tokenSource = new CancellationTokenSource();

            try
            {
                if(SteamClient is {IsConnected: true})
                {
                    SteamClient?.Shutdown();
                    SteamClient?.Dispose();
                    SteamClient = null;
                }
                if (!await SteamLogin())
                    return UpdateState.LoginFailed;
            }
            catch(Exception)
            {
                return UpdateState.LoginFailed;
            }

            var _OS = SteamClient.GetSteamOs().Identifier;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                SteamOs    steamOs    = new(_OS);
                ManifestId manifestId = default;

                Parameters.Output += $"\nFetching mod {modId} infos... ";

                if (!SteamClient.Credentials.IsAnonymous) //IS SYNC ENABLED
                {
                    manifestId = (await SteamContentClient.GetPublishedFileDetailsAsync(modId)).hcontent_file;
                    Manifest manifest = await SteamContentClient.GetManifestAsync(107410, 107410, manifestId);

                    SyncDeleteRemovedFiles(path, manifest);
                }

                Parameters.Output += $"\nAttempting to start download of item {modId}... ";

                var downloadHandler = await SteamContentClient.GetPublishedFileDataAsync(
                                                                                          modId,
                                                                                          manifestId,
                                                                                          null,
                                                                                          null,
                                                                                          steamOs);

                await Download(downloadHandler, path);
            }
            catch (TaskCanceledException)
            {
                sw.Stop();
                SteamClient.Shutdown();
                SteamClient.Dispose();
                SteamClient = null;
                return UpdateState.Cancelled;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Parameters.Output += $"\nError: {ex.Message}{(ex.InnerException != null ? $" Inner Exception: {ex.InnerException.Message}" : "")}";
                SteamClient.Shutdown();
                SteamClient.Dispose();
                SteamClient = null;
                return UpdateState.Error;
            }

            sw.Stop();
            Parameters.Output += $"\nDownload completed, it took {sw.Elapsed.Minutes + sw.Elapsed.Hours * 60}m {sw.Elapsed.Seconds}s {sw.Elapsed.Milliseconds}ms";
            return UpdateState.Success;
        }


        public async Task<int> RunModsUpdater(ObservableCollection<ArmaMod> mods)
        {
            
            tokenSource = new CancellationTokenSource();
            if(!await SteamLogin())
            { 
                IsLoggingIn = false;
                return UpdateState.LoginFailed;
            }

            var _OS = SteamClient.GetSteamOs().Identifier;

            Parameters.Output += "\nAdding mods to download list...";

            SemaphoreSlim maxThread = new(1);
            var  ml = mods.Where(m => !m.IsLocal).ToList();
            uint finished = 0;
            IsDlOverride = true;

            foreach (ArmaMod mod in ml)
            {
                await maxThread.WaitAsync();

                _ = Task.Factory.StartNew(() =>
                {
                    if (!Directory.Exists(mod.Path))
                        Directory.CreateDirectory(mod.Path);

                    if (tokenSource.Token.IsCancellationRequested)
                    {
                        mod.Status = ArmaModStatus.NotComplete;
                        return;
                    }
                    Parameters.Output += $"\n   Starting {mod.WorkshopId}";

                    Stopwatch sw = Stopwatch.StartNew();
                    try
                    {
                        SteamOs steamOs = new(_OS);
                        ManifestId manifestId = default;
                        
                        if(mod.LocalLastUpdated > mod.SteamLastUpdated)
                        {
                            mod.Status = ArmaModStatus.UpToDate;
                            Parameters.Output += $"\n   Mod{mod.WorkshopId} already up to date. Ignoring...";
                            return;
                        }

                        if (!SteamClient.Credentials.IsAnonymous) //IS SYNC NEABLED
                        {
                            Parameters.Output += $"\n   Getting manifest for {mod.WorkshopId}";
                            manifestId = SteamContentClient.GetPublishedFileDetailsAsync(mod.WorkshopId).Result.hcontent_file;
                            Manifest manifest = SteamContentClient.GetManifestAsync(107410, 107410, manifestId).Result;
                            Parameters.Output += $"\n   Manifest retrieved {mod.WorkshopId}";
                            SyncDeleteRemovedFiles(mod.Path, manifest);
                        }

                        Parameters.Output += $"\n    Attempting to start download of item {mod.WorkshopId}... ";

                        var downloadHandler = SteamContentClient.GetPublishedFileDataAsync(mod.WorkshopId,
                                                                                            manifestId,
                                                                                            null,
                                                                                            null,
                                                                                            steamOs);
                        DownloadForMultiple(downloadHandler.Result, mod.Path).Wait();
                    }
                    catch (TaskCanceledException)
                    {
                        sw.Stop();
                        mod.Status = ArmaModStatus.NotComplete;
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        mod.Status = ArmaModStatus.NotComplete;
                        Parameters.Output += $"\nError: {ex.Message}{(ex.InnerException != null ? $" Inner Exception: {ex.InnerException.Message}" : "")}";
                    }

                    sw.Stop();
                    mod.Status = ArmaModStatus.UpToDate;
                    var nx = new DateTime(1970, 1, 1);
                    var ts = DateTime.UtcNow - nx;
                    mod.LocalLastUpdated = (ulong) ts.TotalSeconds;

                    Parameters.Output += $"\n    Download {mod.WorkshopId} completed, it took {sw.Elapsed.Minutes + sw.Elapsed.Hours*60}m {sw.Elapsed.Seconds}s {sw.Elapsed.Milliseconds}ms";


                }, TaskCreationOptions.LongRunning).ContinueWith((_) =>
                {
                    finished += 1;
                    Parameters.Output += $"\n   Thread {mod.WorkshopId} complete  ({finished} / {ml.Count})";
                    Parameters.Progress = finished * ml.Count / 100.00;
                    maxThread.Release();
                });
            }

            Parameters.Output += "\nAlmost there...";
            await maxThread.WaitAsync();
            
            
            Parameters.Output += "\nMods updated !";
            IsDlOverride = false;
            return UpdateState.Success;
        }
        
        internal async Task<bool> SteamLogin()
        {
            IsLoggingIn = true;
            var path = Path.Combine(Path.GetDirectoryName(ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.PerUserRoamingAndLocal).FilePath) ?? string.Empty, "sentries");
            SteamAuthenticationFilesProvider sentryFileProvider = new DirectorySteamAuthenticationFilesProvider(path);
            if (_steamCredentials == null || _steamCredentials.IsAnonymous)
                _steamCredentials = new SteamCredentials(Parameters.Username, Encryption.Instance.DecryptData(Parameters.Password), Parameters.ApiKey);

            SteamClient ??= new SteamClient(_steamCredentials, new AuthCodeProvider(), sentryFileProvider);

            if (!SteamClient.IsConnected || SteamClient.IsFaulted)
            {
                Parameters.Output += $"\nConnecting to Steam as {(_steamCredentials.IsAnonymous ? "anonymous" : _steamCredentials.Username)}";
                SteamClient.MaximumLogonAttempts = 5;
                try
                { await SteamClient.ConnectAsync(); }
                catch (Exception ex)
                {
                    Parameters.Output += $"\nFailed! Error: {ex.Message}";
                    SteamClient.Shutdown();
                    SteamClient.Dispose();
                    SteamClient = null;

                    if (ex.GetBaseException() is SteamLogonException { Result: SteamKit2.EResult.InvalidPassword })
                    {
                        Parameters.Output += "\nWarning: The logon may have failed due to expired sentry-data."
                                             + $"\nIf you are sure that the provided username and password are correct, consider deleting the .bin and .key file for the user \"{SteamClient?.Credentials.Username}\" in the sentries directory."
                                             + $"{path}";
                    }
                    IsLoggingIn = false;
                    return false;
                }
            }
            
            SteamContentClient = new SteamContentClient(SteamClient, Properties.Settings.Default.CliWorkers);
            Parameters.Output += "\nConnected !";
            IsLoggingIn = false;
            return SteamClient.IsConnected;
        }
        
        internal bool SteamReset()
        {
            Parameters.Output += "\nDisconnecting...";
            SteamClient.Shutdown();
            SteamClient.Dispose();
            SteamClient = null;
            Parameters.Output += "\nDisconnected.";
            return SteamClient == null;
        }

        private void SyncDeleteRemovedFiles(string targetDir, Manifest manifest)
        {
            Console.WriteLine("Checking for unnecessary files in target directory...");

            foreach (var localFilePath in Directory.GetFiles(targetDir, "*", SearchOption.AllDirectories))
            {
                var relativeLocalPath = Path.GetRelativePath(targetDir, localFilePath);

                if (manifest.Files.Any(x => string.Equals(x.FileName, relativeLocalPath, StringComparison.InvariantCultureIgnoreCase)))
                    continue;

                Console.WriteLine($"Deleting local file {relativeLocalPath}");
                File.Delete(localFilePath);
            }
        }


        private async Task Download(IDownloadHandler downloadHandler, string targetDir)
        {
            ulong downloadedSize = 0;
            downloadHandler.FileVerified          += (_, args) => Parameters.Output += $"{(args.RequiresDownload ? $"\nFile verified : {args.ManifestFile.FileName} ({Functions.ParseFileSize(args.ManifestFile.TotalSize)})" : "")}";
            downloadHandler.VerificationCompleted += (_, args) => Parameters.Output += $"\nVerification completed, {args.QueuedFiles.Count} files queued for download. ({args.QueuedFiles.Sum(f => (double)f.TotalSize)} bytes)";
            downloadHandler.FileDownloaded        += (_, args) =>
                                                     {
                                                         downloadedSize    += args.TotalSize;
                                                         Parameters.Output += $"\nProgress {downloadHandler.TotalProgress * 100:00.00}% ({Functions.ParseFileSize(downloadedSize)} / {Functions.ParseFileSize(downloadHandler.TotalFileSize)})";
                                                         Parameters.Progress = downloadHandler.TotalProgress * 100;
                                                     };
            downloadHandler.DownloadComplete      += (_, _) => Parameters.Output += "\nDownload completed";

            if (tokenSource.IsCancellationRequested)
                tokenSource = new CancellationTokenSource();
            
            Task downloadTask = downloadHandler.DownloadToFolderAsync(targetDir, tokenSource.Token);
            

            Parameters.Output += "\nOK.";

            DownloadTasks.Add(downloadTask);
            
            Parameters.Output += $"\nDownloading {downloadHandler.TotalFileCount} files with total size of {Functions.ParseFileSize(downloadHandler.TotalFileSize)}...";
            Parameters.Progress = 0;
            while (!downloadTask.IsCompleted && !downloadTask.IsCanceled && !tokenSource.Token.IsCancellationRequested)
            {

                var delayTask = Task.Delay(500, tokenSource.Token);
                await Task.WhenAny(delayTask, downloadTask);

                if (tokenSource.Token.IsCancellationRequested)
                    Parameters.Output += "\nTask cancellation requested";
                Parameters.Output += $"\nProgress {downloadHandler.TotalProgress * 100:00.00}%";
                Parameters.Progress = downloadHandler.TotalProgress * 100;
            }

            if (downloadTask.IsCanceled)
            {
                
                Parameters.Output += "\nTask Cancelled";
                Parameters.Progress = 0;
                await downloadHandler.DisposeAsync();
                DownloadTasks.Remove(downloadTask);
                return;
            }

            try
            { await downloadTask; }
            catch (TaskCanceledException)
            {
                Parameters.Output += "\nTask Cancelled";
                Parameters.Progress = 0;
                throw;
            }
            finally
            {
                DownloadTasks.Remove(downloadTask);
                await downloadHandler.DisposeAsync();
                downloadTask.Dispose();
            }
        }

        private async Task DownloadForMultiple(IDownloadHandler downloadHandler, string targetDir)
        {
            if (targetDir == null)
                return;
            
            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            tokenSource.Token.ThrowIfCancellationRequested();
            ulong downloadedSize = 0;
            downloadHandler.FileVerified          += (_, args) => Parameters.Output += $"{(args.RequiresDownload ? $"\n    File verified : {args.ManifestFile.FileName} ({Functions.ParseFileSize(args.ManifestFile.TotalSize)})" : "")}";
            downloadHandler.VerificationCompleted += (_, args) => Parameters.Output += $"\n    Verification completed, {args.QueuedFiles.Count} files queued for download. ({args.QueuedFiles.Sum(f => (double)f.TotalSize)} bytes)";
            downloadHandler.FileDownloaded        += (_, args) =>
                                                     {
                                                         downloadedSize    += args.TotalSize;
                                                         Parameters.Output += $"\n    Progress {downloadHandler.TotalProgress * 100:00.00}% ({Functions.ParseFileSize(downloadedSize)} / {Functions.ParseFileSize(downloadHandler.TotalFileSize)})";
                                                     };
            downloadHandler.DownloadComplete      += (_, _) => Parameters.Output += "\n    Download completed";

            Task downloadTask = downloadHandler.DownloadToFolderAsync(targetDir, tokenSource.Token);
            
            Parameters.Output += "\n    OK.";

            DownloadTasks.Add(downloadTask);
            
            Parameters.Output += $"\n    Downloading {downloadHandler.TotalFileCount} files with total size of {Functions.ParseFileSize(downloadHandler.TotalFileSize)}...";
            Parameters.Progress = 0;
            while (!downloadTask.IsCompleted && !downloadTask.IsCanceled && !tokenSource.IsCancellationRequested)
            {

                var delayTask = Task.Delay(500, tokenSource.Token);
                await Task.WhenAny(delayTask, downloadTask);

                if (tokenSource.IsCancellationRequested)
                    Parameters.Output += "\n    Task cancellation requested";
            }

            if (downloadTask.IsCanceled)
            {
                Parameters.Output += "\n    Task Cancelled";
                DownloadTasks.Remove(downloadTask);
                await downloadHandler.DisposeAsync();
                return;
            }

            try
            { await downloadTask; }
            catch (TaskCanceledException)
            {
                Parameters.Output += "\n    Task Cancelled";
                throw;
            }
            finally
            {
                DownloadTasks.Remove(downloadTask);
                await downloadHandler.DisposeAsync();
                downloadTask.Dispose();
            }
        }

        public async Task<string> SteamGuardInput()
        { return await DialogCoordinator.ShowInputAsync(this, "Steam Guard", "Please enter your 2FA code"); }

        public event PropertyChangedEventHandler PropertyChanged;

        private void RaisePropertyChanged(string property)
        {
            if (PropertyChanged == null) return;
            PropertyChanged(this, new PropertyChangedEventArgs(property));
        }
    }

    public static class UpdateState
    {
        public const int Success     = 0;
        public const int Error       = 1;
        public const int LoginFailed = 2;
        public const int Cancelled   = 3;
    }
}
