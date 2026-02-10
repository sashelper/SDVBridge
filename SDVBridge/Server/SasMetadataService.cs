using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
                var members = ResolveDataMembers(serverName, libref);
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

        public Task<IList<SasColumnDto>> GetColumnsAsync(string serverName, string libref, string member)
        {
            return _context.RunOnUiAsync(() =>
            {
                if (string.IsNullOrWhiteSpace(member))
                {
                    throw new ArgumentException("Dataset member is required.", nameof(member));
                }

                var dataset = ResolveDataMembers(serverName, libref)
                    .FirstOrDefault(d => string.Equals(d.Member, member, StringComparison.OrdinalIgnoreCase));
                if (dataset == null)
                {
                    throw new InvalidOperationException($"Dataset '{member}' was not found in '{libref}'.");
                }

                return ReadColumns(dataset);
            });
        }

        private static IList<SasColumnDto> ReadColumns(SasData dataset)
        {
            var result = new List<SasColumnDto>();
            if (dataset == null)
            {
                return result;
            }

            TryInvokeNoArgMethod(dataset, "PopulateColumns");
            TryInvokeNoArgMethod(dataset, "RefreshColumns");
            var rawColumns = GetMemberValue(dataset, "Columns")
                ?? GetMemberValue(dataset, "GetColumns")
                ?? GetMemberValue(dataset, "GetColumnsInUse");
            if (!(rawColumns is IEnumerable columns))
            {
                return result;
            }

            foreach (var column in columns)
            {
                if (column == null)
                {
                    continue;
                }

                var name = GetTextValue(column, "Name", "ColumnName", "Variable");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                result.Add(new SasColumnDto
                {
                    Name = name,
                    Label = GetTextValue(column, "Label"),
                    Type = GetTextValue(column, "Type", "ColumnType", "DataType"),
                    Length = GetIntValue(column, "Length"),
                    Format = GetTextValue(column, "Format"),
                    Informat = GetTextValue(column, "Informat")
                });
            }

            return result;
        }

        private static string GetTextValue(object target, params string[] names)
        {
            if (target == null || names == null)
            {
                return null;
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var value = GetMemberValue(target, name);
                if (value == null)
                {
                    continue;
                }

                var text = value as string ?? value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return null;
        }

        private static int GetIntValue(object target, params string[] names)
        {
            if (target == null || names == null)
            {
                return 0;
            }

            foreach (var name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var value = GetMemberValue(target, name);
                if (value == null)
                {
                    continue;
                }

                if (value is int asInt)
                {
                    return asInt;
                }

                if (value is long asLong && asLong <= int.MaxValue && asLong >= int.MinValue)
                {
                    return (int)asLong;
                }

                if (int.TryParse(value.ToString(), out var parsed))
                {
                    return parsed;
                }
            }

            return 0;
        }

        private static object GetMemberValue(object target, string memberName)
        {
            if (target == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var type = target.GetType();
            var property = type.GetProperty(memberName, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            var field = type.GetField(memberName, flags);
            if (field != null)
            {
                try
                {
                    return field.GetValue(target);
                }
                catch
                {
                    return null;
                }
            }

            var method = type
                .GetMethods(flags)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, memberName, StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 0 &&
                    m.ReturnType != typeof(void));
            if (method != null)
            {
                try
                {
                    return method.Invoke(target, null);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static void TryInvokeNoArgMethod(object target, string methodName)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
            var method = target.GetType()
                .GetMethods(flags)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, methodName, StringComparison.OrdinalIgnoreCase) &&
                    m.GetParameters().Length == 0);

            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(target, null);
            }
            catch
            {
                // Best effort: some EG versions may not require/allow explicit column refresh.
            }
        }

        private static IList<SasData> ResolveDataMembers(string serverName, string libref)
        {
            if (string.IsNullOrWhiteSpace(libref))
            {
                throw new ArgumentException("Libref is required.", nameof(libref));
            }

            var server = FindServer(serverName);
            var library = server
                .GetSasLibraries()
                .FirstOrDefault(l => string.Equals(l.Libref, libref, StringComparison.OrdinalIgnoreCase));

            if (library == null)
            {
                throw new InvalidOperationException($"Library '{libref}' was not found on server '{server.Name}'.");
            }

            if (!library.IsAssigned)
            {
                library.Assign();
            }

            return library.GetSasDataMembers();
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
