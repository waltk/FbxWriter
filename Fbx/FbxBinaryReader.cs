﻿using System;
using System.Text;
using System.IO;
using System.IO.Compression;

namespace Fbx
{

    /// <summary>
    /// Reads FBX nodes from a binary stream
    /// </summary>
    public class FbxBinaryReader : FbxBinary
    {
        public readonly BinaryReader binStream;
        public readonly ErrorLevel errorLevel;
        public delegate object ReadPrimitive(BinaryReader reader);

        /// <summary>
        /// Creates a new reader
        /// </summary>
        /// <param name="stream">The stream to read from</param>
        /// <param name="errorLevel">When to throw an <see cref="FbxException"/></param>
        /// <exception cref="ArgumentException"><paramref name="stream"/> does
        /// not support seeking</exception>
        public FbxBinaryReader(Stream stream, ErrorLevel errorLevel = ErrorLevel.Checked)
        {
            if (stream == null)
                throw new ArgumentNullException(stream.ToString());
            if (!stream.CanSeek)
                throw new ArgumentException(
                    "The stream must support seeking. Try reading the data into a buffer first");
            this.binStream = new BinaryReader(stream, Encoding.ASCII);
            this.errorLevel = errorLevel;
        }

        // Reads a single property
        public object ReadProperty()
        {

            var dataType = (char)binStream.ReadByte();
            //Debug.Log(dataType);
            switch (dataType)
            {
                case 'Y':
                    return binStream.ReadInt16();
                case 'C':
                    return (char)binStream.ReadByte();
                case 'I':
                    return binStream.ReadInt32();
                case 'F':
                    return binStream.ReadSingle();
                case 'D':
                    return binStream.ReadDouble();
                case 'L':
                    return binStream.ReadInt64();
                case 'f':
                    return ReadArray(br => br.ReadSingle(), typeof(float));
                case 'd':
                    return ReadArray(br => br.ReadDouble(), typeof(double));
                case 'l':
                    return ReadArray(br => br.ReadInt64(), typeof(long));
                case 'i':
                    return ReadArray(br => br.ReadInt32(), typeof(int));
                case 'b':
                    return ReadArray(br => br.ReadBoolean(), typeof(bool));
                case 'S':
                    var len = binStream.ReadInt32();
                    var str = len == 0 ? "" : Encoding.ASCII.GetString(binStream.ReadBytes(len));
                    // Convert \0\1 to '::' and reverse the tokens
                    if (str.Contains(binarySeparator))
                    {
                        var tokens = str.Split(new[] { binarySeparator }, StringSplitOptions.None);
                        var sb = new StringBuilder();
                        bool first = true;
                        for (int i = tokens.Length - 1; i >= 0; i--)
                        {
                            if (!first)
                                sb.Append(asciiSeparator);
                            sb.Append(tokens[i]);
                            first = false;
                        }
                        str = sb.ToString();
                    }
                    return str;
                case 'R':
                    return binStream.ReadBytes(binStream.ReadInt32());
                default:
                    throw new FbxException(binStream.BaseStream.Position - 1,
                        "Invalid property data type `" + dataType + "'");
            }
        }

        // Reads an array, decompressing it if required
        public Array ReadArray(ReadPrimitive readPrimitive, Type arrayType)
        {
            var len = binStream.ReadInt32();
            var encoding = binStream.ReadInt32();
            var compressedLen = binStream.ReadInt32();
            var ret = Array.CreateInstance(arrayType, len);
            var s = binStream;
            var endPos = binStream.BaseStream.Position + compressedLen;
            if (encoding != 0)
            {
                if (errorLevel >= ErrorLevel.Checked)
                {
                    if (encoding != 1)
                        throw new FbxException(binStream.BaseStream.Position - 1,
                            "Invalid compression encoding (must be 0 or 1)");
                    var cmf = binStream.ReadByte();
                    if ((cmf & 0xF) != 8 || (cmf >> 4) > 7)
                        throw new FbxException(binStream.BaseStream.Position - 1,
                            "Invalid compression format " + cmf);
                    var flg = binStream.ReadByte();
                    if (errorLevel >= ErrorLevel.Strict && ((cmf << 8) + flg) % 31 != 0)
                        throw new FbxException(binStream.BaseStream.Position - 1,
                            "Invalid compression FCHECK");
                    if ((flg & (1 << 5)) != 0)
                        throw new FbxException(binStream.BaseStream.Position - 1,
                            "Invalid compression flags; dictionary not supported");
                }
                else
                {
                    binStream.BaseStream.Position += 2;
                }
                var codec = new DeflateWithChecksum(binStream.BaseStream, CompressionMode.Decompress);
                s = new BinaryReader(codec);
            }
            try
            {
                for (int i = 0; i < len; i++)
                    ret.SetValue(readPrimitive(s), i);
            }
            catch (FbxException) //InvalidDataException
            {
                throw new FbxException(binStream.BaseStream.Position - 1,
                    "Compressed data was malformed");
            }
            if (encoding != 0)
            {
                if (errorLevel >= ErrorLevel.Checked)
                {
                    binStream.BaseStream.Position = endPos - sizeof(int);
                    var checksumBytes = new byte[sizeof(int)];
                    binStream.BaseStream.Read(checksumBytes, 0, checksumBytes.Length);
                    int checksum = 0;
                    for (int i = 0; i < checksumBytes.Length; i++)
                        checksum = (checksum << 8) + checksumBytes[i];
                    if (checksum != ((DeflateWithChecksum)s.BaseStream).Checksum)
                        throw new FbxException(binStream.BaseStream.Position,
                            "Compressed data has invalid checksum");
                }
                else
                {
                    binStream.BaseStream.Position = endPos;
                }
            }
            return ret;
        }

        /// <summary>
        /// Reads a single node.
        /// </summary>
        /// <remarks>
        /// This won't read the file header or footer, and as such will fail if the stream is a full FBX file
        /// </remarks>
        /// <returns>The node</returns>
        /// <exception cref="FbxException">The FBX data was malformed
        /// for the reader's error level</exception>
        public FbxNode ReadNode()
        {
            var endOffset = binStream.ReadInt32();
            var numProperties = binStream.ReadInt32();
            var propertyListLen = binStream.ReadInt32();
            var nameLen = binStream.ReadByte();
            var name = nameLen == 0 ? "" : Encoding.ASCII.GetString(binStream.ReadBytes(nameLen));

            if (endOffset == 0)
            {
                // The end offset should only be 0 in a null node
                if (errorLevel >= ErrorLevel.Checked && (numProperties != 0 || propertyListLen != 0 || !string.IsNullOrEmpty(name)))
                    throw new FbxException(binStream.BaseStream.Position,
                        "Invalid node; expected NULL record");
                return null;
            }

            var node = new FbxNode { Name = name };

            var propertyEnd = binStream.BaseStream.Position + propertyListLen;
            // Read properties
            for (int i = 0; i < numProperties; i++)
            {
                node.Properties.Add(ReadProperty());
            }
     
            if (errorLevel >= ErrorLevel.Checked && binStream.BaseStream.Position != propertyEnd)
                throw new FbxException(binStream.BaseStream.Position,
                    "Too many bytes in property list, end point is " + propertyEnd);

            // Read nested nodes
            var listLen = endOffset - binStream.BaseStream.Position;
            if (errorLevel >= ErrorLevel.Checked && listLen < 0)
                throw new FbxException(binStream.BaseStream.Position,
                    "Node has invalid end point");
            if (listLen > 0)
            {
                FbxNode nested;
                do
                {
                    nested = ReadNode();
                    node.Nodes.Add(nested);
                } while (nested != null);
                if (errorLevel >= ErrorLevel.Checked && binStream.BaseStream.Position != endOffset)
                    throw new FbxException(binStream.BaseStream.Position,
                        "Too many bytes in node, end point is " + endOffset);
            }
            return node;
        }

        public FbxDocument Read()
        {
            // Read header
            bool validHeader = ReadHeader(binStream.BaseStream);
            if (errorLevel >= ErrorLevel.Strict && !validHeader)
                throw new FbxException(binStream.BaseStream.Position,
                    "Invalid header string");

            var document = new FbxDocument();// {Version = (FbxVersion) stream.ReadInt32()};
            var fbxVer = binStream.ReadInt32();
            // Read nodes
            var dataPos = binStream.BaseStream.Position;
            FbxNode nested;
            do
            {
                nested = ReadNode();
                if (nested != null)
                    document.Nodes.Add(nested);
            } while (nested != null);

            // Read footer code
            var footerCode = new byte[footerCodeSize];
            binStream.BaseStream.Read(footerCode, 0, footerCode.Length);
            if (errorLevel >= ErrorLevel.Strict)
            {
                var validCode = GenerateFooterCode(document);
                if (!CheckEqual(footerCode, validCode))
                    throw new FbxException(binStream.BaseStream.Position - footerCodeSize,
                        "Incorrect footer code");
            }

            // Read footer extension
            dataPos = binStream.BaseStream.Position;
            var validFooterExtension = CheckFooter(binStream, document.Version);
            if (errorLevel >= ErrorLevel.Strict && !validFooterExtension)
                throw new FbxException(dataPos, "Invalid footer");
            return document;
        }
    }

}
