﻿using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;

namespace Gridsum.DataflowEx.Databases
{
    using System;
    using System.Threading;
    using Common.Logging;

    /// <summary>
    /// The class helps you to bulk insert strongly typed objects to the sql server database. 
    /// It generates object-relational mappings automatically according to the given DBColumnMappings.
    /// </summary>
    /// <typeparam name="T">The db-mapped type of objects</typeparam>
    public class DbBulkInserter<T> : Dataflow<T>, IBatchedDataflow where T : class
    {
        protected readonly TargetTable m_targetTable;
        protected readonly TypeAccessor<T> m_typeAccessor;
        protected readonly int m_bulkSize;
        protected readonly string m_dbBulkInserterName;
        protected readonly PostBulkInsertDelegate<T> m_postBulkInsert;
        protected readonly BatchBlock<T> m_batchBlock;
        protected readonly ActionBlock<T[]> m_actionBlock;
        protected readonly ILog m_logger;
        protected Timer m_timer;

        /// <summary>
        /// Constructs an instance of DbBulkInserter
        /// </summary>
        /// <param name="connectionString">The connection string to the output database</param>
        /// <param name="destTable">The table name in database to bulk insert into</param>
        /// <param name="options">Options to use for this dataflow</param>
        /// <param name="destLabel">The mapping label to help choose among all column mappings</param>
        /// <param name="bulkSize">The bulk size to insert in a batch. Default to 8192.</param>
        /// <param name="dbBulkInserterName">A given name of this bulk inserter (would be nice for logging)</param>
        /// <param name="postBulkInsert">A delegate that enables you to inject some customized work whenever a bulk insert is done</param>
        public DbBulkInserter(
            string connectionString,
            string destTable,
            DataflowOptions options,
            string destLabel,
            int bulkSize = 4096 * 2,
            string dbBulkInserterName = null,
            PostBulkInsertDelegate<T> postBulkInsert = null)
            : this(new TargetTable(destLabel, connectionString, destTable), options, bulkSize, dbBulkInserterName, postBulkInsert)
        {
        }

        /// <summary>
        /// Constructs an instance of DbBulkInserter
        /// </summary>
        /// <param name="targetTable">Information about the database target and mapping label</param>
        /// <param name="options">Options to use for this dataflow</param>
        /// <param name="bulkSize">The bulk size to insert in a batch. Default to 8192.</param>
        /// <param name="dbBulkInserterName">A given name of this bulk inserter (would be nice for logging)</param>
        /// <param name="postBulkInsert">A delegate that enables you to inject some customized work whenever a bulk insert is done</param>
        public DbBulkInserter(TargetTable targetTable, 
            DataflowOptions options, 
            int bulkSize = 4096 * 2, 
            string dbBulkInserterName = null,
            PostBulkInsertDelegate<T> postBulkInsert = null) 
            : base(options)
        {
            m_targetTable = targetTable;
            m_typeAccessor = TypeAccessorManager<T>.GetAccessorForTable(targetTable);
            m_bulkSize = bulkSize;
            m_dbBulkInserterName = dbBulkInserterName;
            m_postBulkInsert = postBulkInsert;
            m_batchBlock = new BatchBlock<T>(bulkSize, options.ToGroupingBlockOption());
            
            var bulkInsertOption = options.ToExecutionBlockOption();
            //Action block deal with array references
            if (bulkInsertOption.BoundedCapacity != DataflowBlockOptions.Unbounded)
            {
                bulkInsertOption.BoundedCapacity = bulkInsertOption.BoundedCapacity / bulkSize;
            }

            m_actionBlock = new ActionBlock<T[]>(array => this.DumpToDBAsync(array, targetTable), bulkInsertOption);
            m_batchBlock.LinkTo(m_actionBlock, m_defaultLinkOption);
            m_logger = Utils.GetNamespaceLogger();

            RegisterChild(m_batchBlock);
            RegisterChild(m_actionBlock);

            m_timer = new Timer(
                state =>
                    {
                        this.TriggerBatch();
                    }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

            
        }

        protected async virtual Task DumpToDBAsync(T[] data, TargetTable targetTable)
        {
            m_logger.Debug(h => h("{3} starts bulk-inserting {0} {1} to db table {2}", data.Length, typeof(T).Name, targetTable.TableName, this.FullName));

            using (var bulkReader = new BulkDataReader<T>(m_typeAccessor, data))
            {
                using (var conn = new SqlConnection(targetTable.ConnectionString))
                {
                    await conn.OpenAsync().ConfigureAwait(false);

                    var transaction = conn.BeginTransaction();
                    try
                    {
                        using (var bulkCopy = new SqlBulkCopy(conn, SqlBulkCopyOptions.TableLock, transaction))
                        {
                            foreach (SqlBulkCopyColumnMapping map in bulkReader.ColumnMappings)
                            {
                                bulkCopy.ColumnMappings.Add(map);
                            }

                            bulkCopy.DestinationTableName = targetTable.TableName;
                            bulkCopy.BulkCopyTimeout = (int)TimeSpan.FromMinutes(30).TotalMilliseconds;
                            bulkCopy.BatchSize = m_bulkSize;

                            // Write from the source to the destination.
                            await bulkCopy.WriteToServerAsync(bulkReader).ConfigureAwait(false);
                        }
                    }
                    catch (Exception e)
                    {
                        if (e is NullReferenceException)
                        {
                            m_logger.ErrorFormat(
                                "{0} NullReferenceException occurred in bulk insertion. This is probably caused by forgetting assigning value to a [NoNullCheck] attribute when constructing your object.", this.FullName);
                        }

                        m_logger.ErrorFormat("{0} Bulk insertion failed. Rolling back all changes...", this.FullName, e);
                        transaction.Rollback();
                        m_logger.InfoFormat("{0} Changes successfully rolled back", this.FullName);

                        //As this is an unrecoverable exception, rethrow it
                        throw new AggregateException(e);
                    }
                    
                    transaction.Commit();
                    await this.OnPostBulkInsert(conn, targetTable, data).ConfigureAwait(false);
                }
            }

            m_logger.Info(h => h("{3} bulk-inserted {0} {1} to db table {2}", data.Length, typeof(T).Name, targetTable.TableName, this.FullName));
        }

        /// <summary>
        /// See <see cref="Dataflow{T}.InputBlock"/>
        /// </summary>
        public override ITargetBlock<T> InputBlock { get { return m_batchBlock; } }

        /// <summary>
        /// See <see cref="Dataflow{T}.Name"/>
        /// </summary>
        public override string Name
        {
            get {
                return m_dbBulkInserterName ?? base.Name;
            }
        }

        /// <summary>
        /// The internal property-column mapper used by this bulk inserter
        /// </summary>
        public TypeAccessor<T> TypeAccessor
        {
            get
            {
                return m_typeAccessor;
            }
        }

        /// <summary>
        /// See <see cref="Dataflow{T}.BufferStatus"/>
        /// </summary>
        public override Tuple<int, int> BufferStatus
        {
            get
            {
                var bs = base.BufferStatus;
                return new Tuple<int, int>(bs.Item1 * m_bulkSize, bs.Item2);
            }
        }

        protected virtual async Task OnPostBulkInsert(SqlConnection sqlConnection, TargetTable target, T[] insertedData)
        {
            if (m_postBulkInsert != null)
            {
                await m_postBulkInsert(sqlConnection, m_targetTable, insertedData).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Explicitly triggers a bulk insert immediately even if the internal buffer has fewer items than the given bulk size
        /// </summary>
        public void TriggerBatch()
        {
            m_batchBlock.TriggerBatch();
        }
    }

    /// <summary>
    /// The handler which allows you to take control after a bulk insertion succeeds. (e.g. you may want to 
    /// execute a stored prodecure after every bulk insertion)
    /// </summary>
    /// <param name="connection">The connection used by previous bulk insert (already opened)</param>
    /// <param name="target">The destination table of the bulk insertion</param>
    /// <param name="insertedData">The inserted data of this round of bulk insertion</param>
    /// <returns>A task represents the state of the post bulk insert job (so you can use await in the delegate)</returns>
    public delegate Task PostBulkInsertDelegate<T>(SqlConnection connection, TargetTable target, ICollection<T> insertedData);
}
