﻿using PKISharp.WACS.Clients.IIS;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Plugins.Base.Factories;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.TargetPlugins
{
    internal class IISOptionsFactory<TOptions>(
        ILogService log,
        IISHelper iisHelper,
        MainArguments args,
        ArgumentsInputService arguments) : PluginOptionsFactory<TOptions>
        where TOptions : IISOptions, new()
    {
        /// <summary>
        /// Change order in the menu
        /// </summary>
        public override int Order => 2;

        private ArgumentResult<string?> Host => arguments.GetString<IISArguments>(x => x.Host);
        private ArgumentResult<string?> Pattern => arguments.GetString<IISArguments>(x => x.Pattern);
        private ArgumentResult<string?> Regex => arguments.GetString<IISArguments>(x => x.Regex);
        private ArgumentResult<string?> SiteId => arguments.GetString<IISArguments>(x => x.SiteId);
        private ArgumentResult<string?> Type => arguments.
            GetString<IISArguments>(x => x.Type).
            DefaultAsNull().
            Validate(x => Task.FromResult(x.ParseCsv()?.All(x => x == IISHelper.WebTypeFilter || x == IISHelper.FtpTypeFilter) ?? false), "unsupported value");

        private ArgumentResult<string?> ExcludeBindings => arguments.GetString<IISArguments>(x => x.ExcludeBindings);
        private ArgumentResult<string?> CommonName => arguments.
            GetString<IISArguments>(x => x.CommonName).
            Validate(x => Task.FromResult(x?.Length <= Constants.MaxCommonName), $"Common name too long (max {Constants.MaxCommonName} characters)");

        /// <summary>
        /// Get settings in interactive mode
        /// </summary>
        /// <param name="input"></param>
        /// <param name="runLevel"></param>
        /// <returns></returns>
        public override async Task<TOptions?> Aquire(IInputService input, RunLevel runLevel)
        {
            var allSites = iisHelper.GetSites(true).Where(x => x.Hosts.Count != 0).ToList();
            if (allSites.Count == 0)
            {
                log.Error($"No websites with host bindings have been configured in IIS. " +
                    $"Add one in the IIS Manager or choose the plugin '{ManualOptions.DescriptionText}' " +
                    $"instead.");
                return null;
            }

            var visibleSites = allSites.Where(x => !args.HideHttps || x.Secure == false).ToList();
            if (visibleSites.Count == 0)
            {
                log.Error("No websites with host name bindings remain after applying the --{hidehttps} filter. " +
                    "It looks like all your websites are already configured for https!", "hidehttps");
                return null;
            }

            // Remove sites with only wildcard bindings because they cannot be validated in simple mode
            if (!runLevel.HasFlag(RunLevel.Advanced))
            {
                visibleSites = visibleSites.Where(x => x.Hosts.Any(h => !h.StartsWith('*'))).ToList();
                if (visibleSites.Count == 0)
                {
                    log.Error("No websites with host names remain after discarding wildcard identifiers. To " +
                        "create certificates including wildcards, please use the 'full options' mode, as " +
                        "this requires DNS validation.");
                    return null;
                }
            }

            // Repeat the process until the user is happy with their settings
            do
            {
                var allBindings = iisHelper.GetBindings();
                var visibleBindings = allBindings.Where(x => !args.HideHttps || x.Secure == false).ToList();
                var ret = await TryAquireSettings(input, allBindings, visibleBindings, allSites, visibleSites, runLevel);
                if (ret != null)
                {
                    var filtered = iisHelper.FilterBindings(allBindings, ret);
                    await ListBindings(input, filtered, ret.CommonName);
                    if (await input.PromptYesNo("Continue with this selection?", true))
                    {
                        return ret;
                    }
                }
                if (!await input.PromptYesNo("Restart?", true))
                {
                    return null;
                }
            } 
            while (true);
        }

        /// <summary>
        /// Single round of aquiring settings
        /// </summary>
        /// <param name="input"></param>
        /// <param name="allBindings"></param>
        /// <param name="visibleBindings"></param>
        /// <param name="allSites"></param>
        /// <param name="visibleSites"></param>
        /// <param name="runLevel"></param>
        /// <returns></returns>
        private async Task<TOptions?> TryAquireSettings(
            IInputService input,
            List<IISHelper.IISBindingOption> allBindings,
            List<IISHelper.IISBindingOption> visibleBindings,
            List<IISHelper.IISSiteOption> allSites,
            List<IISHelper.IISSiteOption> visibleSites,
            RunLevel runLevel)
        {
            input.CreateSpace();
            input.Show(null, "Please select which website(s) should be scanned for host names. " +
                "You may input one or more site identifiers (comma-separated) to filter by those sites, " +
                "or alternatively leave the input empty to scan *all* websites.");

            // Include all types by default
            var options = new TOptions
            {
                IncludeTypes = [
                    IISHelper.FtpTypeFilter,
                    IISHelper.WebTypeFilter
                ]
            };

            // Pick sites
            await input.WritePagedList(
                visibleSites.Select(x => Choice.Create(
                    item: x,
                    description: $"{x.Name} ({x.Hosts.Count} binding{(x.Hosts.Count == 1 ? "" : "s")})",
                    command: x.Id.ToString(),
                    color: x.Secure ? ConsoleColor.DarkGray : (ConsoleColor?)null)));
            var raw = await input.RequestString("Site identifier(s) or <Enter> to choose all");
            if (!ParseSiteOptions(raw, allSites, options))
            {
                return null;
            }

            // Pick bindings
            var filtered = iisHelper.FilterBindings(visibleBindings, options);
            await ListBindings(input, filtered);
            input.CreateSpace();
            input.Show(null,
                "Listed above are the bindings found on the selected site(s). By default all of " +
                "them will be included, but you may either pick specific ones by typing the host names " +
                "or identifiers (comma-separated) or filter them using one of the options from the " +
                "menu.");
            var askExclude = true;
            var filters = new List<Choice<Func<Task>>>
            {
                Choice.Create<Func<Task>>(
                    () => InputPattern(input, options), 
                    "Pick bindings based on a search pattern", command: "P"),
                Choice.Create<Func<Task>>(() => { 
                    askExclude = false; 
                    return Task.CompletedTask; 
                }, "Pick *all* bindings", @default: true, command: "A")
            };
            if (runLevel.HasFlag(RunLevel.Advanced))
            {
                filters.Insert(1, Choice.Create<Func<Task>>(() => { 
                    askExclude = true; 
                    return InputRegex(input, options);
                }, "Pick bindings based on a regular expression", command: "R"));
            }

            // Handle undefined input
            Choice<Func<Task>> processUnkown(string unknown) {
                return Choice.Create<Func<Task>>(() =>
                {
                    askExclude = false;
                    return Task.FromResult(ProcessInputHosts(
                        unknown, 
                        allBindings, 
                        filtered, 
                        options,
                        () => options.IncludeHosts, x => options.IncludeHosts = x));
                });
            }

            var chosen = await input.ChooseFromMenu(
                "Binding identifiers(s) or menu option",
                filters,
                processUnkown);
            await chosen.Invoke();

            filtered = iisHelper.FilterBindings(allBindings, options);

            // Exclude specific bindings
            if (askExclude && filtered.Count > 1 && runLevel.HasFlag(RunLevel.Advanced))
            {
                await ListBindings(input, filtered);
                input.CreateSpace();
                input.Show(null, "The listed bindings match your current filter settings. " +
                    "If you wish to exclude one or more of them from the certificate, please " +
                    "input those bindings now. Press <Enter> to include all listed bindings.");
                await InputHosts("Exclude bindings", 
                    input, allBindings, filtered, options, 
                    () => options.ExcludeHosts, x => options.ExcludeHosts = x);
                if (options.ExcludeHosts != null)
                {
                    filtered = iisHelper.FilterBindings(allBindings, options);
                }
            } 

            // Now the common name
            if (filtered.Select(x => x.HostUnicode).Distinct().Count() > 1)
            {
                await InputCommonName(input, filtered, options);
            }

            var ftpIncluded = filtered.Any(x => x.SiteType == IISSiteType.Ftp);
            var webIncluded = filtered.Any(x => x.SiteType == IISSiteType.Web);
            options.IncludeTypes = [];
            if (ftpIncluded)
            {
                options.IncludeTypes.Add(IISHelper.FtpTypeFilter);
            }
            if (webIncluded)
            {
                options.IncludeTypes.Add(IISHelper.WebTypeFilter);
            }
            return options;
        }

        /// <summary>
        /// Allows users to input both the full host name, or the number that
        /// it's referred to by in the displayed list
        /// </summary>
        /// <param name="label"></param>
        /// <param name="input"></param>
        /// <param name="allBindings"></param>
        /// <param name="filtered"></param>
        /// <param name="options"></param>
        /// <param name="get"></param>
        /// <param name="set"></param>
        /// <returns></returns>
        async Task InputHosts(
            string label,
            IInputService input,
            List<IISHelper.IISBindingOption> allBindings,
            List<IISHelper.IISBindingOption> filtered, 
            IISOptions options,
            Func<List<string>?> get,
            Action<List<string>> set)
        {
            string? raw;
            do
            {
                raw = await input.RequestString(label);
            }
            while (!ProcessInputHosts(raw, allBindings, filtered, options, get, set));
        }

        /// <summary>
        /// Allows users to input both the full host name, or the number that
        /// it's referred to by in the displayed list
        /// </summary>
        /// <param name="label"></param>
        /// <param name="input"></param>
        /// <param name="allBindings"></param>
        /// <param name="filtered"></param>
        /// <param name="options"></param>
        /// <param name="get"></param>
        /// <param name="set"></param>
        /// <returns></returns>
        bool ProcessInputHosts(
            string raw,
            List<IISHelper.IISBindingOption> allBindings,
            List<IISHelper.IISBindingOption> filtered,
            IISOptions options,
            Func<List<string>?> get,
            Action<List<string>> set)
        {
            var sorted = SortBindings(filtered).ToList();
            if (!string.IsNullOrEmpty(raw))
            {
                // Magically replace binding identifiers by their proper host names
                raw = string.Join(",", raw.ParseCsv()!.Select(x =>
                {
                    if (int.TryParse(x, out var id))
                    {
                        if (id > 0 && id <= sorted.Count)
                        {
                            return sorted[id - 1].HostUnicode;
                        }
                    }
                    return x;
                }));
            }
            return ParseHostOptions(raw, allBindings, options, get, set);
        }

        async Task InputCommonName(IInputService input, List<IISHelper.IISBindingOption> filtered, IISOptions options)
        {
            var sorted = SortBindings(filtered).ToList();
            var common = await input.ChooseRequired(
                "Please pick the main host, which will be presented as the subject of the certificate",
                sorted,
                (x) => Choice.Create(x, 
                        description: x.HostUnicode, 
                        @default: sorted.IndexOf(x) == 0));
            ParseCommonName(common.HostUnicode, filtered.Select(x => x.HostUnicode), options);
        }

        /// <summary>
        /// Interactive input of a search pattern
        /// </summary>
        /// <param name="input"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        async Task InputPattern(IInputService input, IISOptions options)
        {
            input.CreateSpace();
            input.Show(null, IISArguments.PatternExamples);
            string raw;
            do
            {
                raw = await input.RequestString("Pattern");
            }
            while (!SetPattern(raw, options));
        }

        async Task InputRegex(IInputService input, IISOptions options)
        {
            string raw;
            do
            {
                raw = await input.RequestString("Regex");
            }
            while (!ParseRegex(raw, options));
        }

        public static bool ParsePattern(string? pattern, ILogService log)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                log.Error("Invalid input");
                return false;
            }
            try
            {
                var regexString = pattern.PatternToRegex();
                var actualRegex = new Regex(regexString);
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Unable to convert pattern to regex");
                return false;
            }
        }

        private bool SetPattern(string? pattern, IISOptions ret)
        {
            if (ParsePattern(pattern, log))
            {
                ret.IncludePattern = pattern;
                return true;
            }
            return false;
        }

        private bool ParseRegex(string? regex, IISOptions options)
        {
            if (string.IsNullOrWhiteSpace(regex))
            {
                log.Error("Invalid input");
                return false;
            }
            try
            {
                var actualRegex = new Regex(regex);
                options.IncludeRegex = regex;
                return true;
            }
            catch (Exception ex)
            {
                log.Error(ex, "Unable to convert pattern to regex");
                return false;
            }
        }

        /// <summary>
        /// We need to use consistent sorting in both listing the bindings and parsing
        /// user input for includes, excludes and common name picking
        /// </summary>
        /// <param name="bindings"></param>
        /// <returns></returns>
        private IEnumerable<IISHelper.IISBindingOption> SortBindings(IEnumerable<IISHelper.IISBindingOption> bindings)
        {
            return bindings.
                OrderBy(x => x.HostUnicode, new HostnameSorter(iisHelper.DomainParser)).
                ThenBy(x => x.SiteId);
        }

        /// <summary>
        /// List bindings for the user to pick from
        /// </summary>
        /// <param name="input"></param>
        /// <param name="bindings"></param>
        /// <param name="highlight"></param>
        /// <returns></returns>
        private async Task ListBindings(IInputService input, List<IISHelper.IISBindingOption> bindings, string? highlight = null)
        {
            var sortedBindings = SortBindings(bindings);
            await input.WritePagedList(
               sortedBindings.Select(x => Choice.Create(
                   item: x,
                   color: BindingColor(x, highlight))));
        }

        private static ConsoleColor? BindingColor(IISHelper.IISBindingOption binding, string? highlight = null)
        {
            if (binding.HostUnicode == highlight) 
            {
                return ConsoleColor.Green;
            }
            else if (binding.Secure)
            {
                return ConsoleColor.DarkGray;
            }
            else
            {
                return default;
            }
        }

        public override async Task<TOptions?> Default()
        {
            var options = new TOptions();
            var host = await Host.GetValue();
            var pattern = await Pattern.GetValue();
            var regex = await Regex.GetValue();
            var siteId = await SiteId.GetValue();
            var types = await Type.GetValue();

            if (string.IsNullOrWhiteSpace(host) && 
                string.IsNullOrWhiteSpace(pattern) &&
                string.IsNullOrWhiteSpace(regex) &&
                string.IsNullOrWhiteSpace(siteId))
            {
                // Logically this would be a no-filter run: all 
                // bindings for all websites. Because the impact
                // of that can be so high, we want the user to
                // be explicit about it.
                log.Error("You have not specified any filters. If you are sure that you want " +
                    "to create a certificate for *all* bindings on the server, please specify " +
                    "--siteid s");
                return null;
            }

            // Host types
            options.IncludeTypes = types.ParseCsv();

            // Site options
            var allSites = iisHelper.GetSites(false);
            if (!ParseSiteOptions(siteId, allSites, options))
            {
                return null;
            }

            // Binding options
            var allBindings = iisHelper.GetBindings();
            if (!ParseHostOptions(host, allBindings, options, () => options.IncludeHosts, x => options.IncludeHosts = x))
            {
                return null;
            }

            // Pattern
            if (pattern != null)
            {
                if (options.IncludeHosts != null)
                {
                    log.Error("Parameters --host and --host-pattern cannot be combined");
                    return null;
                }
                if (!SetPattern(pattern, options))
                {
                    return null;
                }
            }

            // Regex
            if (regex != null)
            {
                if (options.IncludeHosts != null)
                {
                    log.Error("Parameters --host and --host-regex cannot be combined");
                    return null;
                }
                if (options.IncludePattern != null)
                {
                    log.Error("Parameters --host-pattern and --host-regex cannot be combined");
                    return null;
                }
                if (!ParseRegex(regex, options))
                {
                    return null;
                }
            }

            // Excludes
            var excludes = await ExcludeBindings.GetValue();
            var filtered = iisHelper.FilterBindings(allBindings, options);
            if (options.IncludeHosts == null && !string.IsNullOrEmpty(excludes))
            {
                if (!ParseHostOptions(excludes, filtered, options, () => options.ExcludeHosts, x => options.ExcludeHosts = x))
                {
                    return null;
                }
                filtered = iisHelper.FilterBindings(allBindings, options);
            }

            // Common name
            var common = await CommonName.GetValue();
            if (common != null)
            {
                if (!ParseCommonName(common, filtered.Select(x => x.HostUnicode), options))
                {
                    return null;
                }
            }

            return options;
        }

        /// <summary>
        /// Host filtering options in unattended mode
        /// </summary>
        /// <param name="args"></param>
        /// <param name="bindings"></param>
        /// <param name="options"></param>
        private bool ParseHostOptions(
            string? input,
            List<IISHelper.IISBindingOption> allBindings, 
            IISOptions options,
            Func<List<string>?> get,
            Action<List<string>> set)
        {
            var specifiedHosts = input.ParseCsv();
            if (specifiedHosts != null)
            {
                var filteredBindings = iisHelper.FilterBindings(allBindings, options);
                foreach (var specifiedHost in specifiedHosts)
                {
                    var filteredBinding = filteredBindings.FirstOrDefault(x => x.HostUnicode == specifiedHost || x.HostPunycode == specifiedHost);
                    var binding = allBindings.FirstOrDefault(x => x.HostUnicode == specifiedHost || x.HostPunycode == specifiedHost);
                    if (filteredBinding != null)
                    {
                        var list = get();
                        if (list == null)
                        {
                            list = [];
                            set(list);
                        }
                        list.Add(filteredBinding.HostUnicode);
                    }
                    else if (binding != null)
                    {
                        log.Error("Binding {specifiedHost} is excluded by another filter", specifiedHost);
                        return false;
                    }
                    else
                    {
                        log.Error("Binding {specifiedHost} not found", specifiedHost);
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>
        /// Advanced options in unattended mode
        /// </summary>
        /// <param name="args"></param>
        /// <param name="chosen"></param>
        /// <param name="ret"></param>
        /// <returns></returns>
        private bool ParseCommonName(string commonName, IEnumerable<string> chosen, IISOptions ret)
        {
            if (string.IsNullOrWhiteSpace(commonName))
            {
                return false;
            }
            commonName = commonName.ToLower().Trim().ConvertPunycode();
            if (chosen.Contains(commonName))
            {
                ret.CommonName = commonName;
                return true;
            }
            else
            {
                log.Error("Common name {commonName} not found or excluded", commonName);
                return false;
            }
        }

        /// <summary>
        /// SiteId filter in unattended mode
        /// </summary>
        /// <param name="args"></param>
        /// <param name="options"></param>
        /// <returns></returns>
        private bool ParseSiteOptions(string? input, List<IISHelper.IISSiteOption> sites, IISOptions options)
        {
            if (string.IsNullOrEmpty(input))
            {
                return true;
            }
            if (string.Equals(input, "s", StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            var identifiers = input.ParseCsv() ?? throw new InvalidOperationException("No identifiers found");
            var ret = new List<long>();
            foreach (var identifierString in identifiers)
            {
                if (long.TryParse(identifierString, out var id))
                {
                    var site = sites.Where(t => t.Id == id).FirstOrDefault();
                    if (site != null)
                    {
                        ret.Add(site.Id);
                    }
                    else
                    {
                        log.Error("Site identifier '{id}' not found", id);
                        return false;
                    }
                }
                else
                {
                    log.Error("Invalid site identifier '{identifierString}', should be a number", identifierString);
                    return false;
                }
            }
            options.IncludeSiteIds = ret;
            return true;
        }

        public override IEnumerable<(CommandLineAttribute, object?)> Describe(TOptions options)
        {
            // Special case "s"
            if (string.IsNullOrWhiteSpace(options.IncludePattern) &&
                string.IsNullOrWhiteSpace(options.IncludeRegex) &&
                (options.IncludeHosts == null || options.IncludeHosts.Count == 0) &&
                (options.IncludeSiteIds == null || options.IncludeSiteIds.Count == 0))
            {
                yield return (SiteId.Meta, "s");
                yield break;
            }

            yield return (Regex.Meta, options.IncludeRegex);
            yield return (Pattern.Meta, options.IncludePattern);
            if (options.IncludeTypes != null &&
                !string.Join(",", options.IncludeTypes).Equals("http", StringComparison.OrdinalIgnoreCase))
            {
                yield return (Type.Meta, options.IncludeTypes);
            }
            yield return (Host.Meta, options.IncludeHosts);
            yield return (CommonName.Meta, options.CommonName);
            yield return (ExcludeBindings.Meta, options.ExcludeHosts);
            yield return (SiteId.Meta, options.IncludeSiteIds);
        }
    }

    internal class IISOptionsFactory(ILogService log, IISHelper iisHelper, MainArguments args, ArgumentsInputService arguments) : IISOptionsFactory<IISOptions>(log, iisHelper, args, arguments) { }

    internal class IISBindingOptionsFactory(ILogService log, IISHelper iisHelper, MainArguments args, ArgumentsInputService arguments) : IISOptionsFactory<IISBindingOptions>(log, iisHelper, args, arguments) { }

    internal class IISSiteOptionsFactory(ILogService log, IISHelper iisHelper, MainArguments args, ArgumentsInputService arguments) : IISOptionsFactory<IISSiteOptions>(log, iisHelper, args, arguments) { }

    internal class IISSitesOptionsFactory(ILogService log, IISHelper iisHelper, MainArguments args, ArgumentsInputService arguments) : IISOptionsFactory<IISSitesOptions>(log, iisHelper, args, arguments) { }
}