﻿using Autofac;
using Autofac.Core;
using PKISharp.WACS.Clients.Acme;
using PKISharp.WACS.Clients.IIS;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Base.Options;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Plugins.TargetPlugins;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PKISharp.WACS
{

    public enum Shortcuts
    {
        A, // List all selected
        C, // Cancel
        D, // Show details
        E, // Edit
        F, // Filter
        L, // Show command line
        O, // Sort
        Q, // Quit
        R, // Run
        S, // Run force
        T, // Run force, no cache
        U, // Analyze
        V, // Revoke
        X, // Reset filter and sort
    }

    internal class RenewalManager
    {
        private readonly IInputService _input;
        private readonly ILogService _log;
        private readonly IRenewalStore _renewalStore;
        private readonly ArgumentsParser _arguments;
        private readonly MainArguments _args;
        private readonly ISharingLifetimeScope _container;
        private readonly IAutofacBuilder _scopeBuilder;
        private readonly ExceptionHandler _exceptionHandler;
        private readonly RenewalExecutor _renewalExecutor;
        private readonly RenewalCreator _renewalCreator;
        private readonly RenewalDescriber _renewalDescriber;
        private readonly IRenewalRevoker _renewalRevoker;
        private readonly ISettingsService _settings;
        private readonly DueDateStaticService _dueDate;
        private readonly AccountManager _accountManager;


        public RenewalManager(
            ArgumentsParser arguments, MainArguments args,
            IRenewalStore renewalStore, ISharingLifetimeScope container,
            IInputService input, ILogService log,
            ISettingsService settings, DueDateStaticService dueDate,
            IAutofacBuilder autofacBuilder, ExceptionHandler exceptionHandler,
            RenewalCreator renewalCreator, RenewalExecutor renewalExecutor,
            AccountManager accountManager, RenewalDescriber renewalDescriber, 
            IRenewalRevoker renewalRevoker)
        {
            _renewalStore = renewalStore;
            _args = args;
            _input = input;
            _log = log;
            _settings = settings;
            _arguments = arguments;
            _container = container;
            _scopeBuilder = autofacBuilder;
            _exceptionHandler = exceptionHandler;
            _renewalExecutor = renewalExecutor;
            _renewalCreator = renewalCreator;
            _dueDate = dueDate;
            _accountManager = accountManager;
            _renewalDescriber = renewalDescriber;
            _renewalRevoker = renewalRevoker;
        }

        /// <summary>
        /// Renewal management mode
        /// </summary>
        /// <returns></returns>
        internal async Task ManageRenewals()
        {
            IEnumerable<Renewal> originalSelection = _renewalStore.Renewals.OrderBy(x => x.LastFriendlyName);
            var selectedRenewals = originalSelection;
            var quit = false;
            var displayAll = false;
            do
            {
                var all = selectedRenewals.Count() == originalSelection.Count();
                var none = !selectedRenewals.Any();
                var totalLabel = originalSelection.Count() != 1 ? "renewals" : "renewal";
                var renewalSelectedLabel = selectedRenewals.Count() != 1 ? "renewals" : "renewal";
                var selectionLabel = 
                    all ? selectedRenewals.Count() == 1 ? "the renewal" : "*all* renewals" : 
                    none ? "no renewals" :  
                    $"{selectedRenewals.Count()} of {originalSelection.Count()} {totalLabel}";

                _input.CreateSpace();
                _input.Show(null, 
                    "Welcome to the renewal manager. Actions selected in the menu below will " +
                    "be applied to the following list of renewals. You may filter the list to target " +
                    "your action at a more specific set of renewals, or sort it to make it easier to " +
                    "find what you're looking for.");

                var displayRenewals = selectedRenewals;
                var displayLimited = !displayAll && selectedRenewals.Count() >= _settings.UI.PageSize;
                var displayHidden = 0;
                var displayHiddenLabel = "";
                if (displayLimited)
                {
                    displayRenewals = displayRenewals.Take(_settings.UI.PageSize - 1);
                    displayHidden = selectedRenewals.Count() - displayRenewals.Count();
                    displayHiddenLabel = displayHidden != 1 ? "renewals" : "renewal";
                }
                var choices = displayRenewals.Select(x => Choice.Create<Renewal?>(x,
                                  description: x.ToString(_dueDate, _input),
                                  color: x.History.LastOrDefault()?.Success ?? true ?
                                          _dueDate.IsDue(x) ?
                                              ConsoleColor.DarkYellow :
                                              ConsoleColor.Green :
                                          ConsoleColor.Red)).ToList();
                if (displayLimited)
                {
                    choices.Add(Choice.Create<Renewal?>(null,
                                  command: "More",
                                  description: $"{displayHidden} additional {displayHiddenLabel} selected but currently not displayed"));
                }
                await _input.WritePagedList(choices);
                displayAll = false;

                var noneState = none ? State.DisabledState("No renewals selected.") : State.EnabledState();
                var sortFilterState = selectedRenewals.Count() < 2 ? State.DisabledState("Not enough renewals to sort/filter.") : State.EnabledState();
                var editState =
                    selectedRenewals.Count() != 1 
                        ? none 
                            ? State.DisabledState("No renewals selected.") 
                            : State.DisabledState("Multiple renewals selected.") 
                        : State.EnabledState();

                var options = new List<Choice<Func<Task>>>();
                if (displayLimited)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            () => { displayAll = true; return Task.CompletedTask; },
                            "List all selected renewals", Shortcuts.A.ToString()));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => { quit = true; await EditRenewal(selectedRenewals.First()); },
                        "Edit renewal", Shortcuts.E.ToString(), state: editState));
                if (selectedRenewals.Count() > 1)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            async () => selectedRenewals = await FilterRenewalsMenu(selectedRenewals),
                            all ? "Apply filter" : "Apply additional filter", Shortcuts.F.ToString(), state: sortFilterState));
                    options.Add(
                        Choice.Create<Func<Task>>(
                             async () => selectedRenewals = await SortRenewalsMenu(selectedRenewals),
                            "Sort renewals", Shortcuts.O.ToString(), state: sortFilterState));
                }
                if (!all)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(
                            () => { selectedRenewals = originalSelection; return Task.CompletedTask; },
                            "Reset sorting and filtering", Shortcuts.X.ToString()));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => { 
                            foreach (var renewal in selectedRenewals) {
                                var index = selectedRenewals.ToList().IndexOf(renewal) + 1;
                                _input.Show($"-");
                                _input.Show($"Renewal {index}/{selectedRenewals.Count()}");
                                _input.Show($"-");
                                _renewalDescriber.Show(renewal);
                                var cont = false;
                                if (index != selectedRenewals.Count())
                                {
                                    cont = await _input.Wait("Press <Enter> to continue or <Esc> to abort");
                                    if (!cont)
                                    {
                                        break;
                                    }
                                } 
                                else
                                {
                                    await _input.Wait();
                                }

                            } 
                        },
                        $"Show details for {selectionLabel}", Shortcuts.D.ToString(),
                        state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            foreach (var renewal in selectedRenewals)
                            {
                                _input.Show(null, _renewalDescriber.Describe(renewal));
                                _input.CreateSpace();
                            }
                            await _input.Wait();
                        },
                        $"Show command line for {selectionLabel}", Shortcuts.L.ToString(),
                        state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive),
                        $"Run {selectionLabel}", Shortcuts.R.ToString(), state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive | RunLevel.Force),
                        $"Run {selectionLabel} (force)", Shortcuts.S.ToString(), state: noneState));
                if (_settings.Cache.ReuseDays > 0)
                {
                    options.Add(
                        Choice.Create<Func<Task>>(() => Run(selectedRenewals, RunLevel.Interactive | RunLevel.Force | RunLevel.NoCache),
                        $"Run {selectionLabel} (force, no cache)", Shortcuts.T.ToString(), state: noneState));
                }
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => selectedRenewals = await Analyze(selectedRenewals),
                        $"Analyze duplicates for {selectionLabel}", Shortcuts.U.ToString(), state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            var confirm = await _input.PromptYesNo($"Are you sure you want to cancel {selectedRenewals.Count()} currently selected {renewalSelectedLabel}?", false);
                            if (confirm)
                            {
                                await _renewalRevoker.CancelRenewals(selectedRenewals);
                                originalSelection = _renewalStore.Renewals.OrderBy(x => x.LastFriendlyName);
                                selectedRenewals = originalSelection;
                            }
                        },
                        $"Cancel {selectionLabel}", Shortcuts.C.ToString(), state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        async () => {
                            var confirm = await _input.PromptYesNo($"Are you sure you want to revoke the most recently issued certificate for {selectedRenewals.Count()} currently selected {renewalSelectedLabel}? This should only be done in case of a (suspected) security breach. Cancel the {renewalSelectedLabel} if you simply don't need the certificates anymore.", false);
                            if (confirm)
                            {
                                await _renewalRevoker.RevokeCertificates(selectedRenewals);
                            }
                        },
                        $"Revoke certificate(s) for {selectionLabel}", Shortcuts.V.ToString(), state: noneState));
                options.Add(
                    Choice.Create<Func<Task>>(
                        () => { quit = true; return Task.CompletedTask; },
                        "Back", Shortcuts.Q.ToString(),
                        @default: !originalSelection.Any()));

                if (selectedRenewals.Count() > 1)
                {
                    _input.CreateSpace();
                    _input.Show(null, $"Currently selected {selectedRenewals.Count()} of {originalSelection.Count()} {totalLabel}");
                }
                var chosen = await _input.ChooseFromMenu(
                    "Choose an action or type numbers to select renewals",
                    options, 
                    (string unexpected) =>
                        Choice.Create<Func<Task>>(() => { selectedRenewals = FilterRenewalsById(selectedRenewals, unexpected); return Task.CompletedTask; }));
                await chosen.Invoke();
                _container.Resolve<IIISClient>().Refresh();
            }
            while (!quit);
        }

        /// <summary>
        /// Run selected renewals
        /// </summary>
        /// <param name="selectedRenewals"></param>
        /// <param name="flags"></param>
        /// <returns></returns>
        private async Task Run(IEnumerable<Renewal> selectedRenewals, RunLevel flags)
        {
            WarnAboutRenewalArguments();
            foreach (var renewal in selectedRenewals)
            {
                await ProcessRenewal(renewal, flags);
            }
        }

        /// <summary>
        /// Helper to get target for a renewal
        /// </summary>
        /// <param name="renewal"></param>
        /// <returns></returns>
        private async Task<Target?> GetTarget(Renewal renewal)
        {
            try
            {
                using var targetScope = _scopeBuilder.PluginBackend<ITargetPlugin, TargetPluginOptions>(_container, renewal.TargetPluginOptions);
                var targetBackend = targetScope.Resolve<ITargetPlugin>();
                return await targetBackend.Generate();
            } 
            catch
            {
                
            }
            return null;
        }

        /// <summary>
        /// Check if there are multiple renewals installing to the same site 
        /// or requesting certificates for the same domains
        /// </summary>
        /// <param name="selectedRenewals"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> Analyze(IEnumerable<Renewal> selectedRenewals)
        {
            var foundHosts = new Dictionary<Identifier, List<Renewal>>();
            var foundSites = new Dictionary<long, List<Renewal>>();

            foreach (var renewal in selectedRenewals)
            {
                var initialTarget = await GetTarget(renewal);
                if (initialTarget == null)
                {
                    _log.Warning("Unable to generate source for renewal {renewal}, analysis incomplete", renewal.FriendlyName);
                    continue;
                }
                foreach (var targetPart in initialTarget.Parts)
                {
                    if (targetPart.SiteId != null)
                    {
                        var siteId = targetPart.SiteId.Value;
                        if (!foundSites.TryGetValue(siteId, out var value))
                        {
                            value = new List<Renewal>();
                            foundSites.Add(siteId, value);
                        }
                        value.Add(renewal);
                    }
                    foreach (var host in targetPart.GetIdentifiers(true))
                    {
                        if (!foundHosts.TryGetValue(host, out var value))
                        {
                            value = new List<Renewal>();
                            foundHosts.Add(host, value);
                        }
                        value.Add(renewal);
                    }
                }
            }

            // List results
            var options = new List<Choice<List<Renewal>>>();
            foreach (var site in foundSites)
            {
                if (site.Value.Count > 1)
                {
                    options.Add(
                      Choice.Create(
                          site.Value,
                          $"Select {site.Value.Count} renewals covering IIS site {site.Key}"));
                }
            }
            foreach (var host in foundHosts)
            {
                if (host.Value.Count > 1)
                {
                    options.Add(
                      Choice.Create(
                          host.Value,
                          $"Select {host.Value.Count} renewals covering host {host.Key.Value}"));
                }
            }
            _input.CreateSpace();
            if (options.Count == 0)
            {
                _input.Show(null, "Analysis didn't find any overlap between renewals.");
                return selectedRenewals;
            }
            else
            {
                options.Add(
                    Choice.Create(
                        selectedRenewals.ToList(),
                        $"Back"));
                _input.Show(null, "Analysis found some overlap between renewals. You can select the overlapping renewals from the menu.");
                return await _input.ChooseFromMenu("Please choose from the menu", options);
            }
        }

        /// <summary>
        /// Offer user different ways to sort the renewals
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> SortRenewalsMenu(IEnumerable<Renewal> current)
        {
            var options = new List<Choice<Func<IEnumerable<Renewal>>>>
            {
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderBy(x => x.LastFriendlyName ?? ""),
                    "Sort by friendly name",
                    @default: true),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderByDescending(x => x.LastFriendlyName ?? ""),
                    "Sort by friendly name (descending)"),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderBy(x => _dueDate.DueDate(x)),
                    "Sort by due date"),
                Choice.Create<Func<IEnumerable<Renewal>>>(
                    () => current.OrderByDescending(x => _dueDate.DueDate(x)),
                    "Sort by due date (descending)")
            };
            var chosen = await _input.ChooseFromMenu("How would you like to sort the renewals list?", options);
            return chosen.Invoke();
        }

        /// <summary>
        /// Offer user different ways to filter the renewals
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> FilterRenewalsMenu(IEnumerable<Renewal> current)
        {
            var options = new List<Choice<Func<Task<IEnumerable<Renewal>>>>>
            {
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => FilterRenewalsByFriendlyName(current),
                    "Filter by friendly name"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => _dueDate.IsDue(x))),
                    "Filter by due status (keep due)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => !_dueDate.IsDue(x))),
                    "Filter by due status (remove due)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => x.History.Last().Success != true)),
                    "Filter by error status (keep errors)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current.Where(x => x.History.Last().Success == true)),
                    "Filter by error status (remove errors)"),
                Choice.Create<Func<Task<IEnumerable<Renewal>>>>(
                    () => Task.FromResult(current),
                    "Cancel")
            };
            var chosen = await _input.ChooseFromMenu("How would you like to filter?", options);
            return await chosen.Invoke();
        }

        private IEnumerable<Renewal> FilterRenewalsById(IEnumerable<Renewal> current, string input)
        {
            var parts = input.ParseCsv();
            if (parts == null)
            {
                return current;
            }
            var ret = new List<Renewal>();
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var index))
                {
                    if (index > 0 && index <= current.Count())
                    {
                        ret.Add(current.ElementAt(index - 1));
                    }
                    else
                    {
                        _log.Warning("Input out of range: {part}", part);
                    }
                }
                else
                {
                    _log.Warning("Invalid input: {part}", part);
                }
            }
            return ret;
        }

        /// <summary>
        /// Filter specific renewals by friendly name
        /// </summary>
        /// <param name="current"></param>
        /// <returns></returns>
        private async Task<IEnumerable<Renewal>> FilterRenewalsByFriendlyName(IEnumerable<Renewal> current)
        {
            _input.CreateSpace();
            _input.Show(null, "Please input friendly name to filter renewals by. " + IISArguments.PatternExamples);
            var rawInput = await _input.RequestString("Friendly name");
            var ret = new List<Renewal>();
            var regex = new Regex(rawInput.PatternToRegex(), RegexOptions.IgnoreCase);
            foreach (var r in current)
            {
                if (!string.IsNullOrEmpty(r.LastFriendlyName) && regex.Match(r.LastFriendlyName).Success)
                {
                    ret.Add(r);
                }
            }
            return ret;
        }

        /// <summary>
        /// Loop through the store renewals and run those which are
        /// due to be run
        /// </summary>
        internal async Task CheckRenewals(RunLevel runLevel)
        {
            IEnumerable<Renewal> renewals;
            if (_args.HasFilter)
            {
                renewals = _renewalStore.FindByArguments(_args.Id, _args.FriendlyName);
                if (!renewals.Any())
                {
                    _log.Error("No renewals found that match the filter parameters --id and/or --friendlyname.");
                }
            }
            else
            {
                _log.Verbose("Checking renewals");
                renewals = _renewalStore.Renewals;
                if (!renewals.Any())
                {
                    _log.Warning("No scheduled renewals found.");
                }
            }

            if (renewals.Any())
            {
                WarnAboutRenewalArguments();
                foreach (var renewal in renewals)
                {
                    try
                    {
                        var success = await ProcessRenewal(renewal, runLevel);
                        if (success == false)
                        {
                            // Make sure the ExitCode is set
                            _exceptionHandler.HandleException();
                        }
                    } 
                    catch (Exception ex)
                    {
                        _exceptionHandler.HandleException(ex, "Unhandled error processing renewal");
                        continue;
                    }
                }
            }
        }

        /// <summary>
        /// Process a single renewal
        /// </summary>
        /// <param name="renewal"></param>
        internal async Task<bool?> ProcessRenewal(Renewal renewal, RunLevel runLevel)
        {
            var notification = _container.Resolve<NotificationService>();
            try
            {
                var result = await _renewalExecutor.HandleRenewal(renewal, runLevel);
                if (result.OrderResults.Any())
                {
                    _renewalStore.Save(renewal, result);
                }
                if (!result.Abort)
                {
                    if (result.Success == true)
                    {
                        await notification.NotifySuccess(renewal, _log.Lines);
                        return true;
                    }
                    else
                    {
                        await notification.NotifyFailure(runLevel, renewal, result, _log.Lines);
                        return false;
                    }
                }
                return null;
            }
            catch (Exception ex)
            {
                _exceptionHandler.HandleException(ex);
                await notification.NotifyFailure(runLevel, renewal, new RenewResult(ex.Message), _log.Lines);
                return false;
            }
        }

        /// <summary>
        /// Show a warning when the user appears to be trying to
        /// use command line arguments in combination with a renew
        /// command.
        /// </summary>
        internal void WarnAboutRenewalArguments()
        {
            if (_arguments.Active())
            {
                _log.Warning("You have specified command line options for plugins. " +
                    "Note that these only affect new certificates, but NOT existing renewals. " +
                    "To change settings, re-create (overwrite) the renewal.");
            }
        }

        /// <summary>
        /// "Edit" renewal
        /// </summary>
        private async Task EditRenewal(Renewal renewal)
        {
            var options = new List<Choice<Steps>>
            {
                Choice.Create(Steps.All, "All"),
                Choice.Create(Steps.Source, "Source"),
                Choice.Create(Steps.Order, "Order"),
                Choice.Create(Steps.Csr, "CSR"),
                Choice.Create(Steps.Validation, "Validation"),
                Choice.Create(Steps.Store, "Store"),
                Choice.Create(Steps.Installation, "Installation"),
                Choice.Create(Steps.Account, "Account", state: _accountManager.ListAccounts().Count() > 1 ? State.EnabledState() : State.DisabledState("Only one account is registered.")),
                Choice.Create(Steps.None, "Cancel")
            };
            var chosen = await _input.ChooseFromMenu("Which step do you want to edit?", options);
            if (chosen != Steps.None)
            {
                await _renewalCreator.SetupRenewal(RunLevel.Interactive | RunLevel.Advanced | RunLevel.Force, chosen, renewal);
            }
        }
    }
}
