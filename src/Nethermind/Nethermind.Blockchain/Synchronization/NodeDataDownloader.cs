/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Logging;
using Nethermind.Store;

namespace Nethermind.Blockchain.Synchronization
{
    public class NodeDataDownloader : INodeDataDownloader
    {
        private static AccountDecoder accountDecoder = new AccountDecoder();

        private Keccak _fastSyncProgressKey = Keccak.Compute("fast_sync_progress");
        private const int MaxRequestSize = 384;
        private long _lastRequestedNodesCount;
        private long _consumedNodesCount;
        private long _savedStorageCount;
        private long _savedStateCount;
        private long _savedNodesCount;
        private long _savedAccounts;
        private long _savedCode;
        private long _requestedNodesCount;
        private long _dbChecks;
        private long _checkWasCached;
        private long _checkWasInDependencies;
        private long _stateWasThere;
        private long _stateWasNotThere;
        private int maxStateLevel; // for priority calculation (prefer depth)

        private const int MaxPendingRequestsCount = 1;
        private int _pendingRequests;

        private Stopwatch _prepareWatch = new Stopwatch();
        private Stopwatch _networkWatch = new Stopwatch();
        private Stopwatch _handleWatch = new Stopwatch();

        private Keccak _rootNode;

        private ISnapshotableDb _codeDb;
        private ILogger _logger;
        private ISnapshotableDb _stateDb;
        private INodeDataRequestExecutor _executor;

        private int TotalCount => Stream0.Count + Stream1.Count + Stream2.Count;

        private ConcurrentStack<StateSyncItem>[] _nodes = new ConcurrentStack<StateSyncItem>[3];
        private ConcurrentStack<StateSyncItem> Stream0 => _nodes[0];
        private ConcurrentStack<StateSyncItem> Stream1 => _nodes[1];
        private ConcurrentStack<StateSyncItem> Stream2 => _nodes[2];

        private object _stateDbLock = new object();
        private object _codeDbLock = new object();
        private static object _nullObject = new object();

        private Dictionary<Keccak, HashSet<DependentItem>> _dependencies = new Dictionary<Keccak, HashSet<DependentItem>>();

        private LruCache<Keccak, object> _alreadySaved = new LruCache<Keccak, object>(1024 * 64);

        public NodeDataDownloader(ISnapshotableDb codeDb, ISnapshotableDb stateDb, ILogManager logManager)
        {
            _codeDb = codeDb ?? throw new ArgumentNullException(nameof(codeDb));
            _stateDb = stateDb ?? throw new ArgumentNullException(nameof(stateDb));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));

            byte[] progress = _codeDb.Get(_fastSyncProgressKey);
            if (progress != null)
            {
                Rlp.DecoderContext context = new Rlp.DecoderContext(progress);
                context.ReadSequenceLength();
                _consumedNodesCount = context.DecodeLong();
                _savedStorageCount = context.DecodeLong();
                _savedStateCount = context.DecodeLong();
                _savedNodesCount = context.DecodeLong();
                _savedAccounts = context.DecodeLong();
                _savedCode = context.DecodeLong();
                _requestedNodesCount = context.DecodeLong();
                _dbChecks = context.DecodeLong();
                _stateWasThere = context.DecodeLong();
                _stateWasNotThere = context.DecodeLong();
            }
        }

        public NodeDataDownloader(ISnapshotableDb codeDb, ISnapshotableDb stateDb, INodeDataRequestExecutor executor, ILogManager logManager)
            : this(codeDb, stateDb, logManager)
        {
            SetExecutor(executor);
        }

        private async Task KeepSyncing(CancellationToken token)
        {
            StateSyncBatch[] dataBatches;
            do
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                dataBatches = PrepareRequests();
                for (int i = 0; i < dataBatches.Length; i++)
                {
                    if (_logger.IsTrace) _logger.Trace($"Sending requests for {dataBatches[i].StateSyncs.Length} nodes");
                    _requestedNodesCount += dataBatches[i].StateSyncs.Length;
                    _networkWatch.Restart();
                    _lastRequest = dataBatches[i].StateSyncs;
                    await _executor.ExecuteRequest(token, dataBatches[i]).ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            _networkWatch.Stop();
                            HandleResponse(t.Result);
                            return;
                        }

                        if (t.IsCanceled)
                        {
                            throw new EthSynchronizationException("Canceled");
                        }

                        if (t.IsFaulted)
                        {
                            if (_logger.IsDebug) _logger.Debug($"Node data request faulted {t.Exception}");
                            throw t.Exception;
                        }

                        if (_logger.IsDebug) _logger.Debug($"Something else happened with node data request");
                    });
                }
            } while (dataBatches.Length != 0);
        }

        private AddNodeResult AddNode(StateSyncItem syncItem, DependentItem dependentItem, string reason, bool missing = false)
        {
            if (!missing)
            {
                if (_alreadySaved.Get(syncItem.Hash) != null)
                {
                    _checkWasCached++;
                    if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadySaved;
                }

                object lockToTake = syncItem.NodeDataType == NodeDataType.Code ? _codeDbLock : _stateDbLock;
                lock (lockToTake)
                {
                    ISnapshotableDb dbToCheck = syncItem.NodeDataType == NodeDataType.Code ? _codeDb : _stateDb;
                    _dbChecks++;
                    bool keyExists = dbToCheck.KeyExists(syncItem.Hash);
                    if (keyExists)
                    {
                        if (_logger.IsTrace) _logger.Trace($"Node already in the DB - skipping {syncItem.Hash}");
                        _alreadySaved.Set(syncItem.Hash, _nullObject);
                        _stateWasThere++;
                        return AddNodeResult.AlreadySaved;
                    }

                    _stateWasNotThere++;
                }

                bool isAlreadyRequested = _dependencies.ContainsKey(syncItem.Hash);
                if (dependentItem != null)
                {
                    if (_logger.IsTrace) _logger.Trace($"Adding dependency {syncItem.Hash} -> {dependentItem.SyncItem.Hash}");
                    AddDependency(syncItem.Hash, dependentItem);
                }

                // same items can have same hashes and we only need them once
                // there is an issue when we have an item, we add it to dependencies, then we request it and the request times out
                // and we never request it again because it was already on the dependencies list
                if (isAlreadyRequested)
                {
                    _checkWasInDependencies++;
                    if (_logger.IsTrace) _logger.Trace($"Node already requested - skipping {syncItem.Hash}");
                    return AddNodeResult.AlreadyRequested;
                }
            }

            PushToSelectedStream(syncItem);
            if (_logger.IsTrace) _logger.Trace($"Added a node {syncItem.Hash} - {reason}");
            return AddNodeResult.Added;
        }

        private StateSyncItem[] _lastRequest;

        private void PushToSelectedStream(StateSyncItem stateSyncItem)
        {
            ConcurrentStack<StateSyncItem> selectedStream;
            if (stateSyncItem.Priority < 0.5f)
            {
                selectedStream = Stream0;
            }
            else if (stateSyncItem.Priority <= 1.5f)
            {
                selectedStream = Stream1;
            }
            else
            {
                selectedStream = Stream2;
            }

            selectedStream.Push(stateSyncItem);
        }

        private void RunChainReaction(Keccak hash)
        {
            if (_dependencies.ContainsKey(hash))
            {
                HashSet<DependentItem> dependentItems = _dependencies[hash];

                if (_logger.IsTrace)
                {
                    string nodeNodes = dependentItems.Count == 1 ? "node" : "nodes";
                    _logger.Trace($"{dependentItems.Count} {nodeNodes} dependent on {hash}");
                }

                foreach (DependentItem dependentItem in dependentItems)
                {
                    dependentItem.Counter--;
                    _dependencies.Remove(hash);
                    if (dependentItem.Counter == 0)
                    {
                        SaveNode(dependentItem.SyncItem, dependentItem.Value);
                    }
                }
            }
            else
            {
                if (_logger.IsTrace) _logger.Trace($"No nodes dependent on {hash}");
            }
        }

        private void SaveNode(StateSyncItem syncItem, byte[] data)
        {
            if (_logger.IsTrace) _logger.Trace($"SAVE {new string('+', syncItem.Level * 2)}{syncItem.NodeDataType.ToString().ToUpperInvariant()} {syncItem.Hash}");
            _savedNodesCount++;
            if (syncItem.NodeDataType == NodeDataType.State)
            {
                _savedStateCount++;
                lock (_stateDbLock)
                {
                    _stateDb.Set(syncItem.Hash, data);
                }
            }

            if (syncItem.NodeDataType == NodeDataType.Storage)
            {
                if (_codesSameAsNodes.Contains(syncItem.Hash))
                {
                    lock (_codeDbLock)
                    {
                        _codeDb.Set(syncItem.Hash, data);
                    }

                    _codesSameAsNodes.Remove(syncItem.Hash);
                }
                
                _savedStorageCount++;
                lock (_stateDbLock)
                {
                    _stateDb.Set(syncItem.Hash, data);
                }
            }
            
            if (syncItem.NodeDataType == NodeDataType.Code)
            {
                _savedCode++;
                lock (_codeDbLock)
                {
                    _codeDb.Set(syncItem.Hash, data);
                }
            }

            if (syncItem.IsRoot)
            {
                if (_dependencies.Count != 0)
                {
                    throw new EthSynchronizationException($"Dependencies hanging after the root node saved - count: {_dependencies.Count}, first: {_dependencies.Keys.First().ToString()}");
                }

                if (TotalCount != 0)
                {
                    throw new EthSynchronizationException($"Nodes left after the root node saved - count: {TotalCount}");
                }
            }

            RunChainReaction(syncItem.Hash);
        }

        private float GetPriority(StateSyncItem parent)
        {
            if (parent.NodeDataType != NodeDataType.State)
            {
                return 0;
            }

            if (parent.Level > maxStateLevel)
            {
                maxStateLevel = parent.Level;
            }

// priority calculation does not make that much sense but the way it works in result
// is very good so not changing it for now
// in particular we should probably calculate priority as 2.5f - 2 * diff
            return Math.Max(1 - (float) parent.Level / maxStateLevel, parent.Priority - (float) parent.Level / maxStateLevel);
        }

        private HashSet<Keccak> _codesSameAsNodes = new HashSet<Keccak>();
        
        private void HandleResponse(StateSyncBatch batch)
        {
            _handleWatch.Restart();
            Interlocked.Add(ref _pendingRequests, -1);
            if (batch.StateSyncs == null)
            {
                throw new EthSynchronizationException("Received a response with a missing request.");
            }

            if (batch.Responses == null)
            {
                throw new EthSynchronizationException("Node sent an empty response");
            }

            if (_logger.IsDebug) _logger.Debug($"Received node data - {batch.Responses.Length} items in response to {batch.StateSyncs.Length}");
            int added = 0;
            for (int i = 0; i < batch.StateSyncs.Length; i++)
            {
                StateSyncItem currentStateSyncItem = batch.StateSyncs[i];
                if (batch.Responses.Length < i + 1)
                {
                    AddNode(currentStateSyncItem, null, "missing", true);
                    continue;
                }

                byte[] currentResponseItem = batch.Responses[i];
                if (currentResponseItem == null)
                {
                    AddNode(batch.StateSyncs[i], null, "missing", true);
                }
                else
                {
                    if (Keccak.Compute(currentResponseItem) != currentStateSyncItem.Hash)
                    {
                        for (int j = 0; j < batch.Responses.Length; j++)
                        {
                            if (batch.Responses[j] == null)
                            {
                                continue;
                            }

                            if (Keccak.Compute(batch.Responses[j]) == batch.StateSyncs[i].Hash)
                            {
                                if (_logger.IsWarn) _logger.Warn($"Invalid data but response[{j}] matches request[{i}]");        
                            }
                        }
                        
                        if (_logger.IsWarn) _logger.Warn($"Peer sent invalid data of length {batch.Responses[i]?.Length} of type {batch.StateSyncs[i].NodeDataType} at level {batch.StateSyncs[i].Level} of type {batch.StateSyncs[i].NodeDataType} Keccak({batch.Responses[i].ToHexString()}) != {batch.StateSyncs[i].Hash}");
                        throw new EthSynchronizationException("Node sent invalid data");
                    }

                    added++;
                    NodeDataType nodeDataType = currentStateSyncItem.NodeDataType;
                    if (nodeDataType == NodeDataType.Code)
                    {
                        SaveNode(currentStateSyncItem, currentResponseItem);
                        continue;
                    }

                    TrieNode trieNode = new TrieNode(NodeType.Unknown, new Rlp(currentResponseItem));
                    trieNode.ResolveNode(null);
                    switch (trieNode.NodeType)
                    {
                        case NodeType.Unknown:
                            throw new InvalidOperationException("Unknown node type");
                        case NodeType.Branch:
                            trieNode.BuildLookupTable();
                            DependentItem dependentBranch = new DependentItem(currentStateSyncItem, currentResponseItem, 0);
                            HashSet<Keccak> alreadyProcessedChildHashes = new HashSet<Keccak>();
                            for (int childIndex = 0; childIndex < 16; childIndex++)
                            {
                                Keccak child = trieNode.GetChildHash(childIndex);
                                if (alreadyProcessedChildHashes.Contains(child))
                                {
                                    continue;
                                }

                                alreadyProcessedChildHashes.Add(child);

                                if (child != null)
                                {
                                    AddNodeResult addChildResult = AddNode(new StateSyncItem(child, nodeDataType, currentStateSyncItem.Level + 1, GetPriority(currentStateSyncItem)), dependentBranch, "branch child");
                                    if (addChildResult != AddNodeResult.AlreadySaved)
                                    {
                                        dependentBranch.Counter++;
                                    }
                                }
                            }

                            if (dependentBranch.Counter == 0)
                            {
                                SaveNode(currentStateSyncItem, currentResponseItem);
                            }

                            break;
                        case NodeType.Extension:
                            Keccak next = trieNode[0].Keccak;
                            if (next != null)
                            {
                                DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, 1);
                                AddNodeResult addResult = AddNode(new StateSyncItem(next, nodeDataType, currentStateSyncItem.Level + 1, currentStateSyncItem.Priority), dependentItem, "extension child");
                                if (addResult == AddNodeResult.AlreadySaved)
                                {
                                    SaveNode(currentStateSyncItem, currentResponseItem);
                                }
                            }
                            else
                            {
                                throw new InvalidOperationException("Not expected Next to be null in the extension");
                            }

                            break;
                        case NodeType.Leaf:
                            if (nodeDataType == NodeDataType.State)
                            {
                                DependentItem dependentItem = new DependentItem(currentStateSyncItem, currentResponseItem, 0);
                                Account account = accountDecoder.Decode(new Rlp.DecoderContext(trieNode.Value));
                                if (account.CodeHash != Keccak.OfAnEmptyString)
                                {
                                    if (account.CodeHash == account.StorageRoot)
                                    {
                                        _codesSameAsNodes.Add(account.CodeHash);
                                    }
                                    else
                                    {
                                        AddNodeResult addCodeResult = AddNode(new StateSyncItem(account.CodeHash, NodeDataType.Code, 0, 0), dependentItem, "code");
                                        if (addCodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;    
                                    }
                                }

                                if (account.StorageRoot != Keccak.EmptyTreeHash)
                                {
                                    AddNodeResult addStorageNodeResult = AddNode(new StateSyncItem(account.StorageRoot, NodeDataType.Storage, 0, 0), dependentItem, "storage");
                                    if (addStorageNodeResult != AddNodeResult.AlreadySaved) dependentItem.Counter++;
                                }

                                if (dependentItem.Counter == 0)
                                {
                                    _savedAccounts++;
                                    SaveNode(currentStateSyncItem, currentResponseItem);
                                }
                            }
                            else
                            {
                                SaveNode(currentStateSyncItem, currentResponseItem);
                            }

                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            lock (_stateDbLock)
            {
                Rlp rlp = Rlp.Encode(
                    Rlp.Encode(_consumedNodesCount),
                    Rlp.Encode(_savedStorageCount),
                    Rlp.Encode(_savedStateCount),
                    Rlp.Encode(_savedNodesCount),
                    Rlp.Encode(_savedAccounts),
                    Rlp.Encode(_savedCode),
                    Rlp.Encode(_requestedNodesCount),
                    Rlp.Encode(_dbChecks),
                    Rlp.Encode(_stateWasThere),
                    Rlp.Encode(_stateWasNotThere));
                lock (_codeDbLock)
                {
                    _codeDb[_fastSyncProgressKey.Bytes] = rlp.Bytes;
                    _codeDb.Commit();
                    _stateDb.Commit();
                }

                _lastRequest = null;
            }

            Interlocked.Add(ref _consumedNodesCount, added);
            if (added == 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Peer sent no data in response to a request of length {batch.StateSyncs.Length}");
                throw new EthSynchronizationException("Node sent no data");
            }

            if (_requestedNodesCount > _lastRequestedNodesCount + 5000)
            {
                _lastRequestedNodesCount = _requestedNodesCount;
                if (_logger.IsInfo) _logger.Info($"Saved nodes {_savedNodesCount} / requested {_requestedNodesCount} ({(decimal) _savedNodesCount / _requestedNodesCount:P2}), saved accounts {_savedAccounts}, enqueued nodes {Stream0.Count:D5}|{Stream1.Count:D5}|{Stream2.Count:D5}");
                if (_logger.IsTrace) _logger.Trace($"Requested {_requestedNodesCount}, consumed {_consumedNodesCount}, missed {_requestedNodesCount - _consumedNodesCount}, {_savedCode} contracts, {_savedStateCount - _savedAccounts} states, {_savedStorageCount} storage, DB checks {_stateWasThere}/{_stateWasNotThere + _stateWasThere} cached({_checkWasCached}+{_checkWasInDependencies})");
                if (_logger.IsTrace) _logger.Trace($"Consume : {(decimal) _consumedNodesCount / _requestedNodesCount:p2}, Save : {(decimal) _savedNodesCount / _requestedNodesCount:p2}, DB Reads : {(decimal) _dbChecks / _requestedNodesCount:p2}");
            }

            _handleWatch.Stop();
            long total = _prepareWatch.ElapsedMilliseconds + _handleWatch.ElapsedMilliseconds + _networkWatch.ElapsedMilliseconds;
            if (total != 0)
            {
                // calculate averages
                if(_logger.IsDebug) _logger.Debug($"Prepare {_prepareWatch.ElapsedMilliseconds} ({(decimal) _prepareWatch.ElapsedMilliseconds / total:P0}) - Request {_networkWatch.ElapsedMilliseconds} ({(decimal) _networkWatch.ElapsedMilliseconds / total:P0}) - Handle {_handleWatch.ElapsedMilliseconds} ({(decimal) _handleWatch.ElapsedMilliseconds / total:P0})");
            }
        }

        private class DependentItemComparer : IEqualityComparer<DependentItem>
        {
            public bool Equals(DependentItem x, DependentItem y)
            {
                return x?.SyncItem.Hash == y?.SyncItem.Hash;
            }

            public int GetHashCode(DependentItem obj)
            {
                return obj?.SyncItem.Hash.GetHashCode() ?? 0;
            }
        }

        private void AddDependency(Keccak dependency, DependentItem dependentItem)
        {
            if (!_dependencies.ContainsKey(dependency))
            {
                _dependencies[dependency] = new HashSet<DependentItem>(new DependentItemComparer());
            }

            _dependencies[dependency].Add(dependentItem);
        }

        private bool TryTake(out StateSyncItem node)
        {
            if (!Stream0.TryPop(out node))
            {
                if (!Stream1.TryPop(out node))
                {
                    return Stream2.TryPop(out node);
                }
            }

            return true;
        }

        private StateSyncBatch[] PrepareRequests()
        {
            _prepareWatch.Reset();
            List<StateSyncBatch> requests = new List<StateSyncBatch>();
            if (_logger.IsDebug) _logger.Debug($"Preparing requests from ({Stream0.Count}|{Stream1.Count}|{Stream2.Count}) nodes  - pending requests {_pendingRequests}");

            // re-add the pending request
            if (_lastRequest != null)
            {
                for (int i = 0; i < _lastRequest.Length; i++)
                {
                    AddNode(_lastRequest[i], null, "last request", true);
                }
            }

            while (TotalCount != 0 && _pendingRequests + requests.Count < MaxPendingRequestsCount)
            {
                StateSyncBatch batch = new StateSyncBatch();
                List<StateSyncItem> requestHashes = new List<StateSyncItem>();
                for (int i = 0; i < MaxRequestSize; i++)
                {
                    if (TryTake(out StateSyncItem result))
                    {
                        if (_logger.IsTrace) _logger.Trace($"Requesting {result.Hash}");
                        requestHashes.Add(result);
                    }
                    else
                    {
                        break;
                    }
                }

                batch.StateSyncs = requestHashes.ToArray();
                requests.Add(batch);
            }

            var requestsArray = requests.ToArray();
            Interlocked.Add(ref _pendingRequests, requestsArray.Length);
            _prepareWatch.Stop();
            return requestsArray;
        }

        public async Task<long> SyncNodeData(CancellationToken token, Keccak rootNode)
        {
            if (_rootNode != rootNode || _pendingRequests == 1)
            {
                _rootNode = rootNode;
                _dependencies.Clear();
                _lastRequest = null;
                _codesSameAsNodes.Clear();
                _nodes[0]?.Clear();
                _nodes[1]?.Clear();
                _nodes[2]?.Clear();
                _pendingRequests = 0;
            }

            if (_nodes[0] == null)
            {
                _nodes[0] = new ConcurrentStack<StateSyncItem>();
                _nodes[1] = new ConcurrentStack<StateSyncItem>();
                _nodes[2] = new ConcurrentStack<StateSyncItem>();
            }

            if (rootNode == Keccak.EmptyTreeHash)
            {
                return _consumedNodesCount;
            }

            bool hasOnlyRootNode = false;
            if (TotalCount == 1)
            {
                Stream0.TryPeek(out StateSyncItem node0);
                Stream1.TryPeek(out StateSyncItem node1);
                Stream2.TryPeek(out StateSyncItem node2);
                if ((node0 ?? node1 ?? node2).Hash == rootNode)
                {
                    hasOnlyRootNode = true;
                }
            }

            if (!hasOnlyRootNode)
            {
                AddNode(new StateSyncItem(rootNode, NodeDataType.State, 0, 1) {IsRoot = true}, null, "initial");
            }

            await KeepSyncing(token);
            return _consumedNodesCount;
        }

        public void SetExecutor(INodeDataRequestExecutor executor)
        {
            if (_logger.IsTrace) _logger.Trace($"Setting request executor to {executor.GetType().Name}");
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public bool IsFullySynced(Keccak bestSuggestedStateRoot)
        {
            lock (_stateDbLock)
            {
                return _stateDb.Get(bestSuggestedStateRoot) != null;
            }
        }

        private enum AddNodeResult
        {
            AlreadySaved,
            AlreadyRequested,
            Added
        }

        [DebuggerDisplay("{SyncItem.Hash} {Counter}")]
        private class DependentItem
        {
            public StateSyncItem SyncItem { get; set; }
            public byte[] Value { get; }
            public int Counter { get; set; }

            public DependentItem(StateSyncItem syncItem, byte[] value, int counter)
            {
                SyncItem = syncItem;
                Value = value;
                Counter = counter;
            }
        }
    }
}