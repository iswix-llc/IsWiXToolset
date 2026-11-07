// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.Native.Msi
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Globalization;
    using WixToolset.Core.Native.Managed;
    using Lib = IsWiX.WindowsInstaller;

    /// <summary>
    /// Wrapper class for MSI API views.
    /// </summary>
    public sealed class View : MsiHandle
    {
        private Lib.View inner;
        private string managedQuery;

        /// <summary>
        /// Constructor that creates a view given a database handle and a query.
        /// </summary>
        /// <param name="db">Handle to the database to run the query on.</param>
        /// <param name="query">Query to be executed.</param>
        public View(Database db, string query)
        {
            if (null == db)
            {
                throw new ArgumentNullException(nameof(db));
            }

            if (null == query)
            {
                throw new ArgumentNullException(nameof(query));
            }

            if (db.Inner != null)
            {
                this.inner = ManagedBackend.Run(() => db.Inner.OpenView(query), "OpenView [" + query + "]");
                this.managedQuery = query;
                return;
            }

            var error = MsiInterop.MsiDatabaseOpenView(db.Handle, query, out var handle);
            if (0 != error)
            {
                throw new MsiException(error);
            }

            this.Handle = handle;
        }

        /// <summary>
        /// Enumerator that automatically disposes of the retrieved Records.
        /// </summary>
        public IEnumerable<Record> Records => new ViewEnumerable(this);

        /// <summary>
        /// Executes a view with no customizable parameters.
        /// </summary>
        public void Execute()
        {
            this.Execute(null);
        }

        /// <summary>
        /// Executes a query substituing the values from the records into the customizable parameters 
        /// in the view.
        /// </summary>
        /// <param name="record">Record containing parameters to be substituded into the view.</param>
        public void Execute(Record record)
        {
            if (this.inner != null)
            {
                ManagedBackend.Run(() => this.inner.Execute(record?.Inner));
                return;
            }

            var error = MsiInterop.MsiViewExecute(this.Handle, null == record ? IntPtr.Zero : record.Handle);
            if (0 != error)
            {
                throw new MsiException(error);
            }
        }

        /// <summary>
        /// Fetches the next row in the view.
        /// </summary>
        /// <returns>Returns the fetched record; otherwise null.</returns>
        public Record Fetch()
        {
            if (this.inner != null)
            {
                var fetched = ManagedBackend.Run(() => this.inner.Fetch());
                return fetched == null ? null : new Record(fetched);
            }

            var error = MsiInterop.MsiViewFetch(this.Handle, out var recordHandle);
            if (259 == error)
            {
                return null;
            }
            else if (0 != error)
            {
                throw new MsiException(error);
            }

            return new Record(recordHandle);
        }

        /// <summary>
        /// Updates a fetched record.
        /// </summary>
        /// <param name="type">Type of modification mode.</param>
        /// <param name="record">Record to be modified.</param>
        public void Modify(ModifyView type, Record record)
        {
            if (this.inner != null)
            {
                ManagedBackend.Run(() => this.inner.Modify((Lib.ViewModifyMode)Convert.ToInt32(type, CultureInfo.InvariantCulture), record.Inner), "Modify " + type + " on [" + this.managedQuery + "]");
                return;
            }

            var error = MsiInterop.MsiViewModify(this.Handle, Convert.ToInt32(type, CultureInfo.InvariantCulture), record.Handle);
            if (0 != error)
            {
                throw new MsiException(error);
            }
        }

        /// <summary>
        /// Get the column names in a record.
        /// </summary>
        /// <returns></returns>
        public Record GetColumnNames()
        {
            return this.GetColumnInfo(MsiInterop.MSICOLINFONAMES);
        }

        /// <summary>
        /// Get the column types in a record.
        /// </summary>
        /// <returns></returns>
        public Record GetColumnTypes()
        {
            return this.GetColumnInfo(MsiInterop.MSICOLINFOTYPES);
        }

        /// <summary>
        /// Returns a record containing column names or definitions.
        /// </summary>
        /// <param name="columnType">Specifies a flag indicating what type of information is needed. Either MSICOLINFO_NAMES or MSICOLINFO_TYPES.</param>
        /// <returns>The record containing information about the column.</returns>
        public Record GetColumnInfo(int columnType)
        {
            if (this.inner != null)
            {
                return ManagedBackend.Run(() =>
                {
                    var columns = this.inner.Columns;
                    var record = new Lib.Record(columns.Count);
                    for (var i = 0; i < columns.Count; ++i)
                    {
                        record.SetString(i + 1, columnType == MsiInterop.MSICOLINFONAMES ? columns[i].Name : columns[i].ColumnDefinitionString);
                    }

                    return new Record(record);
                });
            }


            var error = MsiInterop.MsiViewGetColumnInfo(this.Handle, columnType, out var recordHandle);
            if (0 != error)
            {
                throw new MsiException(error);
            }

            return new Record(recordHandle);
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing && this.inner != null)
            {
                this.inner.Dispose();
                this.inner = null;
            }

            base.Dispose(disposing);
        }

        private class ViewEnumerable : IEnumerable<Record>
        {
            private readonly View view;

            public ViewEnumerable(View view) => this.view = view;

            public IEnumerator<Record> GetEnumerator() => new ViewEnumerator(this.view);

            IEnumerator IEnumerable.GetEnumerator() => new ViewEnumerator(this.view);
        }

        private class ViewEnumerator : IEnumerator<Record>
        {
            private readonly View view;
            private readonly List<Record> records = new List<Record>();
            private int position = -1;
            private bool disposed;

            public ViewEnumerator(View view) => this.view = view;

            public Record Current => this.records[this.position];

            object IEnumerator.Current => this.records[this.position];

            public bool MoveNext()
            {
                if (this.position + 1 >= this.records.Count)
                {
                    var record = this.view.Fetch();

                    if (record == null)
                    {
                        return false;
                    }

                    this.records.Add(record);
                    this.position = this.records.Count - 1;
                }
                else
                {
                    ++this.position;
                }

                return true;
            }

            public void Reset() => this.position = -1;

            public void Dispose()
            {
                this.Dispose(true);
            }

            protected virtual void Dispose(bool disposing)
            {
                if (!this.disposed)
                {
                    if (disposing)
                    {
                        foreach (var record in this.records)
                        {
                            record.Dispose();
                        }
                    }

                    this.disposed = true;
                }
            }
        }
    }
}
