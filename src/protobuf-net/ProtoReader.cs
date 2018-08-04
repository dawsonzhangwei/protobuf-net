﻿
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ProtoBuf.Meta;

namespace ProtoBuf
{
    /// <summary>
    /// A stateful reader, used to read a protobuf stream. Typical usage would be (sequentially) to call
    /// ReadFieldHeader and (after matching the field) an appropriate Read* method.
    /// </summary>
    public abstract partial class ProtoReader : IDisposable
    {
        TypeModel _model;
        int _fieldNumber, _depth;
        long blockEnd64, _position64;
        WireType wireType;
        bool internStrings;
        private NetObjectCache netCache;

        // this is how many outstanding objects do not currently have
        // values for the purposes of reference tracking; we'll default
        // to just trapping the root object
        // note: objects are trapped (the ref and key mapped) via NoteObject
        uint trapCount; // uint is so we can use beq/bne more efficiently than bgt

        /// <summary>
        /// Gets the number of the field being processed.
        /// </summary>
        public int FieldNumber => _fieldNumber;

        /// <summary>
        /// Indicates the underlying proto serialization format on the wire.
        /// </summary>
        public WireType WireType
        {
            get => wireType;
            private protected set => wireType = value;
        }



        internal const long TO_EOF = -1;

        /// <summary>
        /// Gets / sets a flag indicating whether strings should be checked for repetition; if
        /// true, any repeated UTF-8 byte sequence will result in the same String instance, rather
        /// than a second instance of the same string. Enabled by default. Note that this uses
        /// a <i>custom</i> interner - the system-wide string interner is not used.
        /// </summary>
        public bool InternStrings { get { return internStrings; } set { internStrings = value; } }

        private protected ProtoReader() { }

        /// <summary>
        /// Initialize the reader
        /// </summary>
        internal void Init(TypeModel model, SerializationContext context)
        {
            _model = model;

            if (context == null) { context = SerializationContext.Default; }
            else { context.Freeze(); }
            this.context = context;
            _position64 = 0;
            _depth = _fieldNumber = 0;
            
            blockEnd64 = long.MaxValue;
            internStrings = RuntimeTypeModel.Default.InternStrings;
            wireType = WireType.None;
            trapCount = 1;
            if (netCache == null) netCache = new NetObjectCache();
        }

        private SerializationContext context;

        /// <summary>
        /// Addition information about this deserialization operation.
        /// </summary>
        public SerializationContext Context => context;

        /// <summary>
        /// Releases resources used by the reader, but importantly <b>does not</b> Dispose the 
        /// underlying stream; in many typical use-cases the stream is used for different
        /// processes, so it is assumed that the consumer will Dispose their stream separately.
        /// </summary>
        public virtual void Dispose()
        {
            _model = null;
            if (stringInterner != null)
            {
                stringInterner.Clear();
                stringInterner = null;
            }
            if (netCache != null) netCache.Clear();
        }

        /// <summary>
        /// Reads an unsigned 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public abstract uint ReadUInt32();
        
        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public int Position { get { return checked((int)LongPosition); } }

        /// <summary>
        /// Returns the position of the current reader (note that this is not necessarily the same as the position
        /// in the underlying stream, if multiple readers are used on the same stream)
        /// </summary>
        public long LongPosition { get { return _position64; } }

        private protected void Advance(long count) => _position64 += count;
        
        /// <summary>
        /// Reads a signed 16-bit integer from the stream: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public short ReadInt16()
        {
            checked { return (short)ReadInt32(); }
        }
        /// <summary>
        /// Reads an unsigned 16-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public ushort ReadUInt16()
        {
            checked { return (ushort)ReadUInt32(); }
        }

        /// <summary>
        /// Reads an unsigned 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public byte ReadByte()
        {
            checked { return (byte)ReadUInt32(); }
        }

        /// <summary>
        /// Reads a signed 8-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public sbyte ReadSByte()
        {
            checked { return (sbyte)ReadInt32(); }
        }

        /// <summary>
        /// Reads a signed 32-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public abstract int ReadInt32();

        private const long Int64Msb = ((long)1) << 63;
        private const int Int32Msb = ((int)1) << 31;
        private protected static int Zag(uint ziggedValue)
        {
            int value = (int)ziggedValue;
            return (-(value & 0x01)) ^ ((value >> 1) & ~ProtoReader.Int32Msb);
        }

        private protected static long Zag(ulong ziggedValue)
        {
            long value = (long)ziggedValue;
            return (-(value & 0x01L)) ^ ((value >> 1) & ~ProtoReader.Int64Msb);
        }
        /// <summary>
        /// Reads a signed 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64, SignedVariant
        /// </summary>
        public abstract long ReadInt64();

        private Dictionary<string, string> stringInterner;
        private protected string Intern(string value)
        {
            if (value == null) return null;
            if (value.Length == 0) return "";
            if (stringInterner == null)
            {
                stringInterner = new Dictionary<string, string>
                {
                    { value, value }
                };
            }
            else if (stringInterner.TryGetValue(value, out string found))
            {
                value = found;
            }
            else
            {
                stringInterner.Add(value, value);
            }
            return value;
        }

#if COREFX
        private protected static readonly Encoding UTF8 = Encoding.UTF8;
#else
        private protected readonly UTF8Encoding UTF8 = new UTF8Encoding();
#endif
        /// <summary>
        /// Reads a string from the stream (using UTF8); supported wire-types: String
        /// </summary>
        public abstract string ReadString();

        /// <summary>
        /// Throws an exception indication that the given value cannot be mapped to an enum.
        /// </summary>
        public void ThrowEnumException(Type type, int value)
        {
            string desc = type == null ? "<null>" : type.FullName;
            throw AddErrorData(new ProtoException("No " + desc + " enum is mapped to the wire-value " + value.ToString()), this);
        }

        private protected Exception CreateWireTypeException()
        {
            return CreateException("Invalid wire-type; this usually means you have over-written a file without truncating or setting the length; see https://stackoverflow.com/q/2152978/23354");
        }

        private Exception CreateException(string message)
        {
            return AddErrorData(new ProtoException(message), this);
        }
        /// <summary>
        /// Reads a double-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        public
#if !FEAT_SAFE
 unsafe
#endif
 double ReadDouble()
        {
            switch (wireType)
            {
                case WireType.Fixed32:
                    return ReadSingle();
                case WireType.Fixed64:
                    long value = ReadInt64();
#if FEAT_SAFE
                    return BitConverter.ToDouble(BitConverter.GetBytes(value), 0);
#else
                    return *(double*)&value;
#endif
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads (merges) a sub-message from the stream, internally calling StartSubItem and EndSubItem, and (in between)
        /// parsing the message in accordance with the model associated with the reader
        /// </summary>
        public static object ReadObject(object value, int key, ProtoReader reader)
        {
            return ReadTypedObject(value, key, reader, null);
        }

        internal static object ReadTypedObject(object value, int key, ProtoReader reader, Type type)
        {
            if (reader._model == null)
            {
                throw AddErrorData(new InvalidOperationException("Cannot deserialize sub-objects unless a model is provided"), reader);
            }
            SubItemToken token = ProtoReader.StartSubItem(reader);
            if (key >= 0)
            {
                value = reader._model.Deserialize(key, value, reader);
            }
            else if (type != null && reader._model.TryDeserializeAuxiliaryType(reader, DataFormat.Default, Serializer.ListItemTag, type, ref value, true, false, true, false, null))
            {
                // ok
            }
            else
            {
                TypeModel.ThrowUnexpectedType(type);
            }
            ProtoReader.EndSubItem(token, reader);
            return value;
        }

        /// <summary>
        /// Makes the end of consuming a nested message in the stream; the stream must be either at the correct EndGroup
        /// marker, or all fields of the sub-message must have been consumed (in either case, this means ReadFieldHeader
        /// should return zero)
        /// </summary>
        public static void EndSubItem(SubItemToken token, ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            long value64 = token.value64;
            switch (reader.wireType)
            {
                case WireType.EndGroup:
                    if (value64 >= 0) throw AddErrorData(new ArgumentException("token"), reader);
                    if (-(int)value64 != reader._fieldNumber) throw reader.CreateException("Wrong group was ended"); // wrong group ended!
                    reader.wireType = WireType.None; // this releases ReadFieldHeader
                    reader._depth--;
                    break;
                // case WireType.None: // TODO reinstate once reads reset the wire-type
                default:
                    if (value64 < reader._position64) throw reader.CreateException($"Sub-message not read entirely; expected {value64}, was {reader._position64}");
                    if (reader.blockEnd64 != reader._position64 && reader.blockEnd64 != long.MaxValue)
                    {
                        throw reader.CreateException("Sub-message not read correctly");
                    }
                    reader.blockEnd64 = value64;
                    reader._depth--;
                    break;
                    /*default:
                        throw reader.BorkedIt(); */
            }
        }

        private protected abstract ulong ReadUInt64Variant();

        /// <summary>
        /// Begins consuming a nested message in the stream; supported wire-types: StartGroup, String
        /// </summary>
        /// <remarks>The token returned must be help and used when callining EndSubItem</remarks>
        public static SubItemToken StartSubItem(ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            switch (reader.wireType)
            {
                case WireType.StartGroup:
                    reader.wireType = WireType.None; // to prevent glitches from double-calling
                    reader._depth++;
                    return new SubItemToken((long)(-reader._fieldNumber));
                case WireType.String:
                    long len = (long)reader.ReadUInt64Variant();
                    if (len < 0) throw AddErrorData(new InvalidOperationException(), reader);
                    long lastEnd = reader.blockEnd64;
                    reader.blockEnd64 = reader._position64 + len;
                    reader._depth++;
                    return new SubItemToken(lastEnd);
                default:
                    throw reader.CreateWireTypeException(); // throws
            }
        }

        private protected bool TryReadUInt32Variant(out uint value)
        {
            int read = TryReadUInt32VariantWithoutMoving(false, out value);
            if (read > 0)
            {
                SkipBytes(read);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Reads a field header from the stream, setting the wire-type and retuning the field number. If no
        /// more fields are available, then 0 is returned. This methods respects sub-messages.
        /// </summary>
        public int ReadFieldHeader()
        {
            // at the end of a group the caller must call EndSubItem to release the
            // reader (which moves the status to Error, since ReadFieldHeader must
            // then be called)
            if (blockEnd64 <= _position64 || wireType == WireType.EndGroup) { return 0; }

            if (TryReadUInt32Variant(out uint tag) && tag != 0)
            {
                wireType = (WireType)(tag & 7);
                _fieldNumber = (int)(tag >> 3);
                if (_fieldNumber < 1) throw new ProtoException("Invalid field in source data: " + _fieldNumber.ToString());
            }
            else
            {
                wireType = WireType.None;
                _fieldNumber = 0;
            }
            if (wireType == ProtoBuf.WireType.EndGroup)
            {
                if (_depth > 0) return 0; // spoof an end, but note we still set the field-number
                throw new ProtoException("Unexpected end-group in source data; this usually means the source data is corrupt");
            }
            return _fieldNumber;
        }
        /// <summary>
        /// Looks ahead to see whether the next field in the stream is what we expect
        /// (typically; what we've just finished reading - for example ot read successive list items)
        /// </summary>
        public bool TryReadFieldHeader(int field)
        {
            // check for virtual end of stream
            if (blockEnd64 <= _position64 || wireType == WireType.EndGroup) { return false; }

            int read = TryReadUInt32VariantWithoutMoving(false, out uint tag);
            WireType tmpWireType; // need to catch this to exclude (early) any "end group" tokens
            if (read > 0 && ((int)tag >> 3) == field
                && (tmpWireType = (WireType)(tag & 7)) != WireType.EndGroup)
            {
                wireType = tmpWireType;
                _fieldNumber = field;
                SkipBytes(read);
                return true;
            }
            return false;
        }

        internal abstract int TryReadUInt32VariantWithoutMoving(bool trimNegative, out uint value);

        /// <summary>
        /// Get the TypeModel associated with this reader
        /// </summary>
        public TypeModel Model { get { return _model; } }

        /// <summary>
        /// Compares the streams current wire-type to the hinted wire-type, updating the reader if necessary; for example,
        /// a Variant may be updated to SignedVariant. If the hinted wire-type is unrelated then no change is made.
        /// </summary>
        public void Hint(WireType wireType)
        {
            if (this.wireType == wireType) { }  // fine; everything as we expect
            else if (((int)wireType & 7) == (int)this.wireType)
            {   // the underling type is a match; we're customising it with an extension
                this.wireType = wireType;
            }
            // note no error here; we're OK about using alternative data
        }

        /// <summary>
        /// Verifies that the stream's current wire-type is as expected, or a specialized sub-type (for example,
        /// SignedVariant) - in which case the current wire-type is updated. Otherwise an exception is thrown.
        /// </summary>
        public void Assert(WireType wireType)
        {
            if (this.wireType == wireType) { }  // fine; everything as we expect
            else if (((int)wireType & 7) == (int)this.wireType)
            {   // the underling type is a match; we're customising it with an extension
                this.wireType = wireType;
            }
            else
            {   // nope; that is *not* what we were expecting!
                throw CreateWireTypeException();
            }
        }

        internal abstract void SkipBytes(long count);

        /// <summary>
        /// Discards the data for the current field.
        /// </summary>
        public void SkipField()
        {
            switch (wireType)
            {
                case WireType.Fixed32:
                    SkipBytes(4);
                    return;
                case WireType.Fixed64:
                    SkipBytes(8);
                    return;
                case WireType.String:
                    long len = (long)ReadUInt64Variant();
                    SkipBytes(len);
                    return;
                case WireType.Variant:
                case WireType.SignedVariant:
                    ReadUInt64Variant(); // and drop it
                    return;
                case WireType.StartGroup:
                    int originalFieldNumber = this._fieldNumber;
                    _depth++; // need to satisfy the sanity-checks in ReadFieldHeader
                    while (ReadFieldHeader() > 0) { SkipField(); }
                    _depth--;
                    if (wireType == WireType.EndGroup && _fieldNumber == originalFieldNumber)
                    { // we expect to exit in a similar state to how we entered
                        wireType = ProtoBuf.WireType.None;
                        return;
                    }
                    throw CreateWireTypeException();
                case WireType.None: // treat as explicit errorr
                case WireType.EndGroup: // treat as explicit error
                default: // treat as implicit error
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads an unsigned 64-bit integer from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        public abstract ulong ReadUInt64();

        /// <summary>
        /// Reads a single-precision number from the stream; supported wire-types: Fixed32, Fixed64
        /// </summary>
        public
#if !FEAT_SAFE
 unsafe
#endif
 float ReadSingle()
        {
            switch (wireType)
            {
                case WireType.Fixed32:
                    {
                        int value = ReadInt32();
#if FEAT_SAFE
                        return BitConverter.ToSingle(BitConverter.GetBytes(value), 0);
#else
                        return *(float*)&value;
#endif
                    }
                case WireType.Fixed64:
                    {
                        double value = ReadDouble();
                        float f = (float)value;
                        if (float.IsInfinity(f) && !double.IsInfinity(value))
                        {
                            throw AddErrorData(new OverflowException(), this);
                        }
                        return f;
                    }
                default:
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Reads a boolean value from the stream; supported wire-types: Variant, Fixed32, Fixed64
        /// </summary>
        /// <returns></returns>
        public bool ReadBoolean()
        {
            switch (ReadUInt32())
            {
                case 0: return false;
                case 1: return true;
                default: throw CreateException("Unexpected boolean value");
            }
        }

        private protected static readonly byte[] EmptyBlob = new byte[0];
        /// <summary>
        /// Reads a byte-sequence from the stream, appending them to an existing byte-sequence (which can be null); supported wire-types: String
        /// </summary>
        public static byte[] AppendBytes(byte[] value, ProtoReader reader)
            => reader.AppendBytes(value);

        private protected abstract byte[] AppendBytes(byte[] value);
        
        //static byte[] ReadBytes(Stream stream, int length)
        //{
        //    if (stream == null) throw new ArgumentNullException("stream");
        //    if (length < 0) throw new ArgumentOutOfRangeException("length");
        //    byte[] buffer = new byte[length];
        //    int offset = 0, read;
        //    while (length > 0 && (read = stream.Read(buffer, offset, length)) > 0)
        //    {
        //        length -= read;
        //    }
        //    if (length > 0) throw EoF(null);
        //    return buffer;
        //}
        private static int ReadByteOrThrow(Stream source)
        {
            int val = source.ReadByte();
            if (val < 0) throw EoF(null);
            return val;
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber)
            => ReadLengthPrefix(source, expectHeader, style, out fieldNumber, out int bytesRead);

        /// <summary>
        /// Reads a little-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadLittleEndianInt32(Stream source)
        {
            return ReadByteOrThrow(source)
                | (ReadByteOrThrow(source) << 8)
                | (ReadByteOrThrow(source) << 16)
                | (ReadByteOrThrow(source) << 24);
        }

        /// <summary>
        /// Reads a big-endian encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadBigEndianInt32(Stream source)
        {
            return (ReadByteOrThrow(source) << 24)
                 | (ReadByteOrThrow(source) << 16)
                 | (ReadByteOrThrow(source) << 8)
                 | ReadByteOrThrow(source);
        }

        /// <summary>
        /// Reads a varint encoded integer. An exception is thrown if the data is not all available.
        /// </summary>
        public static int DirectReadVarintInt32(Stream source)
        {
            int bytes = TryReadUInt64Variant(source, out ulong val);
            if (bytes <= 0) throw EoF(null);
            return checked((int)val);
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source into a pre-existing buffer. An exception is thrown if the data is not all available.
        /// </summary>
        public static void DirectReadBytes(Stream source, byte[] buffer, int offset, int count)
        {
            int read;
            if (source == null) throw new ArgumentNullException("source");
            while (count > 0 && (read = source.Read(buffer, offset, count)) > 0)
            {
                count -= read;
                offset += read;
            }
            if (count > 0) throw EoF(null);
        }

        /// <summary>
        /// Reads a given number of bytes directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static byte[] DirectReadBytes(Stream source, int count)
        {
            byte[] buffer = new byte[count];
            DirectReadBytes(source, buffer, 0, count);
            return buffer;
        }

        /// <summary>
        /// Reads a string (of a given lenth, in bytes) directly from the source. An exception is thrown if the data is not all available.
        /// </summary>
        public static string DirectReadString(Stream source, int length)
        {
            byte[] buffer = new byte[length];
            DirectReadBytes(source, buffer, 0, length);
            return Encoding.UTF8.GetString(buffer, 0, length);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static int ReadLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            if (style == PrefixStyle.None)
            {
                bytesRead = fieldNumber = 0;
                return int.MaxValue; // avoid the long.maxvalue causing overflow
            }
            long len64 = ReadLongLengthPrefix(source, expectHeader, style, out fieldNumber, out bytesRead);
            return checked((int)len64);
        }

        /// <summary>
        /// Reads the length-prefix of a message from a stream without buffering additional data, allowing a fixed-length
        /// reader to be created.
        /// </summary>
        public static long ReadLongLengthPrefix(Stream source, bool expectHeader, PrefixStyle style, out int fieldNumber, out int bytesRead)
        {
            fieldNumber = 0;
            switch (style)
            {
                case PrefixStyle.None:
                    bytesRead = 0;
                    return long.MaxValue;
                case PrefixStyle.Base128:
                    ulong val;
                    int tmpBytesRead;
                    bytesRead = 0;
                    if (expectHeader)
                    {
                        tmpBytesRead = ProtoReader.TryReadUInt64Variant(source, out val);
                        bytesRead += tmpBytesRead;
                        if (tmpBytesRead > 0)
                        {
                            if ((val & 7) != (uint)WireType.String)
                            { // got a header, but it isn't a string
                                throw new InvalidOperationException();
                            }
                            fieldNumber = (int)(val >> 3);
                            tmpBytesRead = ProtoReader.TryReadUInt64Variant(source, out val);
                            bytesRead += tmpBytesRead;
                            if (bytesRead == 0)
                            { // got a header, but no length
                                throw EoF(null);
                            }
                            return (long)val;
                        }
                        else
                        { // no header
                            bytesRead = 0;
                            return -1;
                        }
                    }
                    // check for a length
                    tmpBytesRead = ProtoReader.TryReadUInt64Variant(source, out val);
                    bytesRead += tmpBytesRead;
                    return bytesRead < 0 ? -1 : (long)val;

                case PrefixStyle.Fixed32:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return b
                             | (ReadByteOrThrow(source) << 8)
                             | (ReadByteOrThrow(source) << 16)
                             | (ReadByteOrThrow(source) << 24);
                    }
                case PrefixStyle.Fixed32BigEndian:
                    {
                        int b = source.ReadByte();
                        if (b < 0)
                        {
                            bytesRead = 0;
                            return -1;
                        }
                        bytesRead = 4;
                        return (b << 24)
                            | (ReadByteOrThrow(source) << 16)
                            | (ReadByteOrThrow(source) << 8)
                            | ReadByteOrThrow(source);
                    }
                default:
                    throw new ArgumentOutOfRangeException("style");
            }
        }

        /// <returns>The number of bytes consumed; 0 if no data available</returns>
        private static int TryReadUInt64Variant(Stream source, out ulong value)
        {
            value = 0;
            int b = source.ReadByte();
            if (b < 0) { return 0; }
            value = (uint)b;
            if ((value & 0x80) == 0) { return 1; }
            value &= 0x7F;
            int bytesRead = 1, shift = 7;
            while (bytesRead < 9)
            {
                b = source.ReadByte();
                if (b < 0) throw EoF(null);
                value |= ((ulong)b & 0x7F) << shift;
                shift += 7;
                bytesRead++;

                if ((b & 0x80) == 0) return bytesRead;
            }
            b = source.ReadByte();
            if (b < 0) throw EoF(null);
            if ((b & 1) == 0) // only use 1 bit from the last byte
            {
                value |= ((ulong)b & 0x7F) << shift;
                return ++bytesRead;
            }
            throw new OverflowException();
        }

        internal static void Seek(Stream source, long count, byte[] buffer)
        {
            if (source.CanSeek)
            {
                source.Seek(count, SeekOrigin.Current);
                count = 0;
            }
            else if (buffer != null)
            {
                int bytesRead;
                while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    count -= bytesRead;
                }
                while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                {
                    count -= bytesRead;
                }
            }
            else // borrow a buffer
            {
                buffer = BufferPool.GetBuffer();
                try
                {
                    int bytesRead;
                    while (count > buffer.Length && (bytesRead = source.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        count -= bytesRead;
                    }
                    while (count > 0 && (bytesRead = source.Read(buffer, 0, (int)count)) > 0)
                    {
                        count -= bytesRead;
                    }
                }
                finally
                {
                    BufferPool.ReleaseBufferToPool(ref buffer);
                }
            }
            if (count > 0) throw EoF(null);
        }
        internal static Exception AddErrorData(Exception exception, ProtoReader source)
        {
#if !CF && !PORTABLE
            if (exception != null && source != null && !exception.Data.Contains("protoSource"))
            {
                exception.Data.Add("protoSource", string.Format("tag={0}; wire-type={1}; offset={2}; depth={3}",
                    source._fieldNumber, source.wireType, source.LongPosition, source._depth));
            }
#endif
            return exception;
        }

        /// <summary>
        /// Create an EOF
        /// </summary>
        protected static Exception EoF(ProtoReader reader)
        {
            return AddErrorData(new EndOfStreamException(), reader);
        }

        /// <summary>
        /// Copies the current field into the instance as extension data
        /// </summary>
        public void AppendExtensionData(IExtensible instance)
        {
            if (instance == null) throw new ArgumentNullException(nameof(instance));
            IExtension extn = instance.GetExtensionObject(true);
            bool commit = false;
            // unusually we *don't* want "using" here; the "finally" does that, with
            // the extension object being responsible for disposal etc
            Stream dest = extn.BeginAppend();
            try
            {
                //TODO: replace this with stream-based, buffered raw copying
                using (ProtoWriter writer = ProtoWriter.Create(dest, _model, null))
                {
                    AppendExtensionField(writer);
                    writer.Close();
                }
                commit = true;
            }
            finally { extn.EndAppend(dest, commit); }
        }

        private void AppendExtensionField(ProtoWriter writer)
        {
            //TODO: replace this with stream-based, buffered raw copying
            ProtoWriter.WriteFieldHeader(_fieldNumber, wireType, writer);
            switch (wireType)
            {
                case WireType.Fixed32:
                    ProtoWriter.WriteInt32(ReadInt32(), writer);
                    return;
                case WireType.Variant:
                case WireType.SignedVariant:
                case WireType.Fixed64:
                    ProtoWriter.WriteInt64(ReadInt64(), writer);
                    return;
                case WireType.String:
                    ProtoWriter.WriteBytes(AppendBytes(null, this), writer);
                    return;
                case WireType.StartGroup:
                    SubItemToken readerToken = StartSubItem(this),
                        writerToken = ProtoWriter.StartSubItem(null, writer);
                    while (ReadFieldHeader() > 0) { AppendExtensionField(writer); }
                    EndSubItem(readerToken, this);
                    ProtoWriter.EndSubItem(writerToken, writer);
                    return;
                case WireType.None: // treat as explicit errorr
                case WireType.EndGroup: // treat as explicit error
                default: // treat as implicit error
                    throw CreateWireTypeException();
            }
        }

        /// <summary>
        /// Indicates whether the reader still has data remaining in the current sub-item,
        /// additionally setting the wire-type for the next field if there is more data.
        /// This is used when decoding packed data.
        /// </summary>
        public static bool HasSubValue(ProtoBuf.WireType wireType, ProtoReader source)
        {
            if (source == null) throw new ArgumentNullException("source");
            // check for virtual end of stream
            if (source.blockEnd64 <= source.LongPosition || wireType == WireType.EndGroup) { return false; }
            source.wireType = wireType;
            return true;
        }

        internal int GetTypeKey(ref Type type)
        {
            return _model.GetKey(ref type);
        }

        internal NetObjectCache NetCache => netCache;

        internal Type DeserializeType(string value)
        {
            return TypeModel.DeserializeType(_model, value);
        }

        internal void SetRootObject(object value)
        {
            netCache.SetKeyedObject(NetObjectCache.Root, value);
            trapCount--;
        }

        /// <summary>
        /// Utility method, not intended for public use; this helps maintain the root object is complex scenarios
        /// </summary>
        public static void NoteObject(object value, ProtoReader reader)
        {
            if (reader == null) throw new ArgumentNullException("reader");
            if (reader.trapCount != 0)
            {
                reader.netCache.RegisterTrappedObject(value);
                reader.trapCount--;
            }
        }

        /// <summary>
        /// Reads a Type from the stream, using the model's DynamicTypeFormatting if appropriate; supported wire-types: String
        /// </summary>
        public Type ReadType()
        {
            return TypeModel.DeserializeType(_model, ReadString());
        }

        internal void TrapNextObject(int newObjectKey)
        {
            trapCount++;
            netCache.SetKeyedObject(newObjectKey, null); // use null as a temp
        }

        internal abstract void CheckFullyConsumed();
        
        /// <summary>
        /// Merge two objects using the details from the current reader; this is used to change the type
        /// of objects when an inheritance relationship is discovered later than usual during deserilazation.
        /// </summary>
        public static object Merge(ProtoReader parent, object from, object to)
        {
            if (parent == null) throw new ArgumentNullException("parent");
            TypeModel model = parent.Model;
            SerializationContext ctx = parent.Context;
            if (model == null) throw new InvalidOperationException("Types cannot be merged unless a type-model has been specified");
            using (var ms = new MemoryStream())
            {
                model.Serialize(ms, from, ctx);
                ms.Position = 0;
                return model.Deserialize(ms, to, null);
            }
        }


        internal virtual void Recycle() { }
    }
}
