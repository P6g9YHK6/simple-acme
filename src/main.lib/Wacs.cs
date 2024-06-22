﻿using PKISharp.WACS.Clients;
using PKISharp.WACS.Clients.IIS;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Services;
using System;
using System.Threading.Tasks;

namespace PKISharp.WACS.Host
{
    internal class Wacs
    {
        private readonly IInputService _input;
        private readonly IIISClient _iis;
        private readonly ILogService _log;
        private readonly ISettingsService _settings;
        private readonly AdminService _adminService;
        private readonly NetworkCheckService _networkCheck;
        private readonly UpdateClient _updateClient;
        private readonly ArgumentsParser _arguments;
        private readonly ExceptionHandler _exceptionHandler;
        private readonly MainArguments _args;
        private readonly RenewalManager _renewalManager;
        private readonly Unattended _unattended;
        private readonly RenewalCreator _renewalCreator;
        private readonly IAutoRenewService _taskScheduler;
        private readonly VersionService _versionService;
        private readonly MainMenu _mainMenu;

        public Wacs(
            ExceptionHandler exceptionHandler,
            IIISClient iis,
            UpdateClient updateClient,
            ILogService logService,
            IInputService inputService,
            ISettingsService settingsService,
            VersionService versionService,
            ArgumentsParser argumentsParser,
            AdminService adminService,
            RenewalCreator renewalCreator,
            NetworkCheckService networkCheck,
            RenewalManager renewalManager,
            Unattended unattended,
            IAutoRenewService taskSchedulerService,
            MainMenu mainMenu)
        {
            // Basic services
            _exceptionHandler = exceptionHandler;
            _log = logService;
            _settings = settingsService;
            _updateClient = updateClient;
            _networkCheck = networkCheck;
            _adminService = adminService;
            _taskScheduler = taskSchedulerService;
            _renewalCreator = renewalCreator; 
            _renewalManager = renewalManager;
            _arguments = argumentsParser;
            _input = inputService;
            _versionService = versionService;
            _unattended = unattended;
            _mainMenu = mainMenu;
            _iis = iis;

            if (!string.IsNullOrWhiteSpace(_settings.UI.TextEncoding))
            {
                try
                {
                    var encoding = System.Text.Encoding.GetEncoding(_settings.UI.TextEncoding);
                    Console.OutputEncoding = encoding;
                    Console.InputEncoding = encoding;
                    Console.Title = $"win-acme {VersionService.SoftwareVersion}";
                }
                catch
                {
                    _log.Warning("Error setting text encoding to {name}", _settings.UI.TextEncoding);
                }
            }

            _arguments.ShowCommandLine();
            _args = _arguments.GetArguments<MainArguments>() ?? new MainArguments();
        }

        /// <summary>
        /// Main program
        /// </summary>
        public async Task<int> Start()
        {
            // Exit when settings are not valid. The settings service
            // also checks the command line arguments
            if (!_settings.Valid)
            {
                return -1;
            }
            if (!_versionService.Init())
            {
                return -1;
            }

            // List informational message and start-up diagnostics
            await ShowBanner();

            // Version display
            if (_args.Version)
            {
                await CloseDefault();
                if (_args.CloseOnFinish)
                {
                    return 0;
                }
            }

            // Help function
            if (_args.Help)
            {
                _arguments.ShowArguments();
                await CloseDefault();
                if (_args.CloseOnFinish)
                {
                    return 0;
                }
            }

            // Base runlevel flags on command line arguments
            var unattendedRunLevel = RunLevel.Unattended;
            var interactiveRunLevel = RunLevel.Interactive;
            if (_args.Force)
            {
                unattendedRunLevel |= RunLevel.Force | RunLevel.NoCache;
            }
            if (_args.NoCache)
            {
                interactiveRunLevel |= RunLevel.Test;
                unattendedRunLevel |= RunLevel.NoCache;
            }
            if (_args.Test)
            {
                interactiveRunLevel |= RunLevel.Test;
                unattendedRunLevel |= RunLevel.Test;
            }

            // Main loop
            do
            {
                try
                {
                    if (_args.Import)
                    {
                        await _mainMenu.Import(unattendedRunLevel);
                        await CloseDefault();
                    }
                    else if (_args.List)
                    {
                        await _unattended.List();
                        await CloseDefault();
                    }
                    else if (_args.Cancel)
                    {
                        await _unattended.Cancel();
                        await CloseDefault();
                    }
                    else if (_args.Revoke)
                    {
                        await _unattended.Revoke();
                        await CloseDefault();
                    }
                    else if (_args.Register)
                    {
                        await _unattended.Register();
                        await CloseDefault();
                    }
                    else if (_args.Renew)
                    {
                        await _renewalManager.CheckRenewals(unattendedRunLevel);
                        await CloseDefault();
                    }
                    else if (!string.IsNullOrEmpty(_args.Target) || !string.IsNullOrEmpty(_args.Source))
                    {
                        await _renewalCreator.SetupRenewal(unattendedRunLevel);
                        await CloseDefault();
                    }
                    else if (_args.Encrypt)
                    {
                        await _mainMenu.Encrypt(unattendedRunLevel);
                        await CloseDefault();
                    }
                    else if (_args.SetupTaskScheduler)
                    {
                        await _taskScheduler.SetupAutoRenew(unattendedRunLevel);
                        await CloseDefault();
                    }
                    else
                    {
                        await _mainMenu.MainMenuEntry(interactiveRunLevel);
                    }
                }
                catch (Exception ex)
                {
                    _exceptionHandler.HandleException(ex);
                    await CloseDefault();
                }
                if (!_args.CloseOnFinish)
                {
                    _args.Clear();
                    _exceptionHandler.ClearError();
                    _iis.Refresh();
                }
            }
            while (!_args.CloseOnFinish);

            // Return control to the caller
            _log.Verbose("Exiting with status code {code}", _exceptionHandler.ExitCode);
            return _exceptionHandler.ExitCode;
        }

        /// <summary>
        /// Show banner
        /// </summary>
        private async Task ShowBanner()
        {
            // Version information
            _input.CreateSpace();
            _log.Information(LogType.Screen, "A simple cross platform ACME client (WACS)");
            _log.Information(LogType.Screen, "Software version {version} ({build}, {bitness})", VersionService.SoftwareVersion, VersionService.BuildType, VersionService.Bitness);
            _log.Information(LogType.Disk | LogType.Event, "Software version {version} ({build}, {bitness}) started", VersionService.SoftwareVersion, VersionService.BuildType, VersionService.Bitness);
            _log.Debug("Running on {platform} {version}", Environment.OSVersion.Platform, Environment.OSVersion.Version);
 
            // Connection test
            _log.Information("Connecting to {ACME}...", _settings.BaseUri);
            var networkCheck = _networkCheck.CheckNetwork();
            await networkCheck.WaitAsync(TimeSpan.FromSeconds(30));
            if (!networkCheck.IsCompletedSuccessfully)
            {
                _log.Warning("Network check failed or timed out, retry without proxy detection...");
                _settings.Proxy.Url = null;
                networkCheck = _networkCheck.CheckNetwork();
                await networkCheck.WaitAsync(TimeSpan.FromSeconds(30));
            }
            if (!networkCheck.IsCompletedSuccessfully)
            {
                _log.Warning("Network check failed or timed out. Functionality may be limited.");
            }

            // New version test
            if (_settings.Client.VersionCheck)
            {
                _input.CreateSpace();
                await _updateClient.CheckNewVersion();
            }

            // IIS version test
            if (_adminService.IsAdmin)
            {
                if (OperatingSystem.IsWindows())
                {
                    _log.Debug("Running as administrator");
                    var iis = _iis.Version;
                    if (iis.Major > 0)
                    {
                        _log.Debug("IIS version {version}", iis);
                    }
                    else
                    {
                        _log.Debug("IIS not detected");
                    }
                }
                else
                {
                    _log.Debug("Running as superuser/root");
                }
            }
            else
            {
                _log.Information("Running as limited user, some options disabled");
            }

            // Task scheduler health check
            _taskScheduler.ConfirmAutoRenew();

            // Further information and tests
            _log.Information("Please report bugs at {url}", "https://github.com/win-acme/win-acme");
            _log.Verbose("Unicode display test: Chinese/{chinese} Russian/{russian} Arab/{arab}", "語言", "язык", "لغة");
        }

        /// <summary>
        /// Present user with the option to close the program
        /// Useful to keep the console output visible when testing
        /// unattended commands
        /// </summary>
        private async Task CloseDefault()
        {
            _args.CloseOnFinish =
                !_args.Test ||
                _args.CloseOnFinish || 
                await _input.PromptYesNo("[--test] Quit?", true);
        }
    }
}