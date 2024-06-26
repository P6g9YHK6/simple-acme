﻿using PKISharp.WACS.Clients.DNS;
using PKISharp.WACS.Plugins.Base.Capabilities;
using PKISharp.WACS.Plugins.Interfaces;
using PKISharp.WACS.Plugins.ValidationPlugins.Dns;
using PKISharp.WACS.Plugins.ValidationPlugins.Dreamhost;
using PKISharp.WACS.Services;
using System;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins
{
    [IPlugin.Plugin<
        DreamhostOptions, DreamhostOptionsFactory,
        DnsValidationCapability, DreamhostJson>
        ("2bfb3ef8-64b8-47f1-8185-ea427b793c1a", 
        "Dreamhost", "Create verification records in Dreamhost DNS")]
    internal class DreamhostDnsValidation(
        LookupClientProvider dnsClient,
        ILogService logService,
        ISettingsService settings,
        SecretServiceManager ssm,
        DreamhostOptions options) : DnsValidation<DreamhostDnsValidation>(dnsClient, logService, settings)
    {
        private readonly DnsManagementClient _client = new(ssm.EvaluateSecret(options.ApiKey) ?? "", logService);

        public override async Task<bool> CreateRecord(DnsValidationRecord record)
        {
            try
            {
                await _client.CreateRecord(record.Authority.Domain, RecordType.TXT, record.Value);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to create record at Dreamhost: {ex.Message}");
                return false;
            }
        }

        public override async Task DeleteRecord(DnsValidationRecord record)
        {
            try
            {
                await _client.DeleteRecord(record.Authority.Domain, RecordType.TXT, record.Value);
            }
            catch (Exception ex)
            {
                _log.Warning($"Unable to delete record from Dreamhost: {ex.Message}");
            }
        }
    }
}
