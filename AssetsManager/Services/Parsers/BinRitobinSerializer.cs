using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AssetsManager.Services.Hashes;
using AssetsManager.Views.Models.Hashes;
using LeagueToolkit.Core.Meta;
using LeagueToolkit.Core.Meta.Properties;
using LeagueToolkit.Toolkit.Ritobin;

namespace AssetsManager.Services.Parsers
{
    public sealed class BinRitobinSerializer
    {
        private readonly HashResolverService _hashResolver;

        public BinRitobinSerializer(HashResolverService hashResolver)
        {
            _hashResolver = hashResolver;
        }

        public Task<string> WriteBinTreeAsRitobinAsync(byte[] data)
        {
            ArgumentNullException.ThrowIfNull(data);
            return Task.Run(() =>
            {
                BinTree tree = ReadTree(data);
                using RitobinWriter writer = CreateWriter(tree);
                return writer.WritePropertyBin(tree);
            });
        }

        public Task<(string OldRitobin, string NewRitobin)> WriteBinDiffAsRitobinAsync(
            byte[] oldData,
            byte[] newData)
        {
            ArgumentNullException.ThrowIfNull(oldData);
            ArgumentNullException.ThrowIfNull(newData);

            return Task.Run(() =>
            {
                BinTree oldTree = ReadTree(oldData);
                BinTree newTree = ReadTree(newData);

                // PTCH data overrides are file-level metadata rather than object properties.
                // Preserve the complete sides so changes to overrides remain visible in the text diff.
                if (oldTree.IsOverride || newTree.IsOverride)
                {
                    using RitobinWriter fullWriter = CreateWriter(oldTree, newTree);
                    return (fullWriter.WritePropertyBin(oldTree), fullWriter.WritePropertyBin(newTree));
                }

                BinTreeDiff diff = oldTree.Diff(newTree);
                var oldObjects = new Dictionary<uint, BinTreeObject>();
                var newObjects = new Dictionary<uint, BinTreeObject>();

                foreach (BinTreeObjectDiff objectDiff in diff.Objects)
                {
                    switch (objectDiff)
                    {
                        case AddedBinTreeObjectDiff added:
                            newObjects.Add(added.PathHash, added.Object);
                            break;
                        case RemovedBinTreeObjectDiff removed:
                            oldObjects.Add(removed.PathHash, removed.Object);
                            break;
                        case ModifiedBinTreeObjectDiff modified:
                            oldObjects.Add(
                                modified.PathHash,
                                CreateChangedObject(modified.OldObject, modified.Properties, useNewValues: false));
                            newObjects.Add(
                                modified.PathHash,
                                CreateChangedObject(modified.NewObject, modified.Properties, useNewValues: true));
                            break;
                    }
                }

                BinTree oldDiffTree = CreateDiffTree(oldTree, oldObjects.Values);
                BinTree newDiffTree = CreateDiffTree(newTree, newObjects.Values);
                using RitobinWriter writer = CreateWriter(oldTree, newTree);
                string oldRitobin = writer.WritePropertyBin(oldDiffTree);
                string newRitobin = writer.WritePropertyBin(newDiffTree);
                return (oldRitobin, newRitobin);
            });
        }

        private static BinTree CreateDiffTree(BinTree source, IEnumerable<BinTreeObject> objects) =>
            new(objects, source.Dependencies);

        private static BinTreeObject CreateChangedObject(
            BinTreeObject source,
            IReadOnlyList<BinTreePropertyDiff> differences,
            bool useNewValues)
        {
            return new BinTreeObject(
                source.PathHash,
                source.ClassHash,
                BuildChangedProperties(source.Properties, differences, useNewValues, pathIndex: 0));
        }

        private static IEnumerable<BinTreeProperty> BuildChangedProperties(
            IReadOnlyDictionary<uint, BinTreeProperty> properties,
            IEnumerable<BinTreePropertyDiff> differences,
            bool useNewValues,
            int pathIndex)
        {
            foreach (IGrouping<uint, BinTreePropertyDiff> group in differences
                .Where(difference => difference.Path.Count > pathIndex)
                .GroupBy(difference => difference.Path[pathIndex])
                .OrderBy(group => group.Key))
            {
                if (!properties.TryGetValue(group.Key, out BinTreeProperty property))
                    continue;

                BinTreePropertyDiff directDifference = group
                    .FirstOrDefault(difference => difference.Path.Count == pathIndex + 1);
                if (directDifference is not null)
                {
                    BinTreeProperty changedProperty = GetPropertyForSide(directDifference, useNewValues);
                    if (changedProperty is not null)
                        yield return changedProperty;
                    continue;
                }

                if (property is not BinTreeStruct structure)
                    continue;

                yield return CreateStructLike(
                    structure,
                    BuildChangedProperties(structure.Properties, group, useNewValues, pathIndex + 1));
            }
        }

        private static BinTreeProperty GetPropertyForSide(
            BinTreePropertyDiff difference,
            bool useNewValues)
        {
            return difference switch
            {
                AddedBinTreePropertyDiff added when useNewValues => added.Property,
                RemovedBinTreePropertyDiff removed when !useNewValues => removed.Property,
                ModifiedBinTreePropertyDiff modified => useNewValues ? modified.NewProperty : modified.OldProperty,
                _ => null
            };
        }

        private static BinTreeStruct CreateStructLike(
            BinTreeStruct source,
            IEnumerable<BinTreeProperty> properties)
        {
            return source is BinTreeEmbedded
                ? new BinTreeEmbedded(source.NameHash, source.ClassHash, properties)
                : new BinTreeStruct(source.NameHash, source.ClassHash, properties);
        }

        private static BinTree ReadTree(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            return new BinTree(stream);
        }

        private RitobinWriter CreateWriter(params BinTree[] trees)
        {
            var entryHashes = new HashSet<uint>();
            var classHashes = new HashSet<uint>();
            var propertyHashes = new HashSet<uint>();
            var binHashes = new HashSet<uint>();
            var wadHashes = new HashSet<ulong>();

            foreach (BinTree tree in trees)
            {
                foreach (BinTreeObject treeObject in tree.Objects.Values)
                {
                    entryHashes.Add(treeObject.PathHash);
                    classHashes.Add(treeObject.ClassHash);
                    foreach (BinTreeProperty property in treeObject.Properties.Values)
                        CollectProperty(property, propertyHashes, classHashes, entryHashes, binHashes, wadHashes);
                }

                foreach (BinTreeDataOverride dataOverride in tree.DataOverrides)
                {
                    entryHashes.Add(dataOverride.ObjectPathHash);
                    CollectProperty(
                        dataOverride.Property,
                        propertyHashes,
                        classHashes,
                        entryHashes,
                        binHashes,
                        wadHashes,
                        includeName: false);
                }
            }

            return new RitobinWriter(
                Resolve(entryHashes, _hashResolver.ResolveBinEntry),
                Resolve(classHashes, _hashResolver.ResolveBinType),
                Resolve(propertyHashes, _hashResolver.ResolveBinField),
                Resolve(binHashes, _hashResolver.ResolveBinHash),
                ResolveWadHashes(wadHashes));
        }

        private static void CollectProperty(
            BinTreeProperty property,
            ISet<uint> propertyHashes,
            ISet<uint> classHashes,
            ISet<uint> entryHashes,
            ISet<uint> binHashes,
            ISet<ulong> wadHashes,
            bool includeName = true)
        {
            if (includeName)
                propertyHashes.Add(property.NameHash);

            switch (property)
            {
                case BinTreeStruct structure:
                    if (structure.ClassHash is not 0)
                        classHashes.Add(structure.ClassHash);
                    foreach (BinTreeProperty child in structure.Properties.Values)
                        CollectProperty(child, propertyHashes, classHashes, entryHashes, binHashes, wadHashes);
                    break;
                case BinTreeMap map:
                    foreach (var pair in map)
                    {
                        CollectProperty(pair.Key, propertyHashes, classHashes, entryHashes, binHashes, wadHashes, false);
                        CollectProperty(pair.Value, propertyHashes, classHashes, entryHashes, binHashes, wadHashes, false);
                    }
                    break;
                case BinTreeContainer container:
                    foreach (BinTreeProperty element in container.Elements)
                        CollectProperty(element, propertyHashes, classHashes, entryHashes, binHashes, wadHashes, false);
                    break;
                case BinTreeOptional optional when optional.Value is not null:
                    CollectProperty(optional.Value, propertyHashes, classHashes, entryHashes, binHashes, wadHashes, false);
                    break;
                case BinTreeObjectLink objectLink:
                    entryHashes.Add(objectLink.Value);
                    break;
                case BinTreeHash hash:
                    binHashes.Add(hash.Value);
                    break;
                case BinTreeWadChunkLink wadChunkLink:
                    wadHashes.Add(wadChunkLink.Value);
                    break;
            }
        }

        private static IEnumerable<KeyValuePair<uint, string>> Resolve(
            IEnumerable<uint> hashes,
            Func<uint, string> resolver)
        {
            foreach (uint hash in hashes)
            {
                string value = resolver(hash);
                if (!string.Equals(value, hash.ToString("x8", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
                    yield return new KeyValuePair<uint, string>(hash, value);
            }
        }

        private IEnumerable<KeyValuePair<ulong, string>> ResolveWadHashes(IEnumerable<ulong> hashes)
        {
            foreach (ulong hash in hashes)
            {
                if (_hashResolver.IsKnownHash(hash))
                    yield return new KeyValuePair<ulong, string>(hash, _hashResolver.ResolveHash(hash));
            }
        }
    }
}
