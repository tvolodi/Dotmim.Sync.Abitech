﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text;
using System.Threading.Tasks;

namespace Dotmim.Sync.Builders
{
    public abstract class DbBuilder
    {
        /// <summary>
        /// First step before creating schema
        /// </summary>
        public abstract Task EnsureDatabaseAsync(DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// First step before creating schema
        /// </summary>
        public abstract Task<SyncTable> EnsureTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Get all tables with column names from a database
        /// </summary>
        public abstract Task<SyncSetup> GetAllTablesAsync(DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Make a hello test on the current database
        /// </summary>
        public abstract Task<(string DatabaseName, string Version)> GetHelloAsync(DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Get a table with all rows from a table
        /// </summary>
        public abstract Task<SyncTable> GetTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Get a table definition
        /// </summary>
        public abstract Task<SyncTable> GetTableDefinitionAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Get a table columns definition
        /// </summary>
        public abstract Task<SyncTable> GetTableColumnsAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Check if a table exists
        /// </summary>
        public abstract Task<bool> ExistsTableAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Drops a table if exists
        /// </summary>
        public abstract Task DropsTableIfExistsAsync(string tableName, string schemaName, DbConnection connection, DbTransaction transaction = null);

        /// <summary>
        /// Rename a table
        /// </summary>
        public abstract Task RenameTableAsync(string tableName, string schemaName, string newTableName, string newSchemaName, DbConnection connection, DbTransaction transaction = null);

    }
}
