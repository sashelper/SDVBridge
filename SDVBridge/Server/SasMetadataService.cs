using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SAS.Tasks.Toolkit.Data;
using SAS.Tasks.Toolkit;
using SDVBridge.Interop;

namespace SDVBridge.Server
{
    internal sealed class SasMetadataService
    {
        private readonly EgInteropContext _context;

        public SasMetadataService(EgInteropContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
        }

        public Task<IList<SasServerDto>> GetServersAsync()
        {
            return _context.RunOnUiAsync(() =>
            {
                var servers = SasServer.GetSasServers();
                return servers
                    .Select(s => new SasServerDto
                    {
                        Name = s.Name,
                        IsAssigned = false
                    })
                    .ToList() as IList<SasServerDto>;
            });
        }

        public Task<IList<SasLibraryDto>> GetLibrariesAsync(string serverName)
        {
            return _context.RunOnUiAsync(() =>
            {
                var server = FindServer(serverName);
                var libraries = server.GetSasLibraries();
                return libraries
                    .Select(lib => new SasLibraryDto
                    {
                        Name = lib.Name,
                        Libref = lib.Libref,
                        IsAssigned = lib.IsAssigned
                    })
                    .ToList() as IList<SasLibraryDto>;
            });
        }

        public Task<IList<SasDatasetDto>> GetDatasetsAsync(string serverName, string libref)
        {
            return _context.RunOnUiAsync(() =>
            {
                var server = FindServer(serverName);
                var library = server
                    .GetSasLibraries()
                    .FirstOrDefault(l => string.Equals(l.Libref, libref, StringComparison.OrdinalIgnoreCase));

                if (library == null)
                {
                    throw new InvalidOperationException($"Library '{libref}' was not found on server '{serverName}'.");
                }

                if (!library.IsAssigned)
                {
                    library.Assign();
                }

                var members = library.GetSasDataMembers();
                return members
                    .Select(d => new SasDatasetDto
                    {
                        Member = d.Member,
                        Libref = d.Libref,
                        Server = d.Server
                    })
                    .ToList() as IList<SasDatasetDto>;
            });
        }

        private static SasServer FindServer(string serverName)
        {
            var servers = SasServer.GetSasServers();
            if (string.IsNullOrWhiteSpace(serverName))
            {
                return servers.First();
            }

            var server = servers.FirstOrDefault(s => string.Equals(s.Name, serverName, StringComparison.OrdinalIgnoreCase));
            if (server == null)
            {
                throw new InvalidOperationException($"Server '{serverName}' was not found.");
            }

            return server;
        }
    }
}
