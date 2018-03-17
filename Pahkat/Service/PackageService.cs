﻿using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Pahkat.Util;
using Microsoft.Win32;
using System.Linq;
using System.Net;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using Pahkat.Models;
using System.Reactive.Disposables;
using System.Windows.Media.Animation;
using Pahkat.Extensions;

namespace Pahkat.Service
{
    public enum PackageStatus
    {
        NotInstalled,
        UpToDate,
        RequiresUpdate,
        VersionSkipped,
        ErrorNoInstaller,
        ErrorParsingVersion
    }

    public static class PackageInstallStatusExtensions
    {
        public static string Description(this PackageStatus status)
        {
            switch (status)
            {
                case PackageStatus.ErrorNoInstaller:
                    return Strings.ErrorNoInstaller;
                case PackageStatus.ErrorParsingVersion:
                    return Strings.ErrorInvalidVersion;
                case PackageStatus.RequiresUpdate:
                    return Strings.UpdateAvailable;
                case PackageStatus.NotInstalled:
                    return Strings.NotInstalled;
                case PackageStatus.UpToDate:
                    return Strings.Installed;
                case PackageStatus.VersionSkipped:
                    return Strings.VersionSkipped;
            }

            return null;
        }
    }

    public struct PackageProgress
    {
        public Package Package;
        public DownloadProgressChangedEventHandler Progress;
    }

    public struct PackageInstallInfo
    {
        public Package Package;
        public string Path;
    }
    
    public class PackageUninstallInfo
    {
        public Package Package;
        public string Path;
        public string Args;
    }

    public interface IPackageService
    {
        PackageStatus InstallStatus(Package package);
        PackageAction DefaultPackageAction(Package package);
        IObservable<PackageInstallInfo> Download(PackageProgress[] packages, int maxConcurrent, CancellationToken cancelToken);
        PackageUninstallInfo UninstallInfo(Package package);
        void SkipVersion(Package package);
        bool RequiresUpdate(Package package);
        bool IsUpToDate(Package package);
        bool IsError(Package package);
        bool IsUninstallable(Package package);
        bool IsInstallable(Package package);
        bool IsValidAction(PackageActionInfo packageActionInfo);
        bool IsValidAction(Package package, PackageAction action);
    }
    
    public class PackageService : IPackageService
    {
        public static class Keys
        {
            public const string UninstallPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
            public const string DisplayVersion = "DisplayVersion";
            public const string SkipVersion = "SkipVersion";
            public const string QuietUninstallString = "QuietUninstallString";
            public const string UninstallString = "UninstallString";
        }
        
        private readonly IWindowsRegistry _registry;
        
        public PackageService(IWindowsRegistry registry)
        {
            _registry = registry;
        }
        
        public PackageAction DefaultPackageAction(Package package)
        {
            return IsUpToDate(package)
                ? PackageAction.Uninstall
                : PackageAction.Install;
        }

        private PackageStatus CompareVersion<T>(Func<string, T> creator, string packageVersion, string registryVersion) where T: IComparable<T>
        {
            var ver = creator(packageVersion);
            if (ver == null)
            {
                return PackageStatus.ErrorParsingVersion;
            }
            
            var parsedDispVer = creator(registryVersion);
            if (parsedDispVer == null)
            {
                return PackageStatus.ErrorParsingVersion;
            }

            if (ver.CompareTo(parsedDispVer) > 0)
            {
                return PackageStatus.RequiresUpdate;
            }
            else
            {
                return PackageStatus.UpToDate;
            }
        }

        private IObservable<string> DownloadFileTaskAsync(Uri uri, string dest, DownloadProgressChangedEventHandler onProgress, CancellationToken cancelToken)
        {
            using (var client = new WebClient { Encoding = Encoding.UTF8 })
            {
                if (onProgress != null)
                {
                    client.DownloadProgressChanged += onProgress;
                }

                cancelToken.Register(() => client.CancelAsync());

                client.DownloadFileTaskAsync(uri, dest);

                return Observable.Create<string>(observer =>
                {
                    // TODO: turn this into reactive extension... extension
                    var watcher = Observable.FromEventPattern<AsyncCompletedEventHandler, AsyncCompletedEventArgs>(
                        x => client.DownloadFileCompleted += x,
                        x => client.DownloadFileCompleted -= x)
                    .Select(x => x.EventArgs)
                    .Subscribe(args =>
                    {
                        if (args.Error != null)
                        {
                            observer.OnError(args.Error);
                        }
                        else
                        {
                            observer.OnNext(dest);
                        }

                        observer.OnCompleted();
                    });

                    return new CompositeDisposable((IDisposable)observer, watcher);
                });
            }
        }

        private IObservable<PackageInstallInfo> Download(PackageProgress pd, CancellationToken cancelToken)
        {
            var inst = pd.Package.WindowsInstaller;
            
            // Get file ending from URL
            var ext = Path.GetExtension(inst.Url.AbsoluteUri);
            
            // Make name package name + version
            var fileName = $"{pd.Package.Id}-{pd.Package.Version}{ext}";
            var path = Path.Combine(Path.GetTempPath(), fileName);

            return DownloadFileTaskAsync(inst.Url, path, pd.Progress, cancelToken)
                .Select(x => new PackageInstallInfo
                {
                    Package = pd.Package,
                    Path = x
                });
        }

        public bool IsValidAction(PackageActionInfo packageActionInfo)
        {
            return IsValidAction(packageActionInfo.Package, packageActionInfo.Action);
        }

        public bool IsValidAction(Package package, PackageAction action)
        {
            switch (action)
            {
                case PackageAction.Install:
                    return IsInstallable(package);
                case PackageAction.Uninstall:
                    return IsUninstallable(package);
            }
            
            throw new ArgumentException("PackageAction switch exhausted unexpectedly.");
        }

        public bool RequiresUpdate(Package package)
        {
            return InstallStatus(package) == PackageStatus.RequiresUpdate;
        }

        public bool IsUpToDate(Package package)
        {
            return InstallStatus(package) == PackageStatus.UpToDate;
        }

        public bool IsError(Package package)
        {
            switch (InstallStatus(package))
            {
                case PackageStatus.ErrorNoInstaller:
                case PackageStatus.ErrorParsingVersion:
                    return true;
                default:
                    return false;
            }
        }

        public bool IsUninstallable(Package package)
        {
            switch (InstallStatus(package))
            {
                case PackageStatus.UpToDate:
                case PackageStatus.RequiresUpdate:
                case PackageStatus.VersionSkipped:
                    return true;
                default:
                    return false;
            }
        }
        
        public bool IsInstallable(Package package)
        {
            switch (InstallStatus(package))
            {
                case PackageStatus.NotInstalled:
                case PackageStatus.RequiresUpdate:
                case PackageStatus.VersionSkipped:
                    return true;
                default:
                    return false;
            }
        }
        
        /// <summary>
        /// Checks the registry for the installed package. Uses the "DisplayVersion" value and parses that using
        /// either the Assembly versioning technique or the Semantic versioning technique. Attempts Assembly first
        /// as this tends to be more common on Windows than other platforms.
        /// </summary>
        /// <param name="package"></param>
        /// <returns>The package install status</returns>
        public PackageStatus InstallStatus(Package package)
        {
            if (package.Installer == null)
            {
                return PackageStatus.ErrorNoInstaller;
            }

            var installer = package.WindowsInstaller;
            var hklm = _registry.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            var path = $@"{Keys.UninstallPath}\{installer.ProductCode}";
            var instKey = hklm.OpenSubKey(path);

            if (instKey == null)
            {
                return PackageStatus.NotInstalled;
            }
            
            var displayVersion = instKey.Get(Keys.DisplayVersion, "");
            if (displayVersion == "")
            {
                return PackageStatus.ErrorParsingVersion;
            }

            var comp = CompareVersion(AssemblyVersion.Create, package.Version, displayVersion);
            if (comp != PackageStatus.ErrorParsingVersion)
            {
                return comp;
            }

            if (SkippedVersion(package) == package.Version)
            {
                return PackageStatus.VersionSkipped;
            }
                
            comp = CompareVersion(SemanticVersion.Create, package.Version, displayVersion);
            if (comp != PackageStatus.ErrorParsingVersion)
            {
                return comp;
            }

            return PackageStatus.ErrorParsingVersion;
        }

        public PackageUninstallInfo UninstallInfo(Package package)
        {
            var installer = package.WindowsInstaller;
            var hklm = _registry.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            var path = $@"{Keys.UninstallPath}\{installer.ProductCode}";
            var instKey = hklm.OpenSubKey(path);

            var uninstString = instKey.Get<string>(Keys.QuietUninstallString) ??
                               instKey.Get<string>(Keys.UninstallString);
            if (uninstString != null)
            {
                var chunks = uninstString.ParseFileNameAndArgs();
                var args = package.WindowsInstaller.UninstallArgs ?? string.Join(" ", chunks.Item2);
                
                return new PackageUninstallInfo
                {
                    Package = package,
                    Path = chunks.Item1,
                    Args = args
                };
            }

            return null;
        }

        public void SkipVersion(Package package)
        {
            // TODO: implement
            //var hklm = _registry.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            //var path = $@"{AppConfigState.Keys.SubkeyId}\{package.WindowsInstaller.ProductCode}";
            //var instKey = hklm.CreateSubKey(path);
            
            //instKey.Set(Keys.SkipVersion, package.Version, RegistryValueKind.String);
        }

        private string SkippedVersion(Package package)
        {
            // TODO: implement
            //var hklm = _registry.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            //var path = $@"{AppConfigState.Keys.SubkeyId}\{package.WindowsInstaller.ProductCode}";
            //var instKey = hklm.OpenSubKey(path);

            //return instKey?.Get<string>(Keys.SkipVersion);
            return null;
        }

        /// <summary>
        /// Downloads the supplied packages. Each object should contain a unique progress handler so the UI can be
        /// updated effectively.
        /// </summary>
        /// <param name="packages"></param>
        /// <param name="maxConcurrent"></param>
        /// <param name="cancelToken"></param>
        /// <returns></returns>
        public IObservable<PackageInstallInfo> Download(PackageProgress[] packages, int maxConcurrent, CancellationToken cancelToken)
        {
            return packages
                .Select(pkg => Download(pkg, cancelToken))
                .Merge(maxConcurrent);
        }
    }
}