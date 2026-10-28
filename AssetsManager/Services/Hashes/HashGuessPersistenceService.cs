using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AssetsManager.Views.Models.Hashes;

namespace AssetsManager.Services.Hashes
{
    public sealed class HashGuessPersistenceService
    {
        private readonly HashGuessingStore _pathStore;
        private readonly BinRstHashGuessingStore _internalStore;

        public HashGuessPersistenceService(HashGuessingStore pathStore, BinRstHashGuessingStore internalStore)
        {
            _pathStore = pathStore;
            _internalStore = internalStore;
        }

        public async Task CommitPathRunAsync(
            HashGuessDomain domain,
            IEnumerable<ulong> remainingUnknowns,
            CancellationToken cancellationToken)
        {
            await _pathStore.SaveUnknownHashesAsync(domain, remainingUnknowns, cancellationToken);
        }

        public Task PromotePathMatchesAsync(IEnumerable<HashGuessMatch> matches, CancellationToken cancellationToken) =>
            _pathStore.SaveHashesAsync(matches, cancellationToken);

        public Task CommitInternalInventoryAsync(
            IReadOnlyDictionary<InternalHashKind, HashSet<ulong>> observed,
            string patchFingerprint,
            string domain,
            CancellationToken cancellationToken) =>
            _internalStore.SaveInventoryAsync(observed, patchFingerprint, domain, cancellationToken);

        public Task CommitInternalMatchesAsync(IEnumerable<InternalHashGuessMatch> matches, CancellationToken cancellationToken) =>
            _internalStore.SaveMatchesAsync(matches, cancellationToken);
    }
}
