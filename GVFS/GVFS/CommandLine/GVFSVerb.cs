﻿using CommandLine;
using GVFS.Common;
using GVFS.Common.Git;
using GVFS.Common.Tracing;
using Microsoft.Diagnostics.Tracing;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;

namespace GVFS.CommandLine
{
    public abstract class GVFSVerb
    {
        public GVFSVerb()
        {
            this.Output = Console.Out;
            this.ReturnCode = ReturnCode.Success;

            this.InitializeDefaultParameterValues();
        }

        public abstract string EnlistmentRootPath { get; set; }

        public TextWriter Output { get; set; }

        public ReturnCode ReturnCode { get; private set; }

        protected abstract string VerbName { get; }

        public static bool TrySetGitConfigSettings(GitProcess git)
        {
            Dictionary<string, string> expectedConfigSettings = new Dictionary<string, string>
            {
                { "core.autocrlf", "false" },
                { "core.fscache", "true" },
                { "core.gvfs", "true" },
                { "core.preloadIndex", "true" },
                { "core.safecrlf", "false" },
                { "core.sparseCheckout", "true" },
                { "core.untrackedCache", "false" },
                { GitConfigSetting.VirtualizeObjectsGitConfigName, "true" },
                { "credential.validate", "false" },
                { "diff.autoRefreshIndex", "false" },
                { "gc.auto", "0" },
                { "gui.gcwarning", "false" },
                { "index.version", "4" },
                { "merge.stat", "false" },
                { "receive.autogc", "false" },
            };

            Dictionary<string, GitConfigSetting> actualConfigSettings;
            if (!git.TryGetAllLocalConfig(out actualConfigSettings))
            {
                return false;
            }

            foreach (string key in expectedConfigSettings.Keys)
            {
                GitConfigSetting actualSetting;
                if (!actualConfigSettings.TryGetValue(key, out actualSetting) ||
                    !actualSetting.HasValue(expectedConfigSettings[key]))
                {
                    GitProcess.Result setConfigResult = git.SetInLocalConfig(key, expectedConfigSettings[key]);
                    if (setConfigResult.HasErrors)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public static ReturnCode Execute<TVerb>(
            string enlistmentRootPath,
            Action<TVerb> configureVerb = null)
            where TVerb : GVFSVerb, new()
        {
            TVerb verb = new TVerb();
            verb.EnlistmentRootPath = enlistmentRootPath;
            if (configureVerb != null)
            {
                configureVerb(verb);
            }

            try
            {
                verb.Execute();
            }
            catch (VerbAbortedException)
            {
            }

            return verb.ReturnCode;
        }

        public abstract void Execute();

        public virtual void InitializeDefaultParameterValues()
        {
        }

        protected bool ShowStatusWhileRunning(Func<bool> action, string message, bool suppressGvfsLogMessage = false)
        {
            return ConsoleHelper.ShowStatusWhileRunning(
                action,
                message,
                this.Output,
                showSpinner: this.Output == Console.Out && !ConsoleHelper.IsConsoleOutputRedirectedToFile(),
                suppressGvfsLogMessage: suppressGvfsLogMessage);
        }

        protected void ReportErrorAndExit(ReturnCode exitCode, string error, params object[] args)
        {
            if (error != null)
            {
                this.Output.WriteLine(error, args);
            }

            this.ReturnCode = exitCode;
            throw new VerbAbortedException(this);
        }

        protected void ReportErrorAndExit(string error, params object[] args)
        {
            this.ReportErrorAndExit(ReturnCode.GenericError, error, args);
        }

        protected void CheckElevated()
        {
            if (!ProcessHelper.IsAdminElevated())
            {
                this.ReportErrorAndExit("{0} must be run with elevated privileges", this.VerbName);
            }
        }

        protected void CheckGVFltRunning()
        {
            bool gvfltServiceRunning = false;
            try
            {
                ServiceController controller = new ServiceController("gvflt");
                gvfltServiceRunning = controller.Status.Equals(ServiceControllerStatus.Running);
            }
            catch (InvalidOperationException)
            {
                this.ReportErrorAndExit("Error: GVFlt Service was not found. To resolve, re-install GVFS");
            }

            if (!gvfltServiceRunning)
            {
                this.ReportErrorAndExit("Error: GVFlt Service is not running. To resolve, run \"sc start gvflt\" from an admin command prompt");
            }
        }

        protected void CheckGitVersion(Enlistment enlistment)
        {
            GitProcess.Result versionResult = GitProcess.Version(enlistment);
            if (versionResult.HasErrors)
            {
                this.ReportErrorAndExit("Error: Unable to retrieve the git version");
            }

            GitVersion gitVersion;
            string version = versionResult.Output;
            if (version.StartsWith("git version "))
            {
                version = version.Substring(12);
            }

            if (!GitVersion.TryParse(version, out gitVersion))
            {
                this.ReportErrorAndExit("Error: Unable to parse the git version. {0}", version);
            }

            if (gitVersion.Platform != GVFSConstants.MinimumGitVersion.Platform)
            {
                this.ReportErrorAndExit("Error: Invalid version of git {0}.  Must use gvfs version.", version);
            }

            if (gitVersion.IsLessThan(GVFSConstants.MinimumGitVersion))
            {
                this.ReportErrorAndExit(
                    "Error: Installed git version {0} is less than the minimum version of {1}.",
                    gitVersion,
                    GVFSConstants.MinimumGitVersion);
            }
        }

        protected void CheckGVFSHooksVersion(Enlistment enlistment, string hooksPath)
        {
            if (hooksPath == null)
            {
                hooksPath = ProcessHelper.WhereDirectory(GVFSConstants.GVFSHooksExecutableName);
                if (hooksPath == null)
                {
                    this.ReportErrorAndExit("Could not find " + GVFSConstants.GVFSHooksExecutableName);
                }
            }

            FileVersionInfo myFileVersionInfo = FileVersionInfo.GetVersionInfo(hooksPath + "\\" + GVFSConstants.GVFSHooksExecutableName);
            string gvfsVersion = ProcessHelper.GetCurrentProcessVersion();
            if (myFileVersionInfo.ProductVersion != gvfsVersion)
            {
                this.ReportErrorAndExit("GVFS.Hooks version ({0}) does not match GVFS version ({1}).", myFileVersionInfo.ProductVersion, gvfsVersion);
            }
        }

        protected void CheckAntiVirusExclusion(GVFSEnlistment enlistment)
        {
            bool isExcluded;
            string getError;
            if (AntiVirusExclusions.TryGetIsPathExcluded(enlistment.EnlistmentRoot, out isExcluded, out getError))
            {
                if (!isExcluded)
                {
                    string addError;
                    if (!ProcessHelper.IsAdminElevated())
                    {
                        addError = "Need elevated privileges to add exclusion.";
                    }
                    else if (AntiVirusExclusions.AddAntiVirusExclusion(enlistment.EnlistmentRoot, out addError))
                    {
                        addError = string.Empty;
                        AntiVirusExclusions.TryGetIsPathExcluded(enlistment.EnlistmentRoot, out isExcluded, out getError);
                    }

                    if (!isExcluded)
                    {
                        this.Output.WriteLine();
                        this.Output.WriteLine("WARNING: This repo is not excluded from antivirus and we were unable to add an exclusion for it.");

                        if (!string.IsNullOrEmpty(addError))
                        {
                            this.Output.WriteLine("Unable to add exclusion: " + addError);
                        }

                        this.Output.WriteLine("Please check to make sure that '{0}' is excluded.", enlistment.EnlistmentRoot);
                        this.Output.WriteLine();
                    }
                }
            }
            else
            {
                this.Output.WriteLine();
                this.Output.WriteLine("WARNING: Unable to ensure that this repo is excluded from antivirus.");
                this.Output.WriteLine("Please check to make sure that '{0}' is excluded.", enlistment.EnlistmentRoot);
                this.Output.WriteLine();
            }
        }

        protected void ValidateGVFSVersion(GVFSEnlistment enlistment, HttpGitObjects httpGitObjects, ITracer tracer)
        {
            using (ITracer activity = tracer.StartActivity("ValidateGVFSVersion", EventLevel.Informational))
            {
                Version currentVersion = new Version(ProcessHelper.GetCurrentProcessVersion());

                GVFSConfigResponse config = httpGitObjects.QueryGVFSConfig();
                IEnumerable<GVFSConfigResponse.VersionRange> allowedGvfsClientVersions =
                    config != null
                    ? config.AllowedGvfsClientVersions
                    : null;

                if (allowedGvfsClientVersions == null || !allowedGvfsClientVersions.Any())
                {
                    string errorMessage = string.Empty;
                    if (config == null)
                    {
                        errorMessage = "Could not query valid GVFS versions from: " + Uri.EscapeUriString(enlistment.RepoUrl);
                    }
                    else
                    {
                        errorMessage = "Server not configured to provide supported GVFS versions";
                    }

                    EventMetadata metadata = new EventMetadata();
                    metadata.Add("ErrorMessage", errorMessage);
                    tracer.RelatedError(metadata, Keywords.Network);

                    this.Output.WriteLine();
                    this.Output.WriteLine("WARNING: Unable to validate your GVFS version");
                    this.Output.WriteLine();
                    return;
                }

                foreach (GVFSConfigResponse.VersionRange versionRange in config.AllowedGvfsClientVersions)
                {
                    if (currentVersion >= versionRange.Min &&
                        (versionRange.Max == null || currentVersion <= versionRange.Max))
                    {
                        activity.RelatedEvent(
                            EventLevel.Informational,
                            "GVFSVersionValidated",
                            new EventMetadata
                            {
                                { "SupportedVersionRange", versionRange },
                            });
                        return;
                    }
                }

                activity.RelatedError("GVFS version {0} is not supported", currentVersion);
            }

            this.ReportErrorAndExit("\r\nERROR: Your GVFS version is no longer supported.  Install the latest and try again.\r\n");
        }

        public abstract class ForExistingEnlistment : GVFSVerb
        {
            [Value(
                0,
                Required = false,
                Default = "",
                MetaName = "Enlistment Root Path",
                HelpText = "Full or relative path to the GVFS enlistment root")]
            public override string EnlistmentRootPath { get; set; }

            public sealed override void Execute()
            {
                this.PreExecute(this.EnlistmentRootPath);
                GVFSEnlistment enlistment = this.CreateEnlistment(this.EnlistmentRootPath);
                this.Execute(enlistment);
            }

            protected virtual void PreExecute(string enlistmentRootPath)
            {
            }

            protected abstract void Execute(GVFSEnlistment enlistment);

            private GVFSEnlistment CreateEnlistment(string enlistmentRootPath)
            {
                string gitBinPath = GitProcess.GetInstalledGitBinPath();
                if (string.IsNullOrWhiteSpace(gitBinPath))
                {
                    this.ReportErrorAndExit("Error: " + GVFSConstants.GitIsNotInstalledError);
                }

                if (string.IsNullOrWhiteSpace(enlistmentRootPath))
                {
                    enlistmentRootPath = Environment.CurrentDirectory;
                }

                string hooksPath = ProcessHelper.WhereDirectory(GVFSConstants.GVFSHooksExecutableName);
                if (hooksPath == null)
                {
                    this.ReportErrorAndExit("Could not find " + GVFSConstants.GVFSHooksExecutableName);
                }

                GVFSEnlistment enlistment = null;
                try
                {
                    enlistment = GVFSEnlistment.CreateFromDirectory(enlistmentRootPath, null, gitBinPath, hooksPath);
                    if (enlistment == null)
                    {
                        this.ReportErrorAndExit(
                            "Error: '{0}' is not a valid GVFS enlistment",
                            enlistmentRootPath);
                    }
                }
                catch (InvalidRepoException e)
                {
                    this.ReportErrorAndExit(
                        "Error: '{0}' is not a valid GVFS enlistment. {1}",
                        enlistmentRootPath,
                        e.Message);
                }

                return enlistment;
            }
        }

        public class VerbAbortedException : Exception
        {
            public VerbAbortedException(GVFSVerb verb)
            {
                this.Verb = verb;
            }

            public GVFSVerb Verb { get; }
        }
    }
}
