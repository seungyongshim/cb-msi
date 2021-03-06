﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using BeatPackageCompiler.Properties;
using ElastiBuild.Extensions;
using Elastic.Installer;
using WixSharp;
using WixSharp.CommonTasks;

namespace Elastic.PackageCompiler.Beats
{
    public class BeatPackageCompiler
    {
        static void Main(string[] args)
        {
            var opts = CmdLineOptions.Parse(args);

            Compiler.AutoGeneration.LegacyDefaultIdAlgorithm = true;

            var config = BuildConfiguration.Read(
                Path.Combine(opts.ConfigDir, MagicStrings.Files.ConfigYaml));

            Directory.CreateDirectory(opts.PackageOutDir);

            var ap = new ArtifactPackage()
            {
                TargetName = "collectbeat",
                CanonicalTargetName = "collectbeat",
                Architecture = MagicStrings.Arch.x86_64,
                Version = Environment.GetEnvironmentVariable("GITHUB_VERSION").Trim('v'),
            };

            Console.WriteLine(ap.ToString());

            var pc = config.GetProductConfig(ap.TargetName);

            var companyName = "Mirero";
            var productSetName = MagicStrings.Beats.Name;
            var displayName = companyName + " " + MagicStrings.Beats.Name + " " + ap.TargetName;
            var exeName = "nssm.exe";

            // Generate UUID v5 from product properties.
            // This UUID *must* be stable and unique between Beats.
            var upgradeCode = Uuid5.FromString(ap.CanonicalTargetName);

            var project = new Project(displayName)
            {
                InstallerVersion = 500,

                GUID = upgradeCode,

                Name = $"{displayName} {ap.SemVer} ({ap.Architecture})",

                Description = pc.Description,

                OutFileName = Path.Combine(opts.PackageOutDir, opts.ShortPackageName),
                Version = new Version(ap.Version),

                Platform = ap.Is32Bit ? Platform.x86 : Platform.x64,

                InstallScope = InstallScope.perMachine,

                UI = WUI.WixUI_Minimal,

                // TODO: Custom images?
                BannerImage = Path.Combine(opts.ResDir, MagicStrings.Files.TopBannerBmp),
                BackgroundImage = Path.Combine(opts.ResDir, MagicStrings.Files.LeftBannerBmp),

                MajorUpgrade = new MajorUpgrade
                {
                    AllowDowngrades = false,
                    AllowSameVersionUpgrades = false,
                    DowngradeErrorMessage = MagicStrings.Errors.NewerVersionInstalled,
                },
            };

            project.Include(WixExtension.UI);
            project.Include(WixExtension.Util);

            project.ControlPanelInfo = new ProductInfo
            {
                Contact = companyName,
                Manufacturer = companyName,
                UrlInfoAbout = "https://www.mirero.co.kr",

                Comments = pc.Description + ". " + MagicStrings.Beats.Description,

                ProductIcon = Path.Combine(
                    opts.ResDir,
                    Path.GetFileNameWithoutExtension(exeName) + MagicStrings.Ext.DotIco),

                NoRepair = true,
            };


            var beatConfigPath = "[CommonAppDataFolder]" + Path.Combine(companyName, productSetName, ap.CanonicalTargetName);
            var beatSettingsPath = Path.Combine(beatConfigPath, "config");
            var beatDataPath = Path.Combine(beatConfigPath, "data");
            var beatLogsPath = Path.Combine(beatConfigPath, "logs");

            var textInfo = new CultureInfo("en-US", false).TextInfo;
            var serviceDisplayName = $"{companyName} {textInfo.ToTitleCase(ap.TargetName)} {ap.SemVer}";

            WixSharp.File service = null;
            if (pc.IsWindowsService)
            {
                service = new WixSharp.File(Path.Combine(opts.PackageInDir, exeName));

                // TODO: CNDL1150 : ServiceConfig functionality is documented in the Windows Installer SDK to 
                //                  "not [work] as expected." Consider replacing ServiceConfig with the 
                //                  WixUtilExtension ServiceConfig element.

                service.ServiceInstaller = new ServiceInstaller
                {
                    Interactive = false,

                    Name = ap.CanonicalTargetName,
                    DisplayName = serviceDisplayName,
                    Description = pc.Description,

                    DependsOn = new[]
                    {
                        new ServiceDependency(MagicStrings.Services.Tcpip),
                        new ServiceDependency(MagicStrings.Services.Dnscache),
                    },

                    DelayedAutoStart = false,
                    Start = SvcStartType.auto,

                    StartOn = SvcEvent.Install,
                    StopOn = SvcEvent.InstallUninstall_Wait,
                    RemoveOn = SvcEvent.InstallUninstall_Wait,
                };
            }

            project.RegValues = new RegValue[]
            {
                new RegValue(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{ap.CanonicalTargetName}\Parameters", "Application", 
                  $@"[INSTALLDIR]{ap.Version}\{ap.CanonicalTargetName}\bin\logstash.bat"),
                new RegValue(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{ap.CanonicalTargetName}\Parameters", "AppDirectory", 
                  $@"[INSTALLDIR]{ap.Version}\{ap.CanonicalTargetName}\bin"),
                new RegValue(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{ap.CanonicalTargetName}\Parameters", "AppParameters", 
                  $@"--path.settings {beatSettingsPath.Quote()} --path.data {beatDataPath.Quote()}  --path.logs {beatLogsPath.Quote()}"),
                new RegValue(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{ap.CanonicalTargetName}\Parameters", "AppStderr", 
                  $@"""{beatConfigPath}\service_stderr.log"""),
            };
            

            var packageContents = new List<WixEntity>
            {
                new DirFiles(Path.Combine(opts.PackageInDir, MagicStrings.Files.All), path =>
                {
                    var itm = path.ToLower();

                    bool exclude = 

                        // configuration will go into mutable location
                        itm.EndsWith(MagicStrings.Ext.DotYml, StringComparison.OrdinalIgnoreCase) ||

                        // we install/remove service ourselves
                        itm.EndsWith(MagicStrings.Ext.DotPs1, StringComparison.OrdinalIgnoreCase) ||

                        itm.EndsWith(MagicStrings.Ext.DotTxt, StringComparison.OrdinalIgnoreCase) ||

                        itm.EndsWith(MagicStrings.Ext.DotMd, StringComparison.OrdinalIgnoreCase) ||

                        // .exe must be excluded for service configuration to work
                        (pc.IsWindowsService && itm.EndsWith(exeName, StringComparison.OrdinalIgnoreCase))
                    ;

                    // this is an "include" filter
                    return ! exclude;
                })
            };

            packageContents.AddRange(
                new DirectoryInfo(opts.PackageInDir)
                    .GetDirectories()
                    .Select(dir => dir.Name)
                    .Except(pc.MutableDirs)
                    .Select(dirName =>
                        new Dir(
                            dirName,
                            new Files(Path.Combine(
                                opts.PackageInDir,
                                dirName,
                                MagicStrings.Files.All)))));

            packageContents.Add(pc.IsWindowsService ? service : null);
            project.AddProperty(new Property("WIXUI_EXITDIALOGOPTIONALTEXT",
                $"NOTE: {serviceDisplayName} Windows service.\n"));

            var dataContents = new List<WixEntity>();
            var extraDir = Path.Combine(opts.ExtraDir, ap.TargetName);

            dataContents.AddRange(
                new DirectoryInfo(extraDir)
                    .GetFiles(MagicStrings.Files.AllDotYml, SearchOption.TopDirectoryOnly)
                    .Select(fi =>
                    {
                        var wf = new WixSharp.File(fi.FullName);
                        return wf;
                    }));

            dataContents.AddRange(
                new DirectoryInfo(extraDir)
                    .GetDirectories()
                    .Select(dir => dir.Name)
                    .Select(dirName =>
                        new Dir(
                            dirName,
                            new Files(Path.Combine(
                                extraDir,
                                dirName,
                                MagicStrings.Files.All)))));

            // Drop CLI shim on disk
            var cliShimScriptPath = Path.Combine(
                opts.PackageOutDir,
                MagicStrings.Files.ProductCliShim(ap.CanonicalTargetName));

            System.IO.File.WriteAllText(cliShimScriptPath, Resources.GenericCliShim);

            var beatsInstallPath =
                $"[ProgramFiles{(ap.Is64Bit ? "64" : string.Empty)}Folder]" +
                Path.Combine(companyName, productSetName);


            // Ignore Firewall
            foreach (var file in project.AllFiles)
            {
                switch (file.Id)
                {
                    case "java.exe":
                        Console.WriteLine(@"Ignore Firewall: java.exe");
                        file.FirewallExceptions = new[]
                        {
                            new FirewallException
                            {
                                Name = "BLUE CATS Collectbeat",
                                Scope = FirewallExceptionScope.any,
                                Profile = FirewallExceptionProfile.all
                            }
                        };
                        break;
                    default:
                        break;
                }
            }

            project.Dirs = new[]
            {
                // Binaries
                new InstallDir(
                     // Wix# directory parsing needs forward slash
                    beatsInstallPath.Replace("Folder]", "Folder]\\"),
                    new Dir(
                        ap.Version,
                        new Dir(ap.CanonicalTargetName, packageContents.ToArray()),
                        new WixSharp.File(cliShimScriptPath))),

                // Configration and logs
                new Dir("[CommonAppDataFolder]",
                    new Dir(companyName,
                        new Dir(productSetName,
                            new Dir(ap.CanonicalTargetName, dataContents.ToArray())
                            , new DirPermission("Users", "[MachineName]", GenericPermission.All)
                            )))
            };
            
            

            // CLI Shim path
            project.Add(new EnvironmentVariable("PATH", Path.Combine(beatsInstallPath, ap.Version))
            {
                Part = EnvVarPart.last
            });

            // We hard-link Wix Toolset to a known location
            Compiler.WixLocation = Path.Combine(opts.BinDir, "WixToolset", "bin");

#if !DEBUG
            if (opts.KeepTempFiles)
#endif
            {
                Compiler.PreserveTempFiles = true;
            }

            if (opts.Verbose)
            {
                Compiler.CandleOptions += " -v";
                Compiler.LightOptions += " -v";
            }

            project.ResolveWildCards();

            if (opts.WxsOnly)
                project.BuildWxs();
            else if (opts.CmdOnly)
                Compiler.BuildMsiCmd(project, Path.Combine(opts.SrcDir, opts.PackageName) + ".cmd");
            else
                Compiler.BuildMsi(project);
        }
    }
}
