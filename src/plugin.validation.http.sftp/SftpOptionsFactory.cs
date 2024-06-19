﻿using PKISharp.WACS.Configuration;
using PKISharp.WACS.DomainObjects;
using PKISharp.WACS.Services;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace PKISharp.WACS.Plugins.ValidationPlugins.Http
{
    /// <summary>
    /// Sftp validation
    /// </summary>
    internal class SftpOptionsFactory(Target target, ArgumentsInputService arguments) : HttpValidationOptionsFactory<SftpOptions>(arguments, target)
    {
        public override bool PathIsValid(string path) => path.StartsWith("sftp://");

        public override string[] WebrootHint(bool allowEmpty)
        {
            return [
                "SFTP path",
                "Example, sftp://domain.com:22/site/wwwroot/",
            ];
        }

        public override async Task<SftpOptions?> Default()
        {
            return new SftpOptions(await BaseDefault())
            {
                Credential = await NetworkCredentialOptions.Create(_arguments)
            };
        }

        public override async Task<SftpOptions?> Aquire(IInputService inputService, RunLevel runLevel)
        {
            return new SftpOptions(await BaseAquire(inputService))
            {
                Credential = await NetworkCredentialOptions.Create(_arguments, inputService, "SFTP server")
            };
        }

        public override IEnumerable<(CommandLineAttribute, object?)> Describe(SftpOptions options)
        {
            foreach (var x in base.Describe(options))
            {
                yield return x;
            }
            if (options.Credential != null)
            {
                foreach (var x in options.Credential.Describe(_arguments))
                {
                    yield return x;
                }
            }
        }
    }
}