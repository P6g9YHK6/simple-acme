﻿using PKISharp.WACS.Clients.IIS;
using PKISharp.WACS.Configuration;
using PKISharp.WACS.Configuration.Arguments;

namespace PKISharp.WACS.Plugins.InstallationPlugins
{
    internal class IISArguments : BaseArguments
    {
        private const string SslPortParameterName = "sslport";
        private const string SslIpParameterName = "sslipaddress";

        [CommandLine(Description = "Specify site to install new bindings to. Defaults to the source if that is an IIS site.")]
        public long? InstallationSiteId { get; set; }

        [CommandLine(Obsolete = true, Description = "Specify site to install new bindings to. Defaults to the source if that is an IIS site.")]
        public long? FtpSiteId { get; set; }

        [CommandLine(Name = SslPortParameterName, Description = "Port number to use for newly created HTTPS bindings. Defaults to " + IISClient.DefaultBindingPortFormat + ".")]
        public int? SSLPort { get; set; }

        [CommandLine(Name = SslIpParameterName, Description = "IP address to use for newly created HTTPS bindings. Defaults to " + IISClient.DefaultBindingIp + ".")]
        public string? SSLIPAddress { get; set; }
    }
}
