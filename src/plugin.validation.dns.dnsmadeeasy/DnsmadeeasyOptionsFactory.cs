﻿using PKISharp.WACS.Configuration;
using PKISharp.WACS.Plugins.Base.Factories;
using PKISharp.WACS.Services;
using PKISharp.WACS.Services.Serialization;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Dns
{
    /// <summary>
    /// DnsMadeEasy DNS validation
    /// </summary>
    internal class DnsMadeEasyOptionsFactory(ArgumentsInputService arguments) : PluginOptionsFactory<DnsMadeEasyOptions>
    {
        private ArgumentResult<ProtectedString?> ApiKey => arguments.
            GetProtectedString<DnsMadeEasyArguments>(a => a.ApiKey).
            Required();

        private ArgumentResult<ProtectedString?> ApiSecret => arguments.
            GetProtectedString<DnsMadeEasyArguments>(a => a.ApiSecret);

        public override async Task<DnsMadeEasyOptions?> Aquire(IInputService input, RunLevel runLevel)
        {
            return new DnsMadeEasyOptions()
            {
                ApiKey = await ApiKey.Interactive(input).GetValue(),
                ApiSecret = await ApiSecret.Interactive(input).GetValue(),
            };
        }

        public override async Task<DnsMadeEasyOptions?> Default()
        {
            return new DnsMadeEasyOptions()
            {
                ApiKey = await ApiKey.GetValue(),
                ApiSecret = await ApiSecret.GetValue(),
            };
        }

        public override IEnumerable<(CommandLineAttribute, object?)> Describe(DnsMadeEasyOptions options)
        {
            yield return (ApiKey.Meta, options.ApiKey);
            yield return (ApiSecret.Meta, options.ApiSecret);
        }
    }
}
