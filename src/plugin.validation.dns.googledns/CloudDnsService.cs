﻿using Google.Apis.Dns.v1;
using Google.Apis.Dns.v1.Data;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Dns
{
    internal sealed class CloudDnsService(DnsService client)
    {
        public async Task<IList<ManagedZone>> GetManagedZones(string projectId)
        {
            var request = client.ManagedZones.List(projectId);
            var response = await request.ExecuteAsync();
            return [.. response.ManagedZones];
        }

        public async Task<ManagedZone?> FindZone(string projectId, string dnsName)
        {
            var zones = await GetManagedZones(projectId);
            return zones.FirstOrDefault(z => z.DnsName.StartsWith(dnsName));
        }

        public async Task<ResourceRecordSet> CreateTxtRecord(string projectId, ManagedZone zone, string name, string value)
        {
            if (!name.EndsWith('.'))
                name += ".";

            var body = new ResourceRecordSet
            {
                Kind = "dns#resourceRecordSet",
                Name = name,
                Type = "TXT",
                Ttl = 0,
                Rrdatas = ["\"" + value + "\""]
            };

            var request = client.ResourceRecordSets.Create(body, projectId, zone.Name);

            return await request.ExecuteAsync();
        }

        public async Task<ResourceRecordSetsDeleteResponse> DeleteTxtRecord(string projectId, ManagedZone zone, string name)
        {
            if (!name.EndsWith('.'))
                name += ".";

            var request = client.ResourceRecordSets.Delete(projectId, zone.Name, name, "TXT");
            return await request.ExecuteAsync();
        }
    }
}
