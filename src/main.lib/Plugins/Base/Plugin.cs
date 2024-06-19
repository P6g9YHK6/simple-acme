﻿using PKISharp.WACS.Plugins.Interfaces;
using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace PKISharp.WACS.Plugins
{
    /// <summary>
    /// Metadata for a specific plugin
    /// </summary>
    [DebuggerDisplay("{Backend.Name}")]
    public class BasePlugin([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type source)
    {
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type Backend { get; set; } = source;
    }

    /// <summary>
    /// Metadata for a specific plugin
    /// </summary>
    [DebuggerDisplay("{Backend.Name}")]
    public class Plugin([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type source, IPluginMeta meta, Steps step) : BasePlugin(source)
    {
        public Guid Id { get; set; } = meta.Id;
        public Steps Step { get; set; } = step;

        public string Name => meta.Name;
        public string Description => meta.Description;
        public bool Hidden => meta.Hidden;
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type Options => meta.Options;
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type OptionsFactory => meta.OptionsFactory;
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type OptionsJson => meta.OptionsJson;
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)]
        public Type Capability => meta.Capability;
    }
}
