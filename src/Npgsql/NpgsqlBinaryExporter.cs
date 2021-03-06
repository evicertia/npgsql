﻿using System;
using System.Diagnostics.Contracts;
using System.Linq;
using Npgsql.BackendMessages;
using Npgsql.FrontendMessages;
using NpgsqlTypes;

namespace Npgsql
{
    /// <summary>
    /// Provides an API for a binary COPY TO operation, a high-performance data export mechanism from
    /// a PostgreSQL table. Initiated by <see cref="NpgsqlConnection.BeginBinaryExport"/>
    /// </summary>
    public class NpgsqlBinaryExporter : IDisposable
    {
        #region Fields and Properties

        NpgsqlConnector _connector;
        NpgsqlBuffer _buf;
        TypeHandlerRegistry _registry;
        bool _isConsumed, _isDisposed;
        int _leftToReadInDataMsg, _columnLen;

        short _column;

        /// <summary>
        /// The number of columns, as returned from the backend in the CopyInResponse.
        /// </summary>
        internal int NumColumns { get; private set; }

        #endregion

        #region Construction / Initialization

        internal NpgsqlBinaryExporter(NpgsqlConnector connector, string copyToCommand)
        {
            _connector = connector;
            _buf = connector.Buffer;
            _registry = connector.TypeHandlerRegistry;
            _columnLen = int.MinValue;   // Mark that the (first) column length hasn't been read yet
            _column = -1;

            _connector.SendSingleMessage(new QueryMessage(copyToCommand));

            // TODO: Failure will break the connection (e.g. if we get CopyOutResponse), handle more gracefully
            var copyOutResponse = _connector.ReadExpecting<CopyOutResponseMessage>();
            if (!copyOutResponse.IsBinary) {
                _connector.Break();
                throw new ArgumentException("copyToCommand triggered a text transfer, only binary is allowed", "copyToCommand");
            }
            NumColumns = copyOutResponse.NumColumns;
            ReadHeader();
            _connector.State = ConnectorState.Copy;
        }

        void ReadHeader()
        {
            _leftToReadInDataMsg = _connector.ReadExpecting<CopyDataMessage>().Length;
            var headerLen = NpgsqlRawCopyStream.BinarySignature.Length + 4 + 4;
            _buf.Ensure(headerLen);
            if (NpgsqlRawCopyStream.BinarySignature.Any(t => _buf.ReadByte() != t)) {
                throw new Exception("Invalid COPY binary signature at beginning!");
            }
            var flags = _buf.ReadInt32();
            if (flags != 0) {
                throw new NotSupportedException("Unsupported flags in COPY operation (OID inclusion?)");
            }
            _buf.ReadInt32();   // Header extensions, currently unused
            _leftToReadInDataMsg -= headerLen;
        }

        #endregion

        #region Read

        /// <summary>
        /// Starts reading a single row, must be invoked before reading any columns.
        /// </summary>
        /// <returns>
        /// The number of columns in the row. -1 if there are no further rows.
        /// Note: This will currently be the same value for all rows, but this may change in the future.
        /// </returns>
        public int StartRow()
        {
            CheckDisposed();
            if (_isConsumed) { return -1; }

            // The very first row (i.e. _column == -1) is included in the header's CopyData message.
            // Otherwise we need to read in a new CopyData row (the docs specify that there's a CopyData
            // message per row).
            if (_column == NumColumns)
            {
                _leftToReadInDataMsg = _connector.ReadExpecting<CopyDataMessage>().Length;
            }
            else if (_column != -1)
            {
                throw new InvalidOperationException("Already in the middle of a row");
            }
            _buf.Ensure(2);
            _leftToReadInDataMsg -= 2;
            var numColumns = _buf.ReadInt16();
            if (numColumns == -1)
            {
                Contract.Assume(_leftToReadInDataMsg == 0);
                _connector.ReadExpecting<CopyDoneMessage>();
                _connector.ReadExpecting<CommandCompleteMessage>();
                _connector.ReadExpecting<ReadyForQueryMessage>();
                _column = -1;
                _isConsumed = true;
                return -1;
            }
            Contract.Assume(numColumns == NumColumns);

            _column = 0;
            return NumColumns;
        }

        /// <summary>
        /// Reads the current column, returns its value and moves ahead to the next column.
        /// If the column is null an exception is thrown.
        /// </summary>
        /// <typeparam name="T">
        /// The type of the column to be read. This must correspond to the actual type or data
        /// corruption will occur. If in doubt, use <see cref="Read{T}(NpgsqlDbType)"/> to manually
        /// specify the type.
        /// </typeparam>
        /// <returns>The value of the column</returns>
        public T Read<T>()
        {
            CheckDisposed();
            if (_column == -1 || _column == NumColumns) {
                throw new InvalidOperationException("Not reading a row");
            }

            var type = typeof(T);
            var handler = _registry[type];
            return DoRead<T>(handler);
        }

        /// <summary>
        /// Reads the current column, returns its value according to <paramref name="type"/> and
        /// moves ahead to the next column.
        /// If the column is null an exception is thrown.
        /// </summary>
        /// <param name="type">
        /// In some cases <typeparamref name="T"/> isn't enough to infer the data type coming in from the
        /// database. This parameter and be used to unambiguously specify the type. An example is the JSONB
        /// type, for which <typeparamref name="T"/> will be a simple string but for which
        /// <paramref name="type"/> must be specified as <see cref="NpgsqlDbType.Jsonb"/>.
        /// </param>
        /// <typeparam name="T">The .NET type of the column to be read.</typeparam>
        /// <returns>The value of the column</returns>
        public T Read<T>(NpgsqlDbType type)
        {
            CheckDisposed();
            if (_column == -1 || _column == NumColumns) {
                throw new InvalidOperationException("Not reading a row");
            }

            var handler = _registry[type];
            return DoRead<T>(handler);
        }

        T DoRead<T>(TypeHandler handler)
        {
            ReadColumnLenIfNeeded();
            if (_columnLen == -1) {
                // TODO: What actual exception to throw here? Oracle throws InvalidCast, SqlClient throws its
                // own SqlNullValueException
                throw new InvalidCastException("Column is null");
            }

            var result = handler.Read<T>(_buf, _columnLen);
            _leftToReadInDataMsg -= _columnLen;
            _columnLen = int.MinValue;   // Mark that the (next) column length hasn't been read yet
            _column++;
            return result;
        }

        /// <summary>
        /// Returns whether the current column is null.
        /// </summary>
        public bool IsNull
        {
            get
            {
                ReadColumnLenIfNeeded();
                return _columnLen == -1;
            }
        }

        /// <summary>
        /// Skips the current column without interpreting its value.
        /// </summary>
        public void Skip()
        {
            ReadColumnLenIfNeeded();
            if (_columnLen != -1) {
                _buf.Skip(_columnLen);
            }
            _columnLen = int.MinValue;
            _column++;
        }

        #endregion

        #region Utilities

        void ReadColumnLenIfNeeded()
        {
            if (_columnLen == int.MinValue) {
                _buf.Ensure(4);
                _columnLen = _buf.ReadInt32();
                _leftToReadInDataMsg -= 4;
            }
        }

        void CheckDisposed()
        {
            if (_isDisposed) {
                throw new ObjectDisposedException(GetType().FullName, "The COPY operation has already ended.");
            }
        }

        #endregion

        #region Close / Dispose

        public void Dispose()
        {
            if (_isDisposed) { return; }

            if (!_isConsumed)
            {
                // Finish the current CopyData message
                _buf.Skip(_leftToReadInDataMsg);
                // Read to the end
                _connector.SkipUntil(BackendMessageCode.CopyDone);
                _connector.ReadExpecting<CommandCompleteMessage>();
                _connector.ReadExpecting<ReadyForQueryMessage>();
            }

            _connector.State = ConnectorState.Ready;

            _connector = null;
            _registry = null;
            _buf = null;
            _isDisposed = true;
        }

        #endregion
    }
}
