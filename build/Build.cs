// Copyright 2018 Maintainers of NUKE.
// Distributed under the MIT License.
// https://github.com/nuke-build/nuke/blob/master/LICENSE

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nuke.Common;
using Nuke.Common.Git;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Tools.InspectCode;
using Nuke.Common.Tools.Slack;
using Nuke.Platform;
using Nuke.Platform.Extensions;
using Nuke.Platform.IO;
using static Nuke.Common.ChangeLog.ChangelogTasks;
using static Nuke.Common.Gitter.GitterTasks;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;
using static Nuke.Common.Tools.InspectCode.InspectCodeTasks;
using static Nuke.Common.Tools.Slack.SlackTasks;
using static Nuke.Platform.ControlFlow;
using static Nuke.Platform.EnvironmentInfo;
using static Nuke.Platform.IO.FileSystemTasks;
using static Nuke.Platform.IO.PathConstruction;

// ReSharper disable HeapView.DelegateAllocation

partial class Build : NukeBuild
{
    public static int Main() => Execute<Build>(x => x.Pack);
    
    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Parameter("ApiKey for the specified source.")] readonly string ApiKey;
    [Parameter] readonly string Source = "https://api.nuget.org/v3/index.json";
    [Parameter] readonly string SymbolSource = "https://nuget.smbsrc.net/";

    [Parameter("Gitter authtoken.")] readonly string GitterAuthToken;
    [Parameter("Slack webhook.")] readonly string SlackWebhook;

    [Solution] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;

    AbsolutePath OutputDirectory => RootDirectory / "output";
    AbsolutePath SourceDirectory => RootDirectory / "src";

    readonly string MasterBranch = "master";
    readonly string DevelopBranch = "develop";
    readonly string ReleaseBranchPrefix = "release";
    readonly string HotfixBranchPrefix = "hotfix";

    Target Clean => _ => _
        .Executes(() =>
        {
            DeleteDirectories(GlobDirectories(SourceDirectory, "*/bin", "*/obj"));
            EnsureCleanDirectory(OutputDirectory);
        });

    Target Restore => _ => _
        .DependsOn(Clean)
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Project CommonProject => Solution.GetProject("Nuke.Common").NotNull();
    Project GlobalToolProject => Solution.GetProject("Nuke.GlobalTool").NotNull();
    Project MSBuildTasksProject => Solution.GetProject("Nuke.MSBuildTasks").NotNull();

    Target Compile => _ => _
        .DependsOn(Restore)
        .Requires(() => IsUnix || GitVersion != null)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.GetNormalizedAssemblyVersion())
                .SetFileVersion(GitVersion.GetNormalizedFileVersion())
                .SetInformationalVersion(GitVersion.InformationalVersion));

            var publishSettings = new DotNetPublishSettings()
                .EnableNoRestore()
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.GetNormalizedAssemblyVersion())
                .SetFileVersion(GitVersion.GetNormalizedFileVersion())
                .SetInformationalVersion(GitVersion.InformationalVersion);

            DotNetPublish(s => publishSettings
                .SetProject(GlobalToolProject));

            DotNetPublish(s => publishSettings
                .SetProject(MSBuildTasksProject)
                .SetFramework("netstandard2.0"));
            DotNetPublish(s => publishSettings
                .SetProject(MSBuildTasksProject)
                .SetFramework("net461"));
        });

    string ChangelogFile => RootDirectory / "CHANGELOG.md";

    IEnumerable<string> ChangelogSectionNotes => ExtractChangelogSectionNotes(ChangelogFile);

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(Solution)
                .EnableNoBuild()
                .SetConfiguration(Configuration)
                .EnableIncludeSymbols()
                .SetSymbolPackageFormat(DotNetSymbolPackageFormat.snupkg)
                .SetOutputDirectory(OutputDirectory)
                .SetVersion(GitVersion.NuGetVersionV2)
                .SetPackageReleaseNotes(GetNuGetReleaseNotes(ChangelogFile, GitRepository)));
        });

    Target Install => _ => _
        .DependsOn(Pack)
        .Executes(() =>
        {
            SuppressErrors(() => DotNet($"tool uninstall -g {GlobalToolProject.Name}"));
            DotNet($"tool install -g {GlobalToolProject.Name} --add-source {OutputDirectory} --version {GitVersion.NuGetVersionV2}");
        });

    Target Test => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            Solution.GetProjects("*.Tests")
                .ForEach(x => DotNetTest(s => s
                    .SetProjectFile(x)
                    .SetConfiguration(Configuration)
                    .EnableNoBuild()
                    .SetLogger("trx")
                    .SetResultsDirectory(OutputDirectory)));
        });

    Target Analysis => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            InspectCode(s => s
                .SetTargetPath(Solution)
                .SetOutput(OutputDirectory / "inspectCode.xml")
                .AddExtensions(
                    "EtherealCode.ReSpeller",
                    "PowerToys.CyclomaticComplexity",
                    "ReSharper.ImplicitNullability",
                    "ReSharper.SerializationInspections",
                    "ReSharper.XmlDocInspections"));
        });

    Target Publish => _ => _
        .DependsOn(Test, Pack)
        .Requires(() => ApiKey, () => SlackWebhook, () => GitterAuthToken)
        .Requires(() => GitHasCleanWorkingCopy())
        .Requires(() => Configuration.Equals(Configuration.Release))
        .Requires(() => GitRepository.Branch.EqualsOrdinalIgnoreCase(MasterBranch) ||
                        GitRepository.Branch.EqualsOrdinalIgnoreCase(DevelopBranch) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(ReleaseBranchPrefix) ||
                        GitRepository.Branch.StartsWithOrdinalIgnoreCase(HotfixBranchPrefix))
        .Executes(() =>
        {
            GlobFiles(OutputDirectory, "*.nupkg").NotEmpty()
                .Where(x => !x.EndsWith(".symbols.nupkg"))
                .ForEach(x => DotNetNuGetPush(s => s
                    .SetTargetPath(x)
                    .SetSource(Source)
                    .SetSymbolSource(SymbolSource)
                    .SetApiKey(ApiKey)));

            if (GitRepository.Branch.EqualsOrdinalIgnoreCase(MasterBranch))
            {
                SendSlackMessage(m => m
                        .SetText(new StringBuilder()
                            .AppendLine($"<!here> :mega::shipit: *NUKE {GitVersion.SemVer} IS OUT!!!*")
                            .AppendLine()
                            .AppendLine(ChangelogSectionNotes.Select(x => x.Replace("- ", "• ")).JoinNewLine()).ToString()),
                    SlackWebhook);

                SendGitterMessage(new StringBuilder()
                        .AppendLine($"@/all :mega::shipit: **NUKE {GitVersion.SemVer} IS OUT!!!**")
                        .AppendLine()
                        .AppendLine(ChangelogSectionNotes.Select(x => x.Replace("- ", "* ")).JoinNewLine()).ToString(),
                    "593f3dadd73408ce4f66db89",
                    GitterAuthToken);
            }
        });
}
