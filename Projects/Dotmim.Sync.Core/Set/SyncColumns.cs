﻿using Dotmim.Sync.Builders;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace Dotmim.Sync
{
    [CollectionDataContract(Name = "cols", ItemName = "col"), Serializable]
    public class SyncColumns : ICollection<SyncColumn>, IList<SyncColumn>
    {
        /// <summary>
        /// Exposing the InnerCollection for serialization purpose
        /// </summary>
        [DataMember(Name = "c", IsRequired = true)]
        public Collection<SyncColumn> InnerCollection { get; set; } = new Collection<SyncColumn>();

        /// <summary>
        /// Column's schema
        /// </summary>
        [IgnoreDataMember]
        public SyncTable Table { get; internal set; }

        /// <summary>
        /// Create a default collection for Serializers
        /// </summary>
        public SyncColumns()
        {
        }

        /// <summary>
        /// Create a new collection of tables for a SyncSchema
        /// </summary>
        public SyncColumns(SyncTable table) => this.Table = table;

        /// <summary>
        /// Since we don't serializer the reference to the schema, this method will reaffect the correct schema
        /// </summary>
        public void EnsureColumns(SyncTable table)
        {
            this.Table = table;
            foreach (var column in this)
                column.Table = table;
        }

        /// <summary>
        /// Get a Column by its name
        /// </summary>
        public SyncColumn this[string columnName]
        {
            get
            {
                var schema = this.Table?.Schema;

                if (schema == null)
                    throw new ArgumentException("Schema is null");

                return InnerCollection.FirstOrDefault(c => schema.StringEquals(columnName, c.ColumnName));
            }
        }


        /// <summary>
        /// Add a new Column to the Schema Column collection
        /// </summary>
        public void Add(SyncColumn item)
        {
            item.Table = this.Table;
            InnerCollection.Add(item);
            AffectOrder();

        }

        public void Add(string columnName, Type type = null)
        {
            var item = new SyncColumn(columnName, type);
            item.Table = this.Table;
            InnerCollection.Add(item);
            AffectOrder();

        }



        /// <summary>
        /// Add a collection of columns
        /// </summary>
        /// <param name="addedColumns"></param>
        public void AddRange(SyncColumn[] addedColumns)
        {
            foreach (var item in addedColumns)
            {
                item.Table = this.Table;
                InnerCollection.Add(item);
            }

            AffectOrder();

        }

    
        /// <summary>
        /// Reorganize columns order
        /// </summary>
        public void Reorder(SyncColumn column, int newPosition)
        {
            if (newPosition < 0 || newPosition > this.InnerCollection.Count - 1)
                throw new Exception($"InvalidOrdinal(ordinal, {newPosition}");

            // Remove column fro collection
            this.InnerCollection.Remove(column);

            // Add at the end or insert in new positions
            if (newPosition > this.InnerCollection.Count - 1)
                this.InnerCollection.Add(column);
            else
                this.InnerCollection.Insert(newPosition, column);

            AffectOrder();
        }
        private void AffectOrder()
        {
            // now reordered correctly, affect new Ordinal property
            for (int i = 0; i < this.InnerCollection.Count; i++)
                this.InnerCollection[i].Ordinal = i;

        }


        public SyncColumn this[int index] => InnerCollection[index];
        public int Count => InnerCollection.Count;
        public bool IsReadOnly => false;
        SyncColumn IList<SyncColumn>.this[int index]
        {
            get => this.InnerCollection[index];
            set => this.InnerCollection[index] = value;
        }
        public bool Remove(SyncColumn item) => InnerCollection.Remove(item);
        public void Clear() => InnerCollection.Clear();
        public bool Contains(SyncColumn item) => InnerCollection.Contains(item);
        public void CopyTo(SyncColumn[] array, int arrayIndex) => InnerCollection.CopyTo(array, arrayIndex);
        public int IndexOf(SyncColumn item) => InnerCollection.IndexOf(item);
        public void RemoveAt(int index) => InnerCollection.RemoveAt(index);
        IEnumerator IEnumerable.GetEnumerator() => InnerCollection.GetEnumerator();
        public IEnumerator<SyncColumn> GetEnumerator() => InnerCollection.GetEnumerator();
        public override string ToString() => this.InnerCollection.Count.ToString();
        public void Insert(int index, SyncColumn item) => this.InnerCollection.Insert(index, item);
    }

}
