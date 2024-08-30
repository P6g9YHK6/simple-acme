﻿using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;

namespace PKISharp.WACS.Plugins.ValidationPlugins
{
    public abstract class HttpValidationArguments : BaseArguments
    {
        [CommandLine(Description = "Root path of the site that will serve the HTTP validation requests.")]
        public string? WebRoot { get; set; }

        [CommandLine(Obsolete = true, Description = "Not used (warmup is the new default).")]
        public bool Warmup { get; set; }

        [CommandLine(Description = "Copy default web.config to the .well-known directory.")]
        public bool ManualTargetIsIIS { get; set; }
    }
}
