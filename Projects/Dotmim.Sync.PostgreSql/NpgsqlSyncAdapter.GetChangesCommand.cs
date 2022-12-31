﻿using Npgsql;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Data;
using System.Text;
using Dotmim.Sync.Builders;
using NpgsqlTypes;
using System.Linq;

namespace Dotmim.Sync.PostgreSql
{
    public partial class NpgsqlSyncAdapter : DbSyncAdapter
    {

        // ---------------------------------------------------
        // Select Changes Command
        // ---------------------------------------------------

        /// <summary>
        /// Get the Select Changes Command
        /// </summary>
        private (DbCommand, bool) GetSelectChangesCommand(SyncFilter filter = null)
        {
            var schema = NpgsqlManagementUtils.GetUnquotedSqlSchemaName(TableName);

            StringBuilder stringBuilder = new StringBuilder();

            if (filter != null)
                stringBuilder.AppendLine("SELECT DISTINCT");
            else
                stringBuilder.AppendLine("SELECT");
            // ----------------------------------
            // Add all columns
            // ----------------------------------
            foreach (var pkColumn in this.TableDescription.PrimaryKeys)
            {
                var columnName = ParserName.Parse(pkColumn, "\"").Quoted().ToString();
                stringBuilder.AppendLine($"\tside.{columnName}, ");
            }
            foreach (var mutableColumn in this.TableDescription.GetMutableColumns())
            {
                var columnName = ParserName.Parse(mutableColumn, "\"").Quoted().ToString();
                stringBuilder.AppendLine($"\tbase.{columnName}, ");
            }
            stringBuilder.AppendLine($"\tside.\"sync_row_is_tombstone\", ");
            stringBuilder.AppendLine($"\tside.\"update_scope_id\" as \"sync_update_scope_id\" ");
            // ----------------------------------
            stringBuilder.AppendLine($"FROM {schema}.{TableName.Quoted()} base");

            // ----------------------------------
            // Make Right Join
            // ----------------------------------
            stringBuilder.Append($"RIGHT JOIN {schema}.{TrackingTableName.Quoted()} side ON ");

            string empty = "";
            foreach (var pkColumn in this.TableDescription.PrimaryKeys)
            {
                var columnName = ParserName.Parse(pkColumn, "\"").Quoted().ToString();
                stringBuilder.Append($"{empty}base.{columnName} = side.{columnName}");
                empty = " AND ";
            }

            // ----------------------------------
            // Custom Joins
            // ----------------------------------
            if (filter != null)
                stringBuilder.Append(CreateFilterCustomJoins(filter));

            stringBuilder.AppendLine();
            stringBuilder.AppendLine("WHERE (");

            // ----------------------------------
            // Where filters and Custom Where string
            // ----------------------------------
            if (filter != null)
            {
                var createFilterWhereSide = CreateFilterWhereSide(filter, true);
                stringBuilder.Append(createFilterWhereSide);

                if (!string.IsNullOrEmpty(createFilterWhereSide))
                    stringBuilder.AppendLine($"AND ");

                var createFilterCustomWheres = CreateFilterCustomWheres(filter);
                stringBuilder.Append(createFilterCustomWheres);

                if (!string.IsNullOrEmpty(createFilterCustomWheres))
                    stringBuilder.AppendLine($"AND ");
            }
            // ----------------------------------


            stringBuilder.AppendLine("\tside.\"timestamp\" > @sync_min_timestamp");
            stringBuilder.AppendLine("\tAND (side.\"update_scope_id\" <> @sync_scope_id OR side.\"update_scope_id\" IS NULL)");
            stringBuilder.AppendLine(");");

            var sqlCommand = new NpgsqlCommand
            {
                CommandType = CommandType.Text,
                CommandText = stringBuilder.ToString()
            };

            return (sqlCommand, false);
        }

        private void SetSelectChangesParameters(DbCommand command, SyncFilter filter = null)
        {
            var originalProvider = NpgsqlSyncProvider.ProviderType;

            var p = command.CreateParameter();
            p.ParameterName = "@sync_min_timestamp";
            p.DbType = DbType.Int64;
            command.Parameters.Add(p);

            p = command.CreateParameter();
            p.ParameterName = "@sync_scope_id";
            p.DbType = DbType.Guid;
            command.Parameters.Add(p);

            if (filter == null)
                return;

            var parameters = filter.Parameters;

            if (parameters.Count == 0)
                return;

            foreach (var param in parameters)
            {
                if (param.DbType.HasValue)
                {
                    // Get column name and type
                    var columnName = ParserName.Parse(param.Name).Unquoted().Normalized().ToString();
                    var syncColumn = new SyncColumn(columnName)
                    {
                        DbType = (int)param.DbType.Value,
                        MaxLength = param.MaxLength,
                    };
                    var sqlDbType = this.NpgsqlDbMetadata.GetOwnerDbTypeFromDbType(syncColumn);

                    var customParameterFilter = new NpgsqlParameter($"@{columnName}", sqlDbType)
                    {
                        Size = param.MaxLength,
                        IsNullable = param.AllowNull,
                        Value = param.DefaultValue
                    };
                    command.Parameters.Add(customParameterFilter);
                }
                else
                {
                    var tableFilter = this.TableDescription.Schema.Tables[param.TableName, param.SchemaName];
                    if (tableFilter == null)
                        throw new FilterParamTableNotExistsException(param.TableName);

                    var columnFilter = tableFilter.Columns[param.Name];
                    if (columnFilter == null)
                        throw new FilterParamColumnNotExistsException(param.Name, param.TableName);

                    // Get column name and type
                    var columnName = ParserName.Parse(columnFilter).Unquoted().Normalized().ToString();


                    var sqlDbType = tableFilter.OriginalProvider == originalProvider ?
                        this.NpgsqlDbMetadata.GetNpgsqlDbType(columnFilter) : this.NpgsqlDbMetadata.GetOwnerDbTypeFromDbType(columnFilter);

                    // Add it as parameter
                    var sqlParamFilter = new NpgsqlParameter($"@{columnName}", sqlDbType)
                    {
                        Size = columnFilter.MaxLength,
                        IsNullable = param.AllowNull,
                        Value = param.DefaultValue
                    };
                    command.Parameters.Add(sqlParamFilter);
                }

            }

        }


        // ---------------------------------------------------
        // Select Initialize Changes Command
        // ---------------------------------------------------

        private (DbCommand, bool) GetSelectInitializedChangesCommand(SyncFilter filter = null)
        {
            var schema = NpgsqlManagementUtils.GetUnquotedSqlSchemaName(TableName);
            var stringBuilder = new StringBuilder();

            // if we have a filter we may have joins that will duplicate lines
            if (filter != null)
                stringBuilder.AppendLine("SELECT DISTINCT");
            else
                stringBuilder.AppendLine("SELECT");

            var columns = this.TableDescription.GetMutableColumns(false, true).ToList();

            for (var i = 0; i < columns.Count; i++)
            {
                var mutableColumn = columns[i];
                var columnName = ParserName.Parse(mutableColumn, "\"").Quoted().ToString();
                stringBuilder.Append($"\tbase.{columnName}");

                if (i < columns.Count - 1)
                    stringBuilder.AppendLine(", ");
            }
            stringBuilder.AppendLine();
            stringBuilder.AppendLine($"FROM {schema}.{TableName.Quoted()} base");

            // ----------------------------------
            // Make Left Join
            // ----------------------------------
            stringBuilder.Append($"LEFT JOIN {schema}.{TrackingTableName.Quoted()} side ON ");

            string empty = "";
            foreach (var pkColumn in this.TableDescription.GetPrimaryKeysColumns())
            {
                var columnName = ParserName.Parse(pkColumn, "\"").Quoted().ToString();
                stringBuilder.Append($"{empty}base.{columnName} = side.{columnName}");
                empty = " AND ";
            }

            // ----------------------------------
            // Custom Joins
            // ----------------------------------
            if (filter != null)
                stringBuilder.Append(CreateFilterCustomJoins(filter));

            stringBuilder.AppendLine();
            stringBuilder.AppendLine("WHERE (");

            // ----------------------------------
            // Where filters and Custom Where string
            // ----------------------------------
            if (filter != null)
            {
                var createFilterWhereSide = CreateFilterWhereSide(filter);
                stringBuilder.Append(createFilterWhereSide);

                if (!string.IsNullOrEmpty(createFilterWhereSide))
                    stringBuilder.AppendLine($"AND ");

                var createFilterCustomWheres = CreateFilterCustomWheres(filter);
                stringBuilder.Append(createFilterCustomWheres);

                if (!string.IsNullOrEmpty(createFilterCustomWheres))
                    stringBuilder.AppendLine($"AND ");
            }
            // ----------------------------------


            stringBuilder.AppendLine("\t(side.\"timestamp\" > @sync_min_timestamp OR  @sync_min_timestamp IS NULL)");
            stringBuilder.AppendLine(");");

            var sqlCommand = new NpgsqlCommand
            {
                CommandType = CommandType.Text,
                CommandText = stringBuilder.ToString()
            };

            return (sqlCommand, false);
        }

        //----------------------------------------------------

        private string CreateFilterCustomJoins(SyncFilter filter)
        {
            var customJoins = filter.Joins;

            if (customJoins.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();

            stringBuilder.AppendLine();
            foreach (var customJoin in customJoins)
            {
                switch (customJoin.JoinEnum)
                {
                    case Join.Left:
                        stringBuilder.Append("LEFT JOIN ");
                        break;
                    case Join.Right:
                        stringBuilder.Append("RIGHT JOIN ");
                        break;
                    case Join.Outer:
                        stringBuilder.Append("OUTER JOIN ");
                        break;
                    case Join.Inner:
                    default:
                        stringBuilder.Append("INNER JOIN ");
                        break;
                }

                var fullTableName = string.IsNullOrEmpty(filter.SchemaName) ? filter.TableName : $"{filter.SchemaName}.{filter.TableName}";
                var filterTableName = ParserName.Parse(fullTableName, "\"").Quoted().Schema().ToString();

                var joinTableName = ParserName.Parse(customJoin.TableName, "\"").Quoted().Schema().ToString();

                var leftTableName = ParserName.Parse(customJoin.LeftTableName, "\"").Quoted().Schema().ToString();
                if (string.Equals(filterTableName, leftTableName, SyncGlobalization.DataSourceStringComparison))
                    leftTableName = "base";

                var rightTableName = ParserName.Parse(customJoin.RightTableName, "\"").Quoted().Schema().ToString();
                if (string.Equals(filterTableName, rightTableName, SyncGlobalization.DataSourceStringComparison))
                    rightTableName = "base";

                var leftColumName = ParserName.Parse(customJoin.LeftColumnName, "\"").Quoted().ToString();
                var rightColumName = ParserName.Parse(customJoin.RightColumnName, "\"").Quoted().ToString();

                stringBuilder.AppendLine($"{joinTableName} ON {leftTableName}.{leftColumName} = {rightTableName}.{rightColumName}");
            }

            return stringBuilder.ToString();
        }

        protected string CreateFilterCustomWheres(SyncFilter filter)
        {
            var customWheres = filter.CustomWheres;

            if (customWheres.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();
            var and2 = "  ";
            stringBuilder.AppendLine($"(");

            foreach (var customWhere in customWheres)
            {
                stringBuilder.Append($"{and2}{customWhere}");
                and2 = " AND ";
            }

            stringBuilder.AppendLine();
            stringBuilder.AppendLine($")");

            return stringBuilder.ToString();
        }

        protected string CreateFilterWhereSide(SyncFilter filter, bool checkTombstoneRows = false)
        {
            var sideWhereFilters = filter.Wheres;

            if (sideWhereFilters.Count == 0)
                return string.Empty;

            var stringBuilder = new StringBuilder();

            // Managing when state is tombstone
            if (checkTombstoneRows)
                stringBuilder.AppendLine($"(");

            stringBuilder.AppendLine($" (");


            var and2 = "   ";

            foreach (var whereFilter in sideWhereFilters)
            {
                var tableFilter = this.TableDescription.Schema.Tables[whereFilter.TableName, whereFilter.SchemaName];
                if (tableFilter == null)
                    throw new FilterParamTableNotExistsException(whereFilter.TableName);

                var columnFilter = tableFilter.Columns[whereFilter.ColumnName];
                if (columnFilter == null)
                    throw new FilterParamColumnNotExistsException(whereFilter.ColumnName, whereFilter.TableName);

                var tableName = ParserName.Parse(tableFilter, "\"").Unquoted().ToString();
                if (string.Equals(tableName, filter.TableName, SyncGlobalization.DataSourceStringComparison))
                    tableName = "\"base\"";
                else
                    tableName = ParserName.Parse(tableFilter, "\"").Quoted().ToString();

                var columnName = ParserName.Parse(columnFilter, "\"").Quoted().ToString();
                var parameterName = ParserName.Parse(whereFilter.ParameterName, "\"").Unquoted().Normalized().ToString();

                var param = filter.Parameters[parameterName];

                if (param == null)
                    throw new FilterParamColumnNotExistsException(columnName, whereFilter.TableName);

                stringBuilder.Append($"{and2}({tableName}.{columnName} = @{parameterName}");

                if (param.AllowNull)
                    stringBuilder.Append($" OR @{parameterName} IS NULL");

                stringBuilder.Append($")");

                and2 = " AND ";

            }
            stringBuilder.AppendLine();

            stringBuilder.AppendLine($"  )");

            if (checkTombstoneRows)
            {
                stringBuilder.AppendLine($" OR side.sync_row_is_tombstone = TRUE");
                stringBuilder.AppendLine($")");
            }
            // Managing when state is tombstone

            return stringBuilder.ToString();
        }

    }
}
