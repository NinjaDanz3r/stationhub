using System;
using System.Collections.Generic;
using System.Linq;
using ReactiveUI;
using System.Reactive.Linq;
using System.IO;
using System.Reactive.Subjects;
using UnitystationLauncher.Infrastructure;
using Serilog;
using System.Threading;
using System.Reactive;

namespace UnitystationLauncher.Models
{
    public class InstallationManager : ReactiveObject
    {
        private readonly BehaviorSubject<IReadOnlyList<Installation>> installationsSubject;
        private bool autoRemove;
        public bool AutoRemove { get => autoRemove; set { autoRemove = value; if (autoRemove) TryAutoRemove(); } }
        public Action InstallListChange;
        public InstallationManager()
        {
            installationsSubject = new BehaviorSubject<IReadOnlyList<Installation>>(new Installation[0]);

            if (!Directory.Exists(Config.InstallationsPath))
            {
                throw new IOException("InstallationPath does not exist");
            }

            var fileWatcher = new FileSystemWatcher(Config.InstallationsPath) { EnableRaisingEvents = true, IncludeSubdirectories = true };
            Observable.FromEventPattern<FileSystemEventHandler, FileSystemEventArgs>(
                    h => fileWatcher.Changed += h,
                    h => fileWatcher.Changed -= h)
                .Do(o => Log.Debug("File refresh: {FileName}", o.EventArgs.Name))
                .Select(_ => Unit.Default)
                .Merge(Observable.Return(Unit.Default))
                .ObserveOn(SynchronizationContext.Current)
                .ThrottleSubsequent(TimeSpan.FromMilliseconds(200))
                .Select(_ =>
                    Directory.EnumerateDirectories(Config.InstallationsPath)
                        .Select(dir => new Installation(dir))
                        .OrderByDescending(x => x.ForkName + x.BuildVersion)
                        .ToList())
                .Subscribe(installationsSubject);

            installationsSubject.Subscribe(ListHasChanged);
        }

        void ListHasChanged(IReadOnlyList<Installation> list)
        {
            InstallListChange?.Invoke();
        }

        public void TryAutoRemove()
        {
            if (!autoRemove) return;

            var key = "";
            foreach(Installation i in installationsSubject.Value)
            {
                if (!key.Equals(i.ForkName))
                {
                    key = i.ForkName;
                    continue;
                }
                try
                {
                   Directory.Delete(i.InstallationPath, true);
                }
                catch (Exception e)
                {
                    Log.Error(e, "An exception occurred during the deletion of an installation");
                }
            }
        }

        public IObservable<IReadOnlyList<Installation>> Installations => installationsSubject;
    }
}