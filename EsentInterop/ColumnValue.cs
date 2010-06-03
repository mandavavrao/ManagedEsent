﻿//-----------------------------------------------------------------------
// <copyright file="ColumnValue.cs" company="Microsoft Corporation">
//     Copyright (c) Microsoft Corporation.
// </copyright>
//-----------------------------------------------------------------------

namespace Microsoft.Isam.Esent.Interop
{
    using System;
    using System.Diagnostics;

    /// <summary>
    /// Base class for objects that represent a column value to be set.
    /// </summary>
    public abstract class ColumnValue
    {
        /// <summary>
        /// Initializes a new instance of the ColumnValue class.
        /// </summary>
        protected ColumnValue()
        {
            this.ItagSequence = 1;    
        }

        /// <summary>
        /// Gets or sets the columnid to be set or retrieved.
        /// </summary>
        public JET_COLUMNID Columnid { get; set; }

        /// <summary>
        /// Gets the last set or retrieved value of the column. The
        /// value is returned as a generic object.
        /// </summary>
        public abstract object ValueAsObject { get; }

        /// <summary>
        /// Gets or sets column update options.
        /// </summary>
        public SetColumnGrbit SetGrbit { get; set; }

        /// <summary>
        /// Gets or sets column retrieval options.
        /// </summary>
        public RetrieveColumnGrbit RetrieveGrbit { get; set; }

        /// <summary>
        /// Gets or sets the column itag sequence.
        /// </summary>
        public int ItagSequence { get; set; }

        /// <summary>
        /// Gets the error generated by retrieving or setting this column.
        /// </summary>
        public JET_err Error { get; internal set; }

        /// <summary>
        /// Gets the size of the value in the column. This returns 0 for
        /// variable sized columns (i.e. binary and string).
        /// </summary>
        protected abstract int Size { get;  }

        /// <summary>
        /// Recursive RetrieveColumns method for data pinning. This should pin a buffer and
        /// call the inherited RetrieveColumns method.
        /// </summary>
        /// <param name="sesid">The session to use.</param>
        /// <param name="tableid">
        /// The table to retrieve the columns from.
        /// </param>
        /// <param name="columnValues">
        /// Column values to retrieve.
        /// </param>
        /// <returns>An error code.</returns>
        internal static int RetrieveColumns(JET_SESID sesid, JET_TABLEID tableid, ColumnValue[] columnValues)
        {
            if (columnValues.Length > 1024)
            {
                throw new ArgumentOutOfRangeException("columnValues", columnValues.Length, "Too many column values");    
            }

            int err;
            unsafe
            {
                NATIVE_RETRIEVECOLUMN* nativeRetrievecolumns = stackalloc NATIVE_RETRIEVECOLUMN[columnValues.Length];
                byte[] buffer = Caches.ColumnCache.Allocate();
                fixed (byte* pinnedBuffer = buffer)
                {
                    byte* currentBuffer = pinnedBuffer;
                    int numVariableLengthColumns = columnValues.Length;

                    // First the fixed-size columns
                    for (int i = 0; i < columnValues.Length; ++i)
                    {
                        if (0 != columnValues[i].Size)
                        {
                            columnValues[i].MakeNativeRetrieveColumn(ref nativeRetrievecolumns[i]);
                            nativeRetrievecolumns[i].pvData = new IntPtr(currentBuffer);
                            nativeRetrievecolumns[i].cbData = checked((uint)columnValues[i].Size);

                            currentBuffer += nativeRetrievecolumns[i].cbData;
                            Debug.Assert(currentBuffer <= pinnedBuffer + buffer.Length, "Moved past end of pinned buffer");

                            numVariableLengthColumns--;
                        }
                    }

                    // Now the variable-length columns
                    if (numVariableLengthColumns > 0)
                    {
                        int bufferUsed = checked((int)(currentBuffer - pinnedBuffer));
                        int bufferRemaining = checked(buffer.Length - bufferUsed);
                        int bufferPerColumn = bufferRemaining / numVariableLengthColumns;
                        Debug.Assert(bufferPerColumn > 0, "Not enough buffer left to retrieve variable length columns");

                        // Now the variable-size columns
                        for (int i = 0; i < columnValues.Length; ++i)
                        {
                            if (0 == columnValues[i].Size)
                            {
                                columnValues[i].MakeNativeRetrieveColumn(ref nativeRetrievecolumns[i]);
                                nativeRetrievecolumns[i].pvData = new IntPtr(currentBuffer);
                                nativeRetrievecolumns[i].cbData = checked((uint) bufferPerColumn);
                                currentBuffer += nativeRetrievecolumns[i].cbData;
                                Debug.Assert(currentBuffer <= pinnedBuffer + buffer.Length, "Moved past end of pinned buffer");
                            }
                        }
                    }

                    // Retrieve the columns
                    err = Api.Impl.JetRetrieveColumns(sesid, tableid, nativeRetrievecolumns, columnValues.Length);

                    // Propagate the errors.
                    for (int i = 0; i < columnValues.Length; ++i)
                    {
                        columnValues[i].Error = (JET_err) nativeRetrievecolumns[i].err;
                    }

                    // Now parse out the columns that were retrieved successfully
                    for (int i = 0; i < columnValues.Length; ++i)
                    {
                        if (nativeRetrievecolumns[i].err >= (int)JET_err.Success
                            && nativeRetrievecolumns[i].err != (int)JET_wrn.BufferTruncated)
                        {
                            byte* columnBuffer = (byte*) nativeRetrievecolumns[i].pvData;
                            int startIndex = checked((int)(columnBuffer - pinnedBuffer));
                            columnValues[i].GetValueFromBytes(
                                buffer,
                                startIndex,
                                checked((int)nativeRetrievecolumns[i].cbActual),
                                nativeRetrievecolumns[i].err);
                        }
                    }
                }

                Caches.ColumnCache.Free(ref buffer);

                // Finally retrieve the buffers where the columns weren't large enough.
                RetrieveTruncatedBuffers(sesid, tableid, columnValues, nativeRetrievecolumns);
            }

            return err;
        }

        /// <summary>
        /// Recursive SetColumns method for data pinning. This should populate the buffer and
        /// call the inherited SetColumns method.
        /// </summary>
        /// <param name="sesid">The session to use.</param>
        /// <param name="tableid">
        /// The table to set the columns in. An update should be prepared.
        /// </param>
        /// <param name="columnValues">
        /// Column values to set.
        /// </param>
        /// <param name="nativeColumns">
        /// Structures to put the pinned data in.
        /// </param>
        /// <param name="i">Offset of this object in the array.</param>
        /// <returns>An error code.</returns>
        internal abstract unsafe int SetColumns(JET_SESID sesid, JET_TABLEID tableid, ColumnValue[] columnValues, NATIVE_SETCOLUMN* nativeColumns, int i);

        /// <summary>
        /// Recursive SetColumns function used to pin data.
        /// </summary>
        /// <param name="sesid">The session to use.</param>
        /// <param name="tableid">
        /// The table to set the columns in. An update should be prepared.
        /// </param>
        /// <param name="columnValues">
        /// Column values to set.
        /// </param>
        /// <param name="nativeColumns">
        /// Structures to put the pinned data in.
        /// </param>
        /// <param name="i">Offset of this object in the array.</param>
        /// <param name="buffer">The buffer for this object.</param>
        /// <param name="bufferSize">Size of the buffer for ths object.</param>
        /// <param name="hasValue">True if this object is non null.</param>
        /// <returns>An error code.</returns>
        /// <remarks>
        /// This is marked as internal because it uses the NATIVE_SETCOLUMN type
        /// which is also marked as internal. It should be treated as a protected
        /// method though.
        /// </remarks>
        internal unsafe int SetColumns(
            JET_SESID sesid,
            JET_TABLEID tableid,
            ColumnValue[] columnValues,
            NATIVE_SETCOLUMN* nativeColumns,
            int i,
            void* buffer,
            int bufferSize,
            bool hasValue)
        {
            Debug.Assert(this == columnValues[i], "SetColumns should be called on the current object");
            this.MakeNativeSetColumn(ref nativeColumns[i]);

            if (hasValue)
            {
                nativeColumns[i].cbData = checked((uint)bufferSize);
                nativeColumns[i].pvData = new IntPtr(buffer);
                if (0 == bufferSize)
                {
                    nativeColumns[i].grbit |= (uint)SetColumnGrbit.ZeroLength;
                }
            }

            int err = i == columnValues.Length - 1
                          ? Api.Impl.JetSetColumns(sesid, tableid, nativeColumns, columnValues.Length)
                          : columnValues[i + 1].SetColumns(sesid, tableid, columnValues, nativeColumns, i + 1);

            this.Error = (JET_err) nativeColumns[i].err;
            return err;
        }

        /// <summary>
        /// Given data retrieved from ESENT, decode the data and set the value in the ColumnValue object.
        /// </summary>
        /// <param name="value">An array of bytes.</param>
        /// <param name="startIndex">The starting position within the bytes.</param>
        /// <param name="count">The number of bytes to decode.</param>
        /// <param name="err">The error returned from ESENT.</param>
        protected abstract void GetValueFromBytes(byte[] value, int startIndex, int count, int err);

        /// <summary>
        /// Retrieve the value for columns whose buffers were truncated.
        /// </summary>
        /// <param name="sesid">The session to use.</param>
        /// <param name="tableid">The table to use.</param>
        /// <param name="columnValues">The column values.</param>
        /// <param name="nativeRetrievecolumns">
        /// The native retrieve columns that match the column values.
        /// </param>
        private static unsafe void RetrieveTruncatedBuffers(JET_SESID sesid, JET_TABLEID tableid, ColumnValue[] columnValues, NATIVE_RETRIEVECOLUMN* nativeRetrievecolumns)
        {
            for (int i = 0; i < columnValues.Length; ++i)
            {
                if (nativeRetrievecolumns[i].err == (int) JET_wrn.BufferTruncated)
                {
                    var buffer = new byte[nativeRetrievecolumns[i].cbActual];
                    int actualSize;
                    int err;
                    var retinfo = new JET_RETINFO { itagSequence = columnValues[i].ItagSequence };

                    // Pin the buffer and retrieve the data
                    fixed (byte* pinnedBuffer = buffer)
                    {
                        err = Api.Impl.JetRetrieveColumn(
                                      sesid,
                                      tableid,
                                      columnValues[i].Columnid,
                                      new IntPtr(pinnedBuffer),
                                      buffer.Length,
                                      out actualSize,
                                      columnValues[i].RetrieveGrbit,
                                      retinfo);
                    }

                    // Set the error in the ColumnValue before checkin it
                    columnValues[i].Error = (JET_err) err;
                    Api.Check(err);

                    // For BytesColumnValue this will copy the data to a new array.
                    // If this situation becomes common we should simply use the array.
                    columnValues[i].GetValueFromBytes(buffer, 0, actualSize, err);
                }
            }
        }

        /// <summary>
        /// Create a native SetColumn from this object.
        /// </summary>
        /// <param name="setcolumn">The native setcolumn structure to fill in.</param>
        private void MakeNativeSetColumn(ref NATIVE_SETCOLUMN setcolumn)
        {
            setcolumn.columnid = this.Columnid.Value;
            setcolumn.grbit = (uint)this.SetGrbit;
            setcolumn.itagSequence = checked((uint)this.ItagSequence);
        }

        /// <summary>
        /// Create a native RetrieveColumn from this object.
        /// </summary>
        /// <param name="retrievecolumn">
        /// The retrieve column structure to fill in.
        /// </param>
        private void MakeNativeRetrieveColumn(ref NATIVE_RETRIEVECOLUMN retrievecolumn)
        {
            retrievecolumn.columnid = this.Columnid.Value;
            retrievecolumn.grbit = (uint)this.RetrieveGrbit;
            retrievecolumn.itagSequence = checked((uint)this.ItagSequence);
        }
    }
}
