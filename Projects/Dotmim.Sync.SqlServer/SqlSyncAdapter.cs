﻿using Dotmim.Sync.Builders;
using Dotmim.Sync.SqlServer.Manager;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient.Server;

namespace Dotmim.Sync.SqlServer.Builders
{
    public class SqlSyncAdapter : DbSyncAdapter
    {

        // Derive Parameters cache
        // Be careful, we can have collision between databases
        // this static class could be shared accross databases with same command name
        // but different table schema
        // So the string should contains the connection string as well
        private static ConcurrentDictionary<string, List<SqlParameter>> derivingParameters
            = new ConcurrentDictionary<string, List<SqlParameter>>();

        public static DateTime SqlDateMin = new DateTime(1753, 1, 1);

        public SqlObjectNames SqlObjectNames { get; set; }
        public SqlDbMetadata SqlMetadata { get; set; }

        public SqlSyncAdapter(SyncTable tableDescription, ParserName tableName, ParserName trackingName, SyncSetup setup) : base(tableDescription, setup)
        {
            this.SqlObjectNames = new SqlObjectNames(tableDescription, tableName, trackingName, setup);
            this.SqlMetadata = new SqlDbMetadata();
        }

        private SqlMetaData GetSqlMetadaFromType(SyncColumn column)
        {
            long maxLength = column.MaxLength;
            var dataType = column.GetDataType();
            var dbType = column.GetDbType();

            var sqlDbType = this.TableDescription.OriginalProvider == SqlSyncProvider.ProviderType ?
                this.SqlMetadata.GetSqlDbType(column) : this.SqlMetadata.GetOwnerDbTypeFromDbType(column);

            // Since we validate length before, it's not mandatory here.
            // let's say.. just in case..
            if (sqlDbType == SqlDbType.VarChar || sqlDbType == SqlDbType.NVarChar)
            {
                // set value for (MAX) 
                maxLength = maxLength <= 0 ? SqlMetaData.Max : maxLength;

                // If max length is specified (not (MAX) )
                if (maxLength > 0)
                    maxLength = sqlDbType == SqlDbType.NVarChar ? Math.Min(maxLength, 4000) : Math.Min(maxLength, 8000);

                return new SqlMetaData(column.ColumnName, sqlDbType, maxLength);
            }


            if (dataType == typeof(char))
                return new SqlMetaData(column.ColumnName, sqlDbType, 1);

            if (sqlDbType == SqlDbType.Char || sqlDbType == SqlDbType.NChar)
            {
                maxLength = maxLength <= 0 ? (sqlDbType == SqlDbType.NChar ? 4000 : 8000) : maxLength;
                return new SqlMetaData(column.ColumnName, sqlDbType, maxLength);
            }

            if (sqlDbType == SqlDbType.Binary)
            {
                maxLength = maxLength <= 0 ? 8000 : maxLength;
                return new SqlMetaData(column.ColumnName, sqlDbType, maxLength);
            }

            if (sqlDbType == SqlDbType.VarBinary)
            {
                // set value for (MAX) 
                maxLength = maxLength <= 0 ? SqlMetaData.Max : maxLength;

                return new SqlMetaData(column.ColumnName, sqlDbType, maxLength);
            }

            if (sqlDbType == SqlDbType.Decimal)
            {
                if (column.PrecisionSpecified && column.ScaleSpecified)
                {
                    var (p, s) = this.SqlMetadata.GetPrecisionAndScale(column);
                    return new SqlMetaData(column.ColumnName, sqlDbType, p, s);

                }

                if (column.PrecisionSpecified)
                {
                    var p = this.SqlMetadata.GetPrecision(column);
                    return new SqlMetaData(column.ColumnName, sqlDbType, p);
                }

            }

            return new SqlMetaData(column.ColumnName, sqlDbType);

        }


        /// <summary>
        /// Executing a batch command
        /// </summary>
        public override async Task ExecuteBatchCommandAsync(DbCommand cmd, Guid senderScopeId, IEnumerable<SyncRow> applyRows, SyncTable schemaChangesTable,
                                                            SyncTable failedRows, long? lastTimestamp, DbConnection connection, DbTransaction transaction = null)
        {

            var applyRowsCount = applyRows.Count();

            if (applyRowsCount <= 0)
                return;

            var dataRowState = DataRowState.Unchanged;

            var records = new List<SqlDataRecord>(applyRowsCount);
            SqlMetaData[] metadatas = new SqlMetaData[schemaChangesTable.Columns.Count];

            for (int i = 0; i < schemaChangesTable.Columns.Count; i++)
                metadatas[i] = GetSqlMetadaFromType(schemaChangesTable.Columns[i]);

            try
            {
                foreach (var row in applyRows)
                {
                    dataRowState = row.RowState;

                    var record = new SqlDataRecord(metadatas);

                    int sqlMetadataIndex = 0;

                    for (int i = 0; i < schemaChangesTable.Columns.Count; i++)
                    {
                        var schemaColumn = schemaChangesTable.Columns[i];

                        // Get the default value
                        //var columnType = schemaColumn.GetDataType();
                        dynamic defaultValue = schemaColumn.GetDefaultValue();
                        dynamic rowValue = row[i];

                        // metadatas don't have readonly values, so get from sqlMetadataIndex
                        var sqlMetadataType = metadatas[sqlMetadataIndex].SqlDbType;

                        if (rowValue != null)
                        {
                            var columnType = rowValue.GetType();

                            switch (sqlMetadataType)
                            {
                                case SqlDbType.BigInt:
                                    if (columnType != typeof(long))
                                        rowValue = SyncTypeConverter.TryConvertTo<long>(rowValue);
                                    break;
                                case SqlDbType.Bit:
                                    if (columnType != typeof(bool))
                                        rowValue = SyncTypeConverter.TryConvertTo<bool>(rowValue);
                                    break;
                                case SqlDbType.Date:
                                case SqlDbType.DateTime:
                                case SqlDbType.DateTime2:
                                case SqlDbType.SmallDateTime:
                                    if (columnType != typeof(DateTime))
                                        rowValue = SyncTypeConverter.TryConvertTo<DateTime>(rowValue);
                                    if (rowValue < SqlDateMin)
                                        rowValue = SqlDateMin;
                                    break;
                                case SqlDbType.DateTimeOffset:
                                    if (columnType != typeof(DateTimeOffset))
                                        rowValue = SyncTypeConverter.TryConvertTo<DateTimeOffset>(rowValue);
                                    if (rowValue < SqlDateMin)
                                        rowValue = SqlDateMin;
                                    break;
                                case SqlDbType.Decimal:
                                    if (columnType != typeof(decimal))
                                        rowValue = SyncTypeConverter.TryConvertTo<decimal>(rowValue);
                                    break;
                                case SqlDbType.Float:
                                    if (columnType != typeof(double))
                                        rowValue = SyncTypeConverter.TryConvertTo<double>(rowValue);
                                    break;
                                case SqlDbType.Real:
                                    if (columnType != typeof(float))
                                        rowValue = SyncTypeConverter.TryConvertTo<float>(rowValue);
                                    break;
                                case SqlDbType.Image:
                                case SqlDbType.Binary:
                                case SqlDbType.VarBinary:
                                    if (columnType != typeof(byte[]))
                                        rowValue = SyncTypeConverter.TryConvertTo<byte[]>(rowValue);
                                    break;
                                case SqlDbType.Variant:
                                    break;
                                case SqlDbType.Int:
                                    if (columnType != typeof(int))
                                        rowValue = SyncTypeConverter.TryConvertTo<int>(rowValue);
                                    break;
                                case SqlDbType.Money:
                                case SqlDbType.SmallMoney:
                                    if (columnType != typeof(decimal))
                                        rowValue = SyncTypeConverter.TryConvertTo<decimal>(rowValue);
                                    break;
                                case SqlDbType.NChar:
                                case SqlDbType.NText:
                                case SqlDbType.VarChar:
                                case SqlDbType.Xml:
                                case SqlDbType.NVarChar:
                                case SqlDbType.Text:
                                case SqlDbType.Char:
                                    if (columnType != typeof(string))
                                        rowValue = SyncTypeConverter.TryConvertTo<string>(rowValue);
                                    break;
                                case SqlDbType.SmallInt:
                                    if (columnType != typeof(short))
                                        rowValue = SyncTypeConverter.TryConvertTo<short>(rowValue);
                                    break;
                                case SqlDbType.Time:
                                    if (columnType != typeof(TimeSpan))
                                        rowValue = SyncTypeConverter.TryConvertTo<TimeSpan>(rowValue);
                                    break;
                                case SqlDbType.Timestamp:
                                    break;
                                case SqlDbType.TinyInt:
                                    if (columnType != typeof(byte))
                                        rowValue = SyncTypeConverter.TryConvertTo<byte>(rowValue);
                                    break;
                                case SqlDbType.Udt:
                                    throw new ArgumentException($"Can't use UDT as SQL Type");
                                case SqlDbType.UniqueIdentifier:
                                    if (columnType != typeof(Guid))
                                        rowValue = SyncTypeConverter.TryConvertTo<Guid>(rowValue);
                                    break;
                            }
                        }

                        if (rowValue == null)
                            rowValue = DBNull.Value;

                        record.SetValue(sqlMetadataIndex, rowValue);
                        sqlMetadataIndex++;
                    }

                    records.Add(record);
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Can't create a SqlRecord based on the rows we have: {ex.Message}");
            }

            var sqlParameters = cmd.Parameters as SqlParameterCollection;

            sqlParameters["@changeTable"].TypeName = string.Empty;
            sqlParameters["@changeTable"].Value = records;

            if (sqlParameters.Contains("@sync_min_timestamp"))
                sqlParameters["@sync_min_timestamp"].Value = lastTimestamp.HasValue ? (object)lastTimestamp.Value : DBNull.Value;

            if (sqlParameters.Contains("@sync_scope_id"))
                sqlParameters["@sync_scope_id"].Value = senderScopeId;

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                cmd.Transaction = transaction;

                using var dataReader = await cmd.ExecuteReaderAsync().ConfigureAwait(false);

                while (dataReader.Read())
                {
                    //var itemArray = new object[dataReader.FieldCount];
                    //var itemArray = new object[failedRows.Columns.Count];
                    var itemArray = new SyncRow(schemaChangesTable, dataRowState);
                    for (var i = 0; i < dataReader.FieldCount; i++)
                    {
                        var columnValueObject = dataReader.GetValue(i);
                        var columnName = dataReader.GetName(i);

                        var columnValue = columnValueObject == DBNull.Value ? null : columnValueObject;

                        var failedColumn = failedRows.Columns[columnName];
                        var failedIndexColumn = failedRows.Columns.IndexOf(failedColumn);
                        itemArray[failedIndexColumn] = columnValue;
                    }

                    // don't care about row state 
                    // Since it will be requested by next request from GetConflict()
                    failedRows.Rows.Add(itemArray);
                }

                dataReader.Close();

            }
            catch (DbException ex)
            {
                Debug.WriteLine(ex.Message);
                throw;
            }
            finally
            {
                records.Clear();

                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();

            }
        }

        /// <summary>
        /// Check if an exception is a primary key exception
        /// </summary>
        public override bool IsPrimaryKeyViolation(Exception exception)
        {
            if (exception is SqlException error && error.Number == 2627)
                return true;

            return false;
        }


        public override bool IsUniqueKeyViolation(Exception exception)
        {
            if (exception is SqlException error && error.Number == 2627)
                return true;

            return false;
        }

        public override (DbCommand, bool) GetCommand(DbCommandType nameType, SyncFilter filter)
        {
            var command = new SqlCommand();
            bool isBatch = false;
            switch (nameType)
            {
                case DbCommandType.SelectChanges:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectChanges, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectInitializedChanges:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectInitializedChanges, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectInitializedChangesWithFilters:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectInitializedChangesWithFilters, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectChangesWithFilters:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectChangesWithFilters, filter);
                    isBatch = false;
                    break;
                case DbCommandType.SelectRow:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.SelectRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateRow:
                case DbCommandType.InsertRow:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.UpdateRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateRows:
                case DbCommandType.InsertRows:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.BulkUpdateRows, filter);
                    isBatch = true;
                    break;
                case DbCommandType.DeleteRow:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.DeleteRow, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteRows:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.BulkDeleteRows, filter);
                    isBatch = true;
                    break;
                case DbCommandType.DisableConstraints:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetCommandName(DbCommandType.DisableConstraints, filter);
                    isBatch = false;
                    break;
                case DbCommandType.EnableConstraints:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetCommandName(DbCommandType.EnableConstraints, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteMetadata:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.DeleteMetadata, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateMetadata:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetCommandName(DbCommandType.UpdateMetadata, filter);
                    isBatch = false;
                    break;
                case DbCommandType.InsertTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetTriggerCommandName(DbTriggerType.Insert, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetTriggerCommandName(DbTriggerType.Update, filter);
                    isBatch = false;
                    break;
                case DbCommandType.DeleteTrigger:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetTriggerCommandName(DbTriggerType.Delete, filter);
                    isBatch = false;
                    break;
                case DbCommandType.BulkTableType:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.BulkTableType, filter);
                    isBatch = false;
                    break;
                case DbCommandType.UpdateUntrackedRows:
                    command.CommandType = CommandType.Text;
                    command.CommandText = this.SqlObjectNames.GetCommandName(DbCommandType.UpdateUntrackedRows, filter);
                    isBatch = false;
                    break;
                case DbCommandType.Reset:
                    command.CommandType = CommandType.StoredProcedure;
                    command.CommandText = this.SqlObjectNames.GetStoredProcedureCommandName(DbStoredProcedureType.Reset, filter);
                    isBatch = false;
                    break;
                default:
                    throw new NotImplementedException($"This command type {nameType} is not implemented");
            }

            return (command, isBatch);
        }

        /// <summary>
        /// Set a stored procedure parameters or text parameters
        /// </summary>
        public override async Task AddCommandParametersAsync(DbCommandType commandType, DbCommand command, DbConnection connection, DbTransaction transaction = null, SyncFilter filter = null)
        {
            if (command == null)
                return;

            if (command.Parameters != null && command.Parameters.Count > 0)
                return;

            // special case for constraint
            if (commandType == DbCommandType.DisableConstraints || commandType == DbCommandType.EnableConstraints)
                return;


            // special case for UpdateMetadata
            if (commandType == DbCommandType.UpdateMetadata)
            {
                this.SetUpdateRowParameters(command);
                return;
            }

            // if we don't have stored procedure, return, because we don't want to derive parameters
            if (command.CommandType != CommandType.StoredProcedure)
                return;

            bool alreadyOpened = connection.State == ConnectionState.Open;

            try
            {
                if (!alreadyOpened)
                    await connection.OpenAsync().ConfigureAwait(false);

                command.Transaction = transaction;

                var textParser = ParserName.Parse(command.CommandText).Unquoted().Normalized().ToString();

                var source = connection.Database;

                textParser = $"{source}-{textParser}";

                if (derivingParameters.ContainsKey(textParser))
                {
                    foreach (var p in derivingParameters[textParser])
                        command.Parameters.Add(p.Clone());
                }
                else
                {
                    // Using the SqlCommandBuilder.DeriveParameters() method is not working yet, 
                    // because default value is not well done handled on the Dotmim.Sync framework
                    // TODO: Fix SqlCommandBuilder.DeriveParameters
                    //SqlCommandBuilder.DeriveParameters((SqlCommand)command);

                    await ((SqlConnection)connection).DeriveParametersAsync((SqlCommand)command, false, (SqlTransaction)transaction).ConfigureAwait(false);

                    var arrayParameters = new List<SqlParameter>();
                    foreach (var p in command.Parameters)
                        arrayParameters.Add(((SqlParameter)p).Clone());

                    derivingParameters.TryAdd(textParser, arrayParameters);
                }

                if (command.Parameters.Count > 0 && command.Parameters[0].ParameterName == "@RETURN_VALUE")
                    command.Parameters.RemoveAt(0);

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DeriveParameters failed : {ex}");
                throw;
            }
            finally
            {
                if (!alreadyOpened && connection.State != ConnectionState.Closed)
                    connection.Close();
            }


            foreach (var parameter in command.Parameters)
            {
                var sqlParameter = (SqlParameter)parameter;

                // try to get the source column (from the SchemaTable)
                var sqlParameterName = sqlParameter.ParameterName.Replace("@", "");
                var colDesc = TableDescription.Columns.FirstOrDefault(c => c.ColumnName.Equals(sqlParameterName, SyncGlobalization.DataSourceStringComparison));

                if (colDesc != null && !string.IsNullOrEmpty(colDesc.ColumnName))
                    sqlParameter.SourceColumn = colDesc.ColumnName;
            }
        }

        private void SetUpdateRowParameters(DbCommand command)
        {
            DbParameter p;

            foreach (var column in this.TableDescription.GetPrimaryKeysColumns().Where(c => !c.IsReadOnly))
            {
                var unquotedColumn = ParserName.Parse(column).Normalized().Unquoted().ToString();
                p = command.CreateParameter();
                p.ParameterName = $"@{unquotedColumn}";
                p.DbType = column.GetDbType();
                p.SourceColumn = column.ColumnName;
                p.Size = column.MaxLength;
                command.Parameters.Add(p);
            }

            p = command.CreateParameter();
            p.ParameterName = "@sync_scope_id";
            p.DbType = DbType.String;
            p.Size = 36;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = "@sync_row_is_tombstone";
            p.DbType = DbType.Boolean;
            p.Size = 2;
            command.Parameters.Add(p);

        }
    }
}
