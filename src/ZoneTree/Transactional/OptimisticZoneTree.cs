﻿using Tenray;
using ZoneTree.Collections;
using ZoneTree.Core;
using ZoneTree.Serializers;

namespace ZoneTree.Transactional;

public sealed class OptimisticZoneTree<TKey, TValue> : ITransactionalZoneTree<TKey, TValue>
{
    public IZoneTree<TKey, TValue> ZoneTree { get; }

    const string TxStampRecordCategory = "txs";

    readonly ZoneTreeOptions<TKey, TValue> Options;

    readonly ITransactionManager TransactionManager;

    readonly IncrementalIdProvider IdProvider = new ();

    readonly DictionaryWithWAL<TKey, ReadWriteStamp> ReadWriteStamps;

    readonly Dictionary<long, OptimisticTransaction<TKey, TValue>> OptimisticTransactions = new();

    public OptimisticZoneTree(
        ZoneTreeOptions<TKey, TValue> options,
        ITransactionManager transactionManager)
    {
        Options = options;
        TransactionManager = transactionManager;
        ZoneTree = new ZoneTree<TKey, TValue>(options);        
        ReadWriteStamps = new(
            0,
            TxStampRecordCategory,
            options.WriteAheadLogProvider,
            options.KeySerializer,
            new StructSerializer<ReadWriteStamp>(),
            options.Comparer,
            (in ReadWriteStamp x) => x.IsDeleted,
            (ref ReadWriteStamp x) => x = default);
    }

    OptimisticTransaction<TKey, TValue> GetOrCreateTransaction(long transactionId)
    {
        var state = TransactionManager.GetTransactionState(transactionId);
        if (state == TransactionState.Aborted)
            throw new TransactionIsAbortedException(transactionId);

        if (state == TransactionState.Committed)
            throw new TransactionIsAlreadyCommittedException(transactionId);

        if (OptimisticTransactions.TryGetValue(transactionId, out var transaction))
            return transaction;

        transaction = new OptimisticTransaction<TKey, TValue>(transactionId, Options);
        OptimisticTransactions.Add(transactionId, transaction);
        return transaction;
    }

    /// <summary>
    /// Deletes the transaction instance from memory and disposes the resources.
    /// Transaction Manager holds the permanenet status of the transaction.
    /// </summary>
    /// <param name="transaction"></param>
    void DeleteTransaction(OptimisticTransaction<TKey, TValue> transaction)
    {
        OptimisticTransactions.Remove(transaction.TransactionId);
        transaction.Dispose();
    }

    public long BeginTransaction()
    {
        var transactionId = IdProvider.NextId();
        TransactionManager.TransactionStarted(transactionId);
        return transactionId;
    }

    public void Rollback(long transactionId)
    {
        lock (this)
        {
            var transaction = GetOrCreateTransaction(transactionId);
            AbortTransaction(transaction);
        }
    }

    void AbortTransaction(OptimisticTransaction<TKey, TValue> transaction)
    {
        var transactionId = transaction.TransactionId;
        /*
         For each oldOj, oldWTS(Oj) in OLD(Ti)
            if WTS(Oj) equals TS(Ti) then restore Oj = oldOj and WTS(Oj) = oldWTS(Oj)
         */
        foreach (var item in transaction.OldValueEnumerable)
        {
            var key = item.Key;
            var oldValue = item.Value.Value1;
            var oldWriteStamp = item.Value.Value2;
            ReadWriteStamps.TryGetValue(key, out var readWriteStamp);
            if (readWriteStamp.WriteStamp != transactionId)
                continue;
            readWriteStamp.WriteStamp = oldWriteStamp;
            ReadWriteStamps.Upsert(key, readWriteStamp);
            ZoneTree.Upsert(key, oldValue);
        }
        DeleteTransaction(transaction);
        TransactionManager.TransactionAborted(transactionId);
    }

    public CommitResult Prepare(long transactionId)
    {
        lock (this)
        {
            return DoPrepare(transactionId);
        }
    }

    public CommitResult PrepareAndCommit(long transactionId)
    {
        lock (this)
        {
            var result = DoPrepare(transactionId);
            if (result.IsReadyToCommit)
                return DoCommit(transactionId);
            return result;
        }
    }

    public CommitResult Commit(long transactionId)
    {
        lock (this)
        {
            return DoCommit(transactionId);
        }
    }

    CommitResult DoCommit(long transactionId)
    {
        var transaction = GetOrCreateTransaction(transactionId);
        if (!transaction.IsReadyToCommit)
            throw new TransactionIsNotReadyToCommitException(transactionId);
        DeleteTransaction(transaction);
        TransactionManager.TransactionCommitted(transactionId);
        return CommitResult.Committed;
    }

    CommitResult DoPrepare(long transactionId)
    {
        var transaction = GetOrCreateTransaction(transactionId);
        var dependencies = transaction.GetDependencyList();
        var waitList = new List<long>();
        foreach (var dependency in dependencies)
        {
            var state = TransactionManager.GetTransactionState(transactionId);
            if (state == TransactionState.Aborted)
            {
                // If there is a transaction in DEP(Ti) that aborted then abort
                AbortTransaction(transaction);
                throw new TransactionIsAbortedException(transactionId);
            }
            if (state == TransactionState.Uncommitted)
                waitList.Add(dependency);
        }

        if (waitList.Count == 0)
        {
            transaction.IsReadyToCommit = true;
            return CommitResult.ReadyToCommit;
        }

        // While there is a transaction DEP(Ti) that has not ended: wait
        return new CommitResult(
            CommitState.PendingTransactions,
            waitList);
    }

    public bool ContainsKey(long transactionId, in TKey key)
    {
        lock (this)
        {
            var transaction = GetOrCreateTransaction(transactionId);
            var hasReadWriteStamp = ReadWriteStamps.TryGetValue(key, out var readWriteStamp);
            var hasKey = ZoneTree.ContainsKey(in key);
            if (transaction.HandleReadKey(ref readWriteStamp) == OptimisticReadAction.Abort)
            {
                AbortTransaction(transaction);
                throw new TransactionIsAbortedException(transactionId);
            }
            ReadWriteStamps.Upsert(key, in readWriteStamp);
            return hasKey;
        }
    }

    public bool TryGet(long transactionId, in TKey key, out TValue value)
    {
        lock (this)
        {
            var transaction = GetOrCreateTransaction(transactionId);
            var hasReadWriteStamp = ReadWriteStamps.TryGetValue(key, out var readWriteStamp);
            var hasValue = ZoneTree.TryGet(in key, out value);
            if (transaction.HandleReadKey(ref readWriteStamp) == OptimisticReadAction.Abort)
            {
                AbortTransaction(transaction);
                throw new TransactionIsAbortedException(transactionId);
            }

            ReadWriteStamps.Upsert(key, in readWriteStamp);
            return hasValue;
        }
    }

    public bool Upsert(long transactionId, in TKey key, in TValue value)
    {
        lock (this)
        {
            var transaction = GetOrCreateTransaction(transactionId);
            var hasReadWriteStamp = ReadWriteStamps.TryGetValue(key, out var readWriteStamp);
            var hasOldValue = ZoneTree.TryGet(in key, out var oldValue);

            var action = transaction.HandleWriteKey(
                ref readWriteStamp,
                in key,
                hasOldValue,
                in oldValue);

            // skip the write based on Thomas Write Rule.
            if (action == OptimisticWriteAction.SkipWrite)
                return !hasOldValue;

            // abort case
            if (action == OptimisticWriteAction.Abort)
            {
                AbortTransaction(transaction);
                throw new TransactionIsAbortedException(transactionId);
            }

            // actual write happens.
            ReadWriteStamps.Upsert(key, in readWriteStamp);
            ZoneTree.Upsert(in key, in value);
            return !hasOldValue;
        }
    }

    public void Delete(long transactionId, in TKey key)
    {
        lock (this)
        {
            var transaction = GetOrCreateTransaction(transactionId);
            var hasReadWriteStamp = ReadWriteStamps.TryGetValue(key, out var readWriteStamp);
            var hasOldValue = ZoneTree.TryGet(in key, out var oldValue);

            var action = transaction.HandleWriteKey(
               ref readWriteStamp,
               in key,
               hasOldValue,
               in oldValue);

            // skip the write based on Thomas Write Rule.
            if (action == OptimisticWriteAction.SkipWrite)
                return;

            // abort case
            if (action == OptimisticWriteAction.Abort)
            {
                AbortTransaction(transaction);
                throw new TransactionIsAbortedException(transactionId);
            }
            ReadWriteStamps.Upsert(key, in readWriteStamp);
            ZoneTree.ForceDelete(in key);
        }
    }

    public void Dispose()
    {
        foreach (var transaction in OptimisticTransactions.Values.ToArray())
        {
            transaction.Dispose();
        }
        ZoneTree.Dispose();
        ReadWriteStamps.Dispose();
    }
}