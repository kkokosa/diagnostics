// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Diagnostics.Runtime;
using Microsoft.Diagnostics.Runtime.Utilities;
using Microsoft.FileFormats.ELF;
using Microsoft.FileFormats.MachO;
using System;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using FileVersionInfo = Microsoft.Diagnostics.Runtime.Utilities.FileVersionInfo;

namespace Microsoft.Diagnostics.DebugServices.Implementation
{
    /// <summary>
    /// Module base implementation
    /// </summary>
    public abstract class Module : IModule, IDisposable
    {
        [Flags]
        public enum Flags : byte
        {
            None = 0x00,
            IsPEImage = 0x01,
            IsManaged = 0x02,
            IsFileLayout = 0x04,
            IsLoadedLayout = 0x08,
            InitializePEInfo = 0x10,
            InitializeVersion = 0x20,
            InitializeProductVersion = 0x40,
        }

        private readonly IDisposable _onChangeEvent;
        private Flags _flags;
        private PdbFileInfo _pdbFileInfo;
        private ImmutableArray<byte> _buildId;
        private PEImage _peImage;

        public readonly ServiceProvider ServiceProvider;

        public Module(ITarget target)
        {
            ServiceProvider = new ServiceProvider();
            ServiceProvider.AddServiceFactoryWithNoCaching<PEImage>(() => GetPEInfo());

            ServiceProvider.AddServiceFactory<PEReader>(() => ModuleService.GetPEReader(this));
            if (target.OperatingSystem == OSPlatform.Linux) {
                ServiceProvider.AddServiceFactory<ELFFile>(() => ModuleService.GetELFFile(this));
            }
            if (target.OperatingSystem == OSPlatform.OSX) {
                ServiceProvider.AddServiceFactory<MachOFile>(() => ModuleService.GetMachOFile(this));
            }
            _onChangeEvent = target.Services.GetService<ISymbolService>()?.OnChangeEvent.Register(() => {
                ServiceProvider.RemoveService(typeof(MachOFile)); 
                ServiceProvider.RemoveService(typeof(ELFFile));
                ServiceProvider.RemoveService(typeof(PEReader));
            });
         }

        public void Dispose()
        {
            _onChangeEvent?.Dispose();
        }

        #region IModule

        public ITarget Target => ModuleService.Target;

        public IServiceProvider Services => ServiceProvider;

        public abstract int ModuleIndex { get; }

        public abstract string FileName { get; }

        public abstract ulong ImageBase { get; }

        public abstract ulong ImageSize { get; }

        public abstract uint? IndexFileSize { get; }

        public abstract uint? IndexTimeStamp { get; }

        public bool IsPEImage
        {
            get
            {
                GetPEInfo();
                return (_flags & Flags.IsPEImage) != 0;
            }
        }

        public bool IsManaged
        {
            get
            {
                GetPEInfo();
                return (_flags & Flags.IsManaged) != 0;
            }
        }

        public bool? IsFileLayout
        {
            get
            {
                // For Windows targets we can assume that the file layout is always "loaded". The
                // ImageMappingMemoryService depends on no recursion memory access for this property
                // i.e. calling GetPEInfo().
                if (Target.OperatingSystem == OSPlatform.Windows)
                {
                    return false;
                }
                else
                {
                    GetPEInfo();
                    if ((_flags & Flags.IsFileLayout) != 0)
                    {
                        return true;
                    }
                    if ((_flags & Flags.IsLoadedLayout) != 0)
                    {
                        return false;
                    }
                    return null;
                }
            }
        }

        public PdbFileInfo PdbFileInfo
        {
            get
            {
                GetPEInfo();
                return _pdbFileInfo;
            }
        }

        public ImmutableArray<byte> BuildId
        {
            get
            {
                if (_buildId.IsDefault)
                {
                    byte[] id = ModuleService.GetBuildId(ImageBase, ImageSize);
                    if (id != null)
                    {
                        _buildId = id.ToImmutableArray();
                    }
                    else
                    {
                        _buildId = ImmutableArray<byte>.Empty;
                    }
                }
                return _buildId;
            }
        }

        public abstract VersionData VersionData { get; }

        public abstract string VersionString { get; }

        #endregion

        protected VersionData GetVersion()
        {
            VersionData versionData = null;

            PEImage peImage = GetPEInfo();
            if (peImage != null)
            {
                FileVersionInfo fileVersionInfo = peImage.GetFileVersionInfo();
                if (fileVersionInfo != null)
                {
                    versionData = fileVersionInfo.VersionInfo.ToVersionData();
                }
            }
            else 
            {
                // If we can't get the version from the PE, search for version string embedded in the module data
                string versionString = VersionString;
                if (versionString != null)
                {
                    int spaceIndex = versionString.IndexOf(' ');
                    if (spaceIndex > 0)
                    {
                        if (versionString[spaceIndex - 1] == '.')
                        {
                            spaceIndex--;
                        }
                        string versionToParse = versionString.Substring(0, spaceIndex);
                        try
                        {
                            Version version = System.Version.Parse(versionToParse);
                            versionData = new VersionData(version.Major, version.Minor, version.Build, version.Revision);
                        }
                        catch (ArgumentException ex)
                        {
                            Trace.TraceError($"Module.Version FAILURE: '{versionToParse}' '{versionString}' {ex}");
                        }
                    }
                }
            }

            return versionData;
        }

        protected PEImage GetPEInfo()
        {
            if (InitializeValue(Flags.InitializePEInfo)) {
                _peImage = ModuleService.GetPEInfo(ImageBase, ImageSize, ref _pdbFileInfo, ref _flags);
            }
            return _peImage;
        }

        protected bool InitializeValue(Flags flag)
        {
            if ((_flags & flag) == 0)
            {
                _flags |= flag;
                return true;
            }
            return false;
        }

        protected abstract ModuleService ModuleService { get; }

        public override bool Equals(object obj)
        {
            IModule module = (IModule)obj;
            return Target == module.Target && ImageBase == module.ImageBase;
        }

        public override int GetHashCode()
        {
            return Utilities.CombineHashCodes(Target.GetHashCode(), ImageBase.GetHashCode());
        }

        public override string ToString()
        {
            return $"#{ModuleIndex} {ImageBase:X16} {_flags} {FileName ?? ""}";
        }
    }
}
