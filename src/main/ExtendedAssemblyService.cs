﻿namespace PKISharp.WACS.Services
{
    public class ExtendedAssemblyService : AssemblyService
    {
        public ExtendedAssemblyService(ILogService logger) : base(logger)
        {
            _allTypes.AddRange(new[] { 
                new TypeDescriptor(typeof(Plugins.ValidationPlugins.Http.Ftp)), 
                new TypeDescriptor(typeof(Plugins.ValidationPlugins.Http.Sftp)),
                new TypeDescriptor(typeof(Plugins.ValidationPlugins.Http.WebDav))
            });
        }
    }
}