﻿using Dotmim.Sync.Args;
using Dotmim.Sync.Batch;
using Dotmim.Sync.Builders;
using Dotmim.Sync.Enumerations;
using Dotmim.Sync.Serialization;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;


namespace Dotmim.Sync
{
    public partial class RemoteOrchestrator : BaseOrchestrator
    {


        /// <summary>
        /// Get a snapshot
        /// </summary>
        public virtual async Task<(SyncContext context, long RemoteClientTimestamp, BatchInfo ServerBatchInfo, DatabaseChangesSelected DatabaseChangesSelected)>
            GetSnapshotAsync(ServerScopeInfo serverScopeInfo, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            var context = new SyncContext(Guid.NewGuid(), serverScopeInfo.Name);

            try
            {
                await using var runner = await this.GetConnectionAsync(context, SyncMode.Reading, SyncStage.ScopeLoading, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                long remoteClientTimestamp;
                BatchInfo serverBatchInfo;
                DatabaseChangesSelected databaseChangesSelected;

                (context, remoteClientTimestamp, serverBatchInfo, databaseChangesSelected) =
                    await this.InternalGetSnapshotAsync(serverScopeInfo, context, runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return (context, remoteClientTimestamp, serverBatchInfo, databaseChangesSelected);

            }
            catch (Exception ex)
            {
                throw GetSyncError(context, ex);
            }
        }


        /// <summary>
        /// Get a snapshot
        /// </summary>
        public virtual async Task<(SyncContext context, long RemoteClientTimestamp, BatchInfo ServerBatchInfo, DatabaseChangesSelected DatabaseChangesSelected)>
            InternalGetSnapshotAsync(ServerScopeInfo serverScopeInfo, SyncContext context, DbConnection connection = default, DbTransaction transaction = default, CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            await using var runner = await this.GetConnectionAsync(context, SyncMode.Reading, SyncStage.ScopeLoading, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            // Get context or create a new one
            var changesSelected = new DatabaseChangesSelected();

            BatchInfo serverBatchInfo = null;
            if (string.IsNullOrEmpty(this.Options.SnapshotsDirectory))
                return (context, 0, null, changesSelected);

            //Direction set to Download
            context.SyncWay = SyncWay.Download;

            if (cancellationToken.IsCancellationRequested)
                cancellationToken.ThrowIfCancellationRequested();

            // Get Schema from remote provider if no schema passed from args
            if (serverScopeInfo.Schema == null)
            {
                (context, serverScopeInfo) = await this.InternalGetServerScopeInfoAsync(context, serverScopeInfo.Setup, runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);
            }

            // When we get the changes from server, we create the batches if it's requested by the client
            // the batch decision comes from batchsize from client
            var (rootDirectory, nameDirectory) = await this.InternalGetSnapshotDirectoryAsync(serverScopeInfo.Name, default, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

            if (!string.IsNullOrEmpty(rootDirectory))
            {
                var directoryFullPath = Path.Combine(rootDirectory, nameDirectory);

                // if no snapshot present, just return null value.
                if (Directory.Exists(directoryFullPath))
                {
                    // Serialize on disk.
                    var jsonConverter = new Serialization.JsonConverter<BatchInfo>();

                    var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

                    using (var fs = new FileStream(summaryFileName, FileMode.Open, FileAccess.Read))
                    {
                        serverBatchInfo = await jsonConverter.DeserializeAsync(fs).ConfigureAwait(false);
                    }

                    // Create the schema changeset
                    var changesSet = new SyncSet();

                    // Create a Schema set without readonly columns, attached to memory changes
                    foreach (var table in serverScopeInfo.Schema.Tables)
                    {
                        DbSyncAdapter.CreateChangesTable(serverScopeInfo.Schema.Tables[table.TableName, table.SchemaName], changesSet);

                        // Get all stats about this table
                        var bptis = serverBatchInfo.BatchPartsInfo.SelectMany(bpi => bpi.Tables.Where(t =>
                        {
                            var sc = SyncGlobalization.DataSourceStringComparison;

                            var sn = t.SchemaName == null ? string.Empty : t.SchemaName;
                            var otherSn = table.SchemaName == null ? string.Empty : table.SchemaName;

                            return table.TableName.Equals(t.TableName, sc) && sn.Equals(otherSn, sc);

                        }));

                        if (bptis != null)
                        {
                            // Statistics
                            var tableChangesSelected = new TableChangesSelected(table.TableName, table.SchemaName)
                            {
                                // we are applying a snapshot where it can't have any deletes, obviously
                                Upserts = bptis.Sum(bpti => bpti.RowsCount)
                            };

                            if (tableChangesSelected.Upserts > 0)
                                changesSelected.TableChangesSelected.Add(tableChangesSelected);
                        }


                    }
                    serverBatchInfo.SanitizedSchema = changesSet;
                }
            }
            if (serverBatchInfo == null)
                return (context, 0, null, changesSelected);


            await runner.CommitAsync().ConfigureAwait(false);

            return (context, serverBatchInfo.Timestamp, serverBatchInfo, changesSelected);

        }


        public virtual Task<(SyncContext context, BatchInfo batchInfo)> CreateSnapshotAsync(SyncSetup setup = null, SyncParameters syncParameters = null,
            DbConnection connection = default, DbTransaction transaction = default,
            CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
            => CreateSnapshotAsync(SyncOptions.DefaultScopeName, setup, syncParameters, connection, transaction, cancellationToken, progress);

        /// <summary>
        /// Create a snapshot, based on the Setup object. 
        /// </summary>
        /// <param name="syncParameters">if not parameters are found in the SyncContext instance, will use thes sync parameters instead</param>
        /// <returns>Instance containing all information regarding the snapshot</returns>
        public virtual async Task<(SyncContext context, BatchInfo batchInfo)> CreateSnapshotAsync(string scopeName, SyncSetup setup = null, SyncParameters syncParameters = null,
            DbConnection connection = default, DbTransaction transaction = default,
            CancellationToken cancellationToken = default, IProgress<ProgressArgs> progress = null)
        {
            var context = new SyncContext(Guid.NewGuid(), scopeName);

            try
            {
                if (string.IsNullOrEmpty(this.Options.SnapshotsDirectory) || this.Options.BatchSize <= 0)
                    throw new SnapshotMissingMandatariesOptionsException();

                await using var runner = await this.GetConnectionAsync(context, SyncMode.Writing, SyncStage.SnapshotCreating, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

                // check parameters
                // If context has no parameters specified, and user specifies a parameter collection we switch them
                if ((context.Parameters == null || context.Parameters.Count <= 0) && syncParameters != null && syncParameters.Count > 0)
                    context.Parameters = syncParameters;

                // 1) Get Schema from remote provider
                ServerScopeInfo serverScopeInfo;
                (context, serverScopeInfo) = await this.InternalGetServerScopeInfoAsync(context, setup, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                // If we just have create the server scope, we need to provision it
                if (serverScopeInfo != null && serverScopeInfo.IsNewScope)
                {
                    // 2) Provision
                    var provision = SyncProvision.TrackingTable | SyncProvision.StoredProcedures | SyncProvision.Triggers;

                    await this.InternalProvisionAsync(serverScopeInfo, context, false, provision, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);

                    // Write scopes locally
                    (context, serverScopeInfo) = await this.InternalSaveServerScopeInfoAsync(serverScopeInfo, context, runner.Connection, runner.Transaction, cancellationToken, progress).ConfigureAwait(false);
                }

                // 4) Getting the most accurate timestamp
                long remoteClientTimestamp;
                (context, remoteClientTimestamp) = await this.InternalGetLocalTimestampAsync(context,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                // 5) Create the snapshot with
                BatchInfo batchInfo;

                (context, batchInfo) = await this.InternalCreateSnapshotAsync(serverScopeInfo, context, remoteClientTimestamp,
                    runner.Connection, runner.Transaction, runner.CancellationToken, runner.Progress).ConfigureAwait(false);

                await runner.CommitAsync().ConfigureAwait(false);

                return (context, batchInfo);
            }
            catch (Exception ex)
            {
                throw GetSyncError(context, ex);
            }

        }

        internal virtual async Task<(SyncContext context, BatchInfo batchInfo)>
            InternalCreateSnapshotAsync(ServerScopeInfo serverScopeInfo, SyncContext context,
              long remoteClientTimestamp, DbConnection connection, DbTransaction transaction,
              CancellationToken cancellationToken, IProgress<ProgressArgs> progress = null)
        {
            await this.InterceptAsync(new SnapshotCreatingArgs(context, serverScopeInfo.Schema, this.Options.SnapshotsDirectory, this.Options.BatchSize, remoteClientTimestamp, this.Provider.CreateConnection(), null), progress, cancellationToken).ConfigureAwait(false);

            if (!Directory.Exists(this.Options.SnapshotsDirectory))
                Directory.CreateDirectory(this.Options.SnapshotsDirectory);

            var (rootDirectory, nameDirectory) = await this.InternalGetSnapshotDirectoryAsync(serverScopeInfo.Name, context.Parameters, cancellationToken, progress).ConfigureAwait(false);

            // create local directory with scope inside
            if (!Directory.Exists(rootDirectory))
                Directory.CreateDirectory(rootDirectory);

            // Delete directory if already exists
            var directoryFullPath = Path.Combine(rootDirectory, nameDirectory);

            // Delete old version if exists
            if (Directory.Exists(directoryFullPath))
                Directory.Delete(directoryFullPath, true);

            BatchInfo serverBatchInfo;

            (context, serverBatchInfo, _) =
                    await this.InternalGetChangesAsync(serverScopeInfo, context, true, null, Guid.Empty,
                    this.Provider.SupportsMultipleActiveResultSets,
                    rootDirectory, nameDirectory, connection, transaction, cancellationToken, progress).ConfigureAwait(false);

            // since we explicitely defined remote client timestamp to null, to get all rows, just reaffect here
            serverBatchInfo.Timestamp = remoteClientTimestamp;

            // Serialize on disk.
            var jsonConverter = new Serialization.JsonConverter<BatchInfo>();

            var summaryFileName = Path.Combine(directoryFullPath, "summary.json");

            using (var f = new FileStream(summaryFileName, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                var bytes = await jsonConverter.SerializeAsync(serverBatchInfo).ConfigureAwait(false);
                f.Write(bytes, 0, bytes.Length);
            }

            await this.InterceptAsync(new SnapshotCreatedArgs(context, serverBatchInfo, this.Provider.CreateConnection(), null), progress, cancellationToken).ConfigureAwait(false);

            return (context, serverBatchInfo);
        }


    }
}
