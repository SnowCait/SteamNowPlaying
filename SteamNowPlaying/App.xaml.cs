using Gameloop.Vdf;
using Microsoft.Win32;
using Nostr.Sdk;
using System.Diagnostics;

namespace SteamNowPlaying
{
    public partial class App : Application
    {
        private readonly FileSystemWatcher? watcher;

        public App()
        {
            InitializeComponent();

            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            var path = key?.GetValue("InstallPath") as string;
            Debug.WriteLine($"Steam Install Path: {path}");

            if (path == null)
            {
                path = @"C:\Program Files (x86)\Steam\steamapps";
            }
            else
            {
                path = Path.Combine(path, "steamapps");
            }
            var ext = ".acf";
            var filter = $"*{ext}";
            var files = Directory.GetFiles(path, filter);

            HashSet<string> exeList = new();
            foreach (var file in files)
            {
                Debug.WriteLine($"File: {file}");

                var text = File.ReadAllText(file);
                var appState = VdfConvert.Deserialize(text).Value;
                Debug.WriteLine($"AppState: {appState["name"]?.ToString()}");
                Debug.WriteLine($"Installed: {appState["installdir"]?.ToString()}");

                var installDir = Path.Combine(path, "common", appState["installdir"]?.ToString() ?? string.Empty);
                Debug.WriteLine($"Install Path: {installDir}");

                var exeFiles = Directory.GetFiles(installDir, "*.exe");
                foreach (var exe in exeFiles)
                {
                    if (exe.EndsWith("UnityCrashHandler64.exe"))
                    {
                        continue;
                    }
                    Debug.WriteLine($"Executable: {exe}");
                    exeList.Add(Path.GetFileName(exe));
                }
            }
            Debug.WriteLine($"Executable List: {string.Join("\n", exeList)}");

            watcher = new FileSystemWatcher(path)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                Filter = filter,
                EnableRaisingEvents = true
            };

            watcher.Created += (sender, e) => Debug.WriteLine($"File created: {e.FullPath}");
            watcher.Changed += (sender, e) => Debug.WriteLine($"File changed: {e.FullPath}");
            watcher.Deleted += (sender, e) => Debug.WriteLine($"File deleted: {e.FullPath}");
            watcher.Renamed += (sender, e) =>
            {
                if (!e.FullPath.EndsWith(ext))
                {
                    return;
                }
                Debug.WriteLine($"File renamed to {e.FullPath}");
            };
            watcher.Error += (sender, e) => Debug.WriteLine($"Watcher error: {e.GetException()}");

            HashSet<int> previousPids = new();
            Process? playingProcess = null;

            while (true)
            {
                var current = Process.GetProcesses();
                var currentPids = current.Select(p => p.Id).ToHashSet();

                var started = currentPids.Except(previousPids);
                var stopped = previousPids.Except(currentPids);

                foreach (var pid in started)
                {
                    var process = current.FirstOrDefault(p => p.Id == pid);
                    if (process == null)
                    {
                        continue;
                    }
                    Debug.WriteLine($"Process started: {pid} ({process.ProcessName}) {exeList.Contains($"{process.ProcessName}.exe")}");
                    if (exeList.Contains($"{process.ProcessName}.exe"))
                    {
                        Debug.WriteLine($"Play: {process.ProcessName}");
                        playingProcess = process;

                        Task.Run(async () =>
                        {
                            var note = EventBuilder.TextNote($"Now Playing: {process.ProcessName}");
                            var status = new EventBuilder(new Kind(30315), $"Now Playing: {process.ProcessName}").Tags([Tag.Identifier("general")]);
                            await Post([note, status]);
                        });
                    }
                }

                foreach (var pid in stopped)
                {
                    if (pid != playingProcess?.Id)
                    {
                        continue;
                    }
                    Debug.WriteLine($"Process stopped: {pid} ({playingProcess?.ProcessName})");
                    Debug.WriteLine($"End: {playingProcess?.ProcessName}");
                    playingProcess = null;

                    Task.Run(async () =>
                    {
                        var status = new EventBuilder(new Kind(30315), "").Tags([Tag.Identifier("general")]);
                        await Post([status]);
                    });
                }

                previousPids = currentPids;

                Thread.Sleep(1000);
            }
        }

        private async Task Post(EventBuilder[] builders)
        {
            var keys = Keys.Parse("<nsec>");
            var signer = NostrSigner.Keys(keys);
            var client = new Client(signer);
            await client.AddWriteRelay("wss://nos.lol/");
            await client.Connect();
            foreach (var builder in builders)
            {
                await client.SendEventBuilder(builder);
            }
            await client.Disconnect();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            return new Window(new AppShell());
        }
    }
}