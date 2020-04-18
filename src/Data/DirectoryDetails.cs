using Nito.AsyncEx;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace diskusage.Data
{
    public class DirectoryDetails
    {
        readonly static Dictionary<string, DirectoryDetails> Roots = new Dictionary<string, DirectoryDetails>();
        public static async Task<DirectoryDetails> GetDirectoryDetails(DirectoryInfo di)
        {
            var parent = di.Parent;
            if (parent != null)
            {
                var parentDetails = await GetDirectoryDetails(parent);
                return (await parentDetails.SubDirectories)[di.Name];
            }
            else
            {
                if (Roots.TryGetValue(di.FullName, out DirectoryDetails root))
                {
                    return root;
                }
                var newRoot = new DirectoryDetails(di, null);
                Roots.Add(newRoot.DirectoryInfo.FullName, newRoot);
                return newRoot;
            }
        }

        private DirectoryDetails(DirectoryInfo di, DirectoryDetails parent)
        {
            DirectoryInfo = di;
            Parent = parent;
            SubDirectories = new AsyncLazy<ImmutableDictionary<string, DirectoryDetails>>(async () =>
            {
                try
                {
                    return DirectoryInfo.GetDirectories()
                        .ToImmutableDictionary(s => s.Name, s => new DirectoryDetails(s, this));
                }
                catch (Exception) { return ImmutableDictionary<string, DirectoryDetails>.Empty; }
            });
            LocalSize = new AsyncLazy<long>(async () =>
            {
                try
                {
                    return DirectoryInfo.GetFiles()
                        .Select(s => s.Length)
                        .DefaultIfEmpty(0L).Sum();
                }
                catch (Exception) { return 0L; }
            });
            FullSize = new AsyncLazy<long>(async () =>
            {
                if(parent == null)
                {
                    var drive = DriveInfo.GetDrives()
                        .Single(s => s.Name == di.FullName);
                    return drive.TotalSize - drive.TotalFreeSpace;
                }
                return (await LocalSize) + 
                    (await Task.WhenAll((await SubDirectories)
                        .Select(async s => await s.Value.FullSize)))
                        .DefaultIfEmpty(0L)
                        .Sum();
            });
        }

        public DirectoryDetails Parent { get; }
        public AsyncLazy<ImmutableDictionary<string, DirectoryDetails>> SubDirectories { get; }
        public AsyncLazy<long> LocalSize { get; }
        public AsyncLazy<long> FullSize { get; }

        public DirectoryInfo DirectoryInfo { get; }
    }
}
