﻿using PKISharp.WACS.Clients.IIS;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace PKISharp.WACS.DomainObjects
{
    [DebuggerDisplay("TargetPart: ({Identifiers.Count} host(s) - IIS: {IIS})")]
    public class TargetPart
    {
        public TargetPart(Identifier identifier)
        {
            ArgumentNullException.ThrowIfNull(identifier);
            Identifiers = [identifier];
        }

        public TargetPart(IEnumerable<Identifier>? identifiers)
        {
            ArgumentNullException.ThrowIfNull(identifiers);
            Identifiers = identifiers.ToList();
        }

        /// <summary>
        /// Optional IIS site ID that sourced these hostnames
        /// </summary>
        public long? SiteId { get; set; }

        /// <summary>
        /// What type of site is this target part from
        /// </summary>
        public IISSiteType? SiteType { get; set; }

        /// <summary>
        /// Short check
        /// </summary>
        public bool IIS => 
            SiteType == IISSiteType.Ftp || 
            SiteType == IISSiteType.Web;

        /// <summary>
        /// <summary>
        /// Different parts that make up this target
        /// </summary>
        public List<Identifier> Identifiers { get; }
    }
}
