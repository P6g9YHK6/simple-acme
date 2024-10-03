﻿using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.Configuration.Settings;
using PKISharp.WACS.Extensions;
using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using static System.Environment;

namespace PKISharp.WACS.Services
{
    public class SettingsService : ISettingsService
    {
        private readonly ILogService _log;
        private readonly Settings _settings;
        private readonly MainArguments? _arguments;

        public bool Valid { get; private set; } = false;

        public SettingsService(ILogService log, ArgumentsParser parser)
        {
            _log = log;
            _settings = new Settings();
            var settingsFileName = "settings.json";
            var settingsFileTemplateName = "settings_default.json";
            _log.Verbose("Looking for {settingsFileName} in {path}", settingsFileName, VersionService.SettingsPath);
            var settings = new FileInfo(Path.Combine(VersionService.SettingsPath, settingsFileName));
            var settingsTemplate = new FileInfo(Path.Combine(VersionService.ResourcePath, settingsFileTemplateName));
            var useFile = settings;
            if (!settings.Exists)
            {
                if (!settingsTemplate.Exists)
                {
                    // For .NET tool case
                    settingsTemplate = new FileInfo(Path.Combine(VersionService.ResourcePath, settingsFileName));
                }
                if (!settingsTemplate.Exists)
                {
                    _log.Warning("Unable to locate {settings}", settingsFileName);
                }
                else
                {
                    _log.Verbose("Copying {settingsFileTemplateName} to {settingsFileName}", settingsFileTemplateName, settingsFileName);
                    try
                    {
                        if (!settings.Directory!.Exists)
                        {
                            settings.Directory.Create();
                        }
                        settingsTemplate.CopyTo(settings.FullName);
                    }
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Unable to create {settingsFileName}, falling back to defaults", settingsFileName);
                        useFile = settingsTemplate;
                    }
                }
            }

            try
            {
                using var fs = useFile.OpenRead();
                var newSettings = JsonSerializer.Deserialize(fs, SettingsJson.Insensitive.Settings);
                if (newSettings != null)
                {
                    _settings = newSettings;
                }

                // This code specifically deals with backwards compatibility 
                // so it is allowed to use obsolete properties
#pragma warning disable CS0618
                static string? Fallback(string? x, string? y) => x ?? y;
                Source.DefaultSource = Fallback(Source.DefaultSource, Target.DefaultTarget);
                Store.PemFiles.DefaultPath = Fallback(Store.PemFiles.DefaultPath, Store.DefaultPemFilesPath);
                Store.CentralSsl.DefaultPath = Fallback(Store.CentralSsl.DefaultPath, Store.DefaultCentralSslStore);
                Store.CentralSsl.DefaultPassword = Fallback(Store.CentralSsl.DefaultPassword, Store.DefaultCentralSslPfxPassword);
                Store.CertificateStore.DefaultStore = Fallback(Store.CertificateStore.DefaultStore, Store.DefaultCertificateStore);
#pragma warning restore CS0618
            }
            catch (Exception ex)
            {
                _log.Error($"Unable to start program using {useFile.Name}");
                while (ex.InnerException != null)
                {
                    _log.Error(ex.InnerException.Message);
                    ex = ex.InnerException;
                }
                return;
            }

            // Validate command line and ensure main arguments
            // are loaded, because those influence the BaseUri
            if (!parser.Validate())
            {
                return;
            }
            _arguments = parser.GetArguments<MainArguments>();
            if (_arguments == null)
            {
                return;
            }
            try
            {     
                _ = BaseUri;
            } 
            catch
            {
                _log.Error("Error choosing ACME server");
                return;
            }

            try
            {
                var configRoot = ChooseConfigPath();
                Client.ConfigurationPath = Path.Combine(configRoot, BaseUri.CleanUri());
                Client.LogPath = ChooseLogPath();
                Cache.Path = ChooseCachePath();

                EnsureFolderExists(configRoot, "configuration", true);
                EnsureFolderExists(Client.ConfigurationPath, "configuration", false);
                EnsureFolderExists(Client.LogPath, "log", !Client.LogPath.StartsWith(Client.ConfigurationPath));
                EnsureFolderExists(Cache.Path, "cache", !Client.LogPath.StartsWith(Client.ConfigurationPath));

                // Configure disk logger
                _log.ApplyClientSettings(Client);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Error initializing program");
                return;
            }

            Valid = true;
        }

        public Uri BaseUri
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_arguments?.BaseUri))
                {
                    try
                    {
                        return new Uri(_arguments.BaseUri);
                    } 
                    catch (Exception ex)
                    {
                        _log.Error(ex, "Invalid --baseuri specified");
                        throw;
                    }
                }
                if (_arguments?.Test ?? false)
                {
                    if (Acme.DefaultBaseUriTest?.IsAbsoluteUri ?? false)
                    {
                        return Acme.DefaultBaseUriTest;
                    } 
                    else
                    {
                        _log.Warning("Setting Acme.DefaultBaseUriTest is unspecified or invalid, fallback to Acme.DefaultBaseUri");
                    }
                }
                if (Acme.DefaultBaseUri?.IsAbsoluteUri ?? false)
                {
                    return Acme.DefaultBaseUri;
                }
                else
                {
                    _log.Error("Setting Acme.DefaultBaseUri is unspecified or invalid, please specify a valid absolute URI");
                    throw new Exception();
                }
            }
        }

        /// <summary>
        /// Determine which folder to use for configuration data
        /// </summary>
        private string ChooseConfigPath()
        {
            var userRoot = Client.ConfigurationPath;
            string? configRoot;
            if (!string.IsNullOrEmpty(userRoot))
            {
                configRoot = userRoot;

                // Path configured in settings always wins, but
                // check for possible sub directories with client name
                // to keep bug-compatible with older releases that
                // created a subfolder inside of the users chosen config path
                var configRootWithClient = Path.Combine(userRoot, Client.ClientName);
                if (Directory.Exists(configRootWithClient))
                {
                    configRoot = configRootWithClient;
                }
            }
            else if (OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess)
            {
                var appData = Environment.GetFolderPath(SpecialFolder.CommonApplicationData, SpecialFolderOption.DoNotVerify);
                configRoot = Path.Combine(appData, Client.ClientName);
            }
            else
            {
                // For non-elevated Linux we have to fall back to the user directory
                // These user will not be able to auto-renew.
                var appData = Environment.GetFolderPath(SpecialFolder.LocalApplicationData, SpecialFolderOption.DoNotVerify);
                configRoot = Path.Combine(appData, Client.ClientName);
            }
            return configRoot;
        }

        /// <summary>
        /// Determine which folder to use for logging
        /// </summary>
        private string ChooseLogPath()
        {
            if (string.IsNullOrWhiteSpace(Client.LogPath))
            {
                return Path.Combine(Client.ConfigurationPath, "Log");
            }
            else
            {
                // Create separate logs for each endpoint
                return Path.Combine(Client.LogPath, BaseUri.CleanUri());
            }
        }

        /// <summary>
        /// Determine which folder to use for cache certificates
        /// </summary>
        private string ChooseCachePath()
        {
            if (string.IsNullOrWhiteSpace(Cache.Path))
            {
                return Path.Combine(Client.ConfigurationPath, "Certificates");
            }
            return Cache.Path;
        }

        /// <summary>
        /// Create folder if needed
        /// </summary>
        /// <param name="path"></param>
        /// <param name="label"></param>
        /// <exception cref="Exception"></exception>
        private void EnsureFolderExists(string path, string label, bool checkAcl)
        {
            var created = false;
            var di = new DirectoryInfo(path);
            if (!di.Exists)
            {
                try
                {
                    di = Directory.CreateDirectory(path);
                    _log.Debug($"Created {label} folder {{path}}", path);
                    created = true;
                }
                catch (Exception ex)
                {
                    throw new Exception($"Unable to create {label} {path}", ex);
                }
            }
            else
            {
                _log.Debug($"Use existing {label} folder {{path}}", path);
            }
            if (checkAcl)
            {
                if (OperatingSystem.IsWindows())
                {
                    EnsureFolderAcl(di, label, created);
                }
                else if (OperatingSystem.IsLinux())
                {
                    EnsureFolderAclLinux(di, label, created);
                }
              
            }
        }

        [SupportedOSPlatform("linux")]
        private void EnsureFolderAclLinux(DirectoryInfo di, string label, bool created) {
            var currentMode = File.GetUnixFileMode(di.FullName);
            if (currentMode.HasFlag(UnixFileMode.OtherRead) || 
                currentMode.HasFlag(UnixFileMode.OtherExecute) ||
                currentMode.HasFlag(UnixFileMode.OtherWrite))
            {
                if (!created)
                {
                    _log.Warning("All users currently have access to {path}.", di.FullName);
                    _log.Warning("We will now try to limit access to improve security...", label, di.FullName);
                }
                var newMode = currentMode & ~(UnixFileMode.OtherRead | UnixFileMode.OtherExecute | UnixFileMode.OtherWrite);
                _log.Warning("Change file mode in {label} to {newMode}", label, newMode);
                File.SetUnixFileMode(di.FullName, newMode);
            }
        } 

        /// <summary>
        /// Ensure proper access rights to a folder
        /// </summary>
        [SupportedOSPlatform("windows")]
        private void EnsureFolderAcl(DirectoryInfo di, string label, bool created)
        {
            // Test access control rules
            var (access, inherited) = UsersHaveAccess(di);
            if (!access)
            {
                return;
            }

            if (!created)
            {
                _log.Warning("All users currently have access to {path}.", di.FullName);
                _log.Warning("We will now try to limit access to improve security...", label, di.FullName);
            }
            try
            {
                var acl = di.GetAccessControl();
                if (inherited)
                {
                    // Disable access rule inheritance
                    acl.SetAccessRuleProtection(true, true);
                    di.SetAccessControl(acl);
                    acl = di.GetAccessControl();
                }

                var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
                foreach (FileSystemAccessRule rule in rules)
                {
                    if (rule.IdentityReference == sid &&
                        rule.AccessControlType == AccessControlType.Allow)
                    {
                        acl.RemoveAccessRule(rule);
                    }
                }
                var user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                {
                    // Allow user access from non-privilegdes perspective 
                    // as well.
                    acl.AddAccessRule(
                        new FileSystemAccessRule(
                            user,
                            FileSystemRights.Read | FileSystemRights.Delete | FileSystemRights.Modify,
                            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                            PropagationFlags.None,
                            AccessControlType.Allow));
                }
                di.SetAccessControl(acl);
                _log.Warning($"...done. You may manually add specific trusted accounts to the ACL.");
            } 
            catch (Exception ex)
            {
                _log.Error(ex, $"...failed, please take this step manually.");
            }
        }

        /// <summary>
        /// Test if users have access through inherited or direct rules
        /// </summary>
        /// <param name="di"></param>
        /// <returns></returns>
        [SupportedOSPlatform("windows")]
        private static (bool, bool) UsersHaveAccess(DirectoryInfo di)
        {
            var acl = di.GetAccessControl();
            var sid = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier));
            var hit = false;
            var inherited = false;
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.IdentityReference == sid &&
                    rule.AccessControlType == AccessControlType.Allow)
                {
                    hit = true;
                    inherited = inherited || rule.IsInherited;
                }
            }
            return (hit, inherited);
        }

        /// <summary>
        /// Interface implementation
        /// </summary>

        public UiSettings UI => _settings.UI;
        public AcmeSettings Acme => _settings.Acme;
        public ExecutionSettings Execution => _settings.Execution;
        public ProxySettings Proxy => _settings.Proxy;
        public CacheSettings Cache => _settings.Cache;
        public SecretsSettings Secrets => _settings.Secrets;
        public ScheduledTaskSettings ScheduledTask => _settings.ScheduledTask;
        public NotificationSettings Notification => _settings.Notification;
        public SecuritySettings Security => _settings.Security;
        public ScriptSettings Script => _settings.Script;
        public ClientSettings Client => _settings.Client;
        public SourceSettings Source => _settings.Source;
        [Obsolete("Use Source instead")]
        public SourceSettings Target => _settings.Target;
        public ValidationSettings Validation => _settings.Validation;
        public OrderSettings Order => _settings.Order;
        public CsrSettings Csr => _settings.Csr;
        public StoreSettings Store => _settings.Store;
        public InstallationSettings Installation => _settings.Installation;
    }
}