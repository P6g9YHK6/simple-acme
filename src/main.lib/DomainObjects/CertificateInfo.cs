﻿using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.X509;
using PKISharp.WACS.Extensions;
using PKISharp.WACS.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace PKISharp.WACS.DomainObjects
{
    /// <summary>
    /// Provides information about a certificate, which may or may not already
    /// be stored on the disk somewhere in a .pfx file
    /// </summary>
    public partial class CertificateInfo : ICertificateInfo
    {

        private readonly byte[] _hash;
        private readonly List<Identifier> _san;

        /// <summary>
        /// Convert certificate to a different protection mode
        /// </summary>
        /// <param name="certificateInfo"></param>
        /// <param name="protectionMode"></param>
        public CertificateInfo(ICertificateInfo certificateInfo, PfxProtectionMode protectionMode) :
            this(PfxService.ConvertPfx(certificateInfo.Collection, protectionMode)) { }
        
        /// Default constructor
        /// </summary>
        /// <param name="collection"></param>
        /// <exception cref="InvalidDataException"></exception>
        public CertificateInfo(PfxWrapper collection)
        {
            // Store original collection
            Collection = collection;

            // Get first certificate that has not been used to issue 
            // another one in the collection. That is the outermost leaf
            // and thus will be our main certificate
            var certificates = collection.
                Store.
                Aliases.
                Select(alias => new { alias, collection.Store.GetCertificate(alias).Certificate }).
                ToList();
            if (certificates.Count == 0)
            {
                throw new InvalidDataException("Empty X509Certificate2Collection");
            }

            var main = certificates.FirstOrDefault(x => !certificates.Any(y => x.Certificate.SubjectDN.ToString() == y.Certificate.IssuerDN.ToString()));
           
            // Self-signed (unit test)
            main ??= certificates.First();

            Certificate = main.Certificate;
            FriendlyName = main.alias;

            // Compute fingerprint
            var encoded = Certificate.GetEncoded();
            var sha1 = new Sha1Digest();
            sha1.BlockUpdate(encoded, 0, encoded.Length);
            _hash = new byte[20];
            sha1.DoFinal(_hash, 0);
            Thumbprint = Convert.ToHexString(_hash);

            // Identify identifiers
            var str = Certificate.SubjectDN.CommonName();
            if (!string.IsNullOrWhiteSpace(str))
            {
                CommonName = new DnsIdentifier(str);
            }
            _san = Certificate.
                GetSubjectAlternativeNameExtension().
                GetNames().
                Select<GeneralName, Identifier>(name =>
                {
                    var value = name.ToString().Split(": ")[1];
                    switch (name.TagNo)
                    {
                        case GeneralName.DnsName:
                            {
                                return new DnsIdentifier(value);
                            }
                        case GeneralName.IPAddress:
                            {
                                if (value.Length < 10)
                                { 
                                    // Assume IPv4
                                    value = value.Replace("#", "0x");
                                }
                                else
                                {
                                    // Assume IPv6
                                    value = value.Replace("#", "");
                                    value = IP6Regex().Replace(value, "$0:").Trim(':');
                                }
                                return new IpIdentifier(IPAddress.Parse(value));
                            }
                        default:
                            {
                                return new UnknownIdentifier(value);
                            }
                    }
                }).ToList();

            // Check if we have the private key
            PrivateKey = collection.
                Store.
                Aliases.
                Where(collection.Store.IsKeyEntry).
                Select(a => collection.Store.GetKey(a).Key).
                FirstOrDefault();

            // Now order the remaining certificates in the correct order of who signed whom.
            var certonly = certificates.Select(t => t.Certificate).ToList();
            certonly.Remove(Certificate);
            var lastChainElement = Certificate;
            var orderedCollection = new List<X509Certificate>();
            while (certonly.Count > 0)
            {
                var signedBy = certonly.FirstOrDefault(x => lastChainElement.IssuerDN.ToString() == x.SubjectDN.ToString());
                if (signedBy == null)
                {
                    // Chain cannot be resolved any further
                    break;
                }
                orderedCollection.Add(signedBy);
                lastChainElement = signedBy;
                certonly.Remove(signedBy);
            }
            Chain = orderedCollection;
        }

        public PfxWrapper Collection { get; private set; }

        public X509Certificate Certificate { get; private set; }

        public string FriendlyName { get; private set; }

        public AsymmetricKeyParameter? PrivateKey { get; private set; }

        public IEnumerable<X509Certificate> Chain { get; private set; }

        public Identifier? CommonName { get; private set; }

        public IEnumerable<Identifier> SanNames => _san;

        public byte[] GetHash() => _hash;

        public string Thumbprint { get; private set; }

        [GeneratedRegex(".{4}")]
        private static partial Regex IP6Regex();
    }
}
