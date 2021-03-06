﻿// Copyright (c) Microsoft Open Technologies, Inc. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.AspNet.Http.Interfaces;
using Microsoft.Framework.Cache.Distributed;
using Microsoft.Framework.Logging;

namespace Microsoft.AspNet.Session
{
    public class DistributedSession : ISession
    {
        private const byte SerializationRevision = 1;
        private const int KeyLengthLimit = ushort.MaxValue;

        private readonly IDistributedCache _cache;
        private readonly string _sessionId;
        private readonly TimeSpan _idleTimeout;
        private readonly Func<bool> _tryEstablishSession;
        private readonly IDictionary<EncodedKey, byte[]> _store;
        private readonly ILogger _logger;
        private bool _isModified;
        private bool _loaded;
        private bool _isNewSessionKey;

        public DistributedSession([NotNull] IDistributedCache cache, [NotNull] string sessionId, TimeSpan idleTimeout,
            [NotNull] Func<bool> tryEstablishSession, [NotNull] ILoggerFactory loggerFactory, bool isNewSessionKey)
        {
            _cache = cache;
            _sessionId = sessionId;
            _idleTimeout = idleTimeout;
            _tryEstablishSession = tryEstablishSession;
            _store = new Dictionary<EncodedKey, byte[]>();
            _logger = loggerFactory.Create<DistributedSession>();
            _isNewSessionKey = isNewSessionKey;
        }

        public IEnumerable<string> Keys
        {
            get
            {
                Load(); // TODO: Silent failure
                return _store.Keys.Select(key => key.KeyString);
            }
        }

        public bool TryGetValue(string key, out byte[] value)
        {
            Load(); // TODO: Silent failure
            return _store.TryGetValue(new EncodedKey(key), out value);
        }

        public void Set(string key, ArraySegment<byte> value)
        {
            var encodedKey = new EncodedKey(key);
            if (encodedKey.KeyBytes.Length > KeyLengthLimit)
            {
                throw new ArgumentOutOfRangeException("key", key,
                    string.Format("The key cannot be longer than '{0}' when encoded with UTF-8.", KeyLengthLimit));
            }
            if (value.Array == null)
            {
                throw new ArgumentException("The ArraySegment<byte>.Array cannot be null.", "value");
            }

            Load();
            if (!_tryEstablishSession())
            {
                throw new InvalidOperationException("The session cannot be established after the response has started.");
            }
            _isModified = true;
            byte[] copy = new byte[value.Count];
            Buffer.BlockCopy(value.Array, value.Offset, copy, 0, value.Count);
            _store[encodedKey] = copy;
        }

        public void Remove(string key)
        {
            Load();
            _isModified |= _store.Remove(new EncodedKey(key));
        }

        public void Clear()
        {
            Load();
            _isModified |= _store.Count > 0;
            _store.Clear();
        }

        // TODO: This should throw if called directly, but most other places it should fail silently (e.g. TryGetValue should just return null).
        public void Load()
        {
            if (!_loaded)
            {
                Stream data;
                if (_cache.TryGetValue(_sessionId, out data))
                {
                    Deserialize(data);
                }
                else if (!_isNewSessionKey)
                {
                    _logger.WriteWarning("Accessing expired session {0}", _sessionId);
                }
                _loaded = true;
            }
        }

        public void Commit()
        {
            if (_isModified)
            {
                Stream data;
                if (_logger.IsEnabled(LogLevel.Information) && !_cache.TryGetValue(_sessionId, out data))
                {
                    _logger.WriteInformation("Session {0} started", _sessionId);
                }
                _isModified = false;
                _cache.Set(_sessionId, context => {
                    context.SetSlidingExpiration(_idleTimeout);
                    Serialize(context.Data);
                });
            }
        }

        // Format:
        // Serialization revision: 1 byte, range 0-255
        // Entry count: 3 bytes, range 0-16,777,215
        // foreach entry:
        //   key name byte length: 2 bytes, range 0-65,535
        //   UTF-8 encoded key name byte[]
        //   data byte length: 4 bytes, range 0-2,147,483,647
        //   data byte[]
        private void Serialize(Stream output)
        {
            output.WriteByte(SerializationRevision);
            SerializeNumAs3Bytes(output, _store.Count);

            foreach (var entry in _store)
            {
                var keyBytes = entry.Key.KeyBytes;
                SerializeNumAs2Bytes(output, keyBytes.Length);
                output.Write(keyBytes, 0, keyBytes.Length);
                SerializeNumAs4Bytes(output, entry.Value.Length);
                output.Write(entry.Value, 0, entry.Value.Length);
            }
        }

        private void Deserialize(Stream content)
        {
            if (content == null || content.ReadByte() != SerializationRevision)
            {
                // TODO: Throw?
                // Replace the un-readable format.
                _isModified = true;
                return;
            }

            int expectedEntries = DeserializeNumFrom3Bytes(content);
            for (int i = 0; i < expectedEntries; i++)
            {
                int keyLength = DeserializeNumFrom2Bytes(content);
                var key = new EncodedKey(content.ReadBytes(keyLength));
                int dataLength = DeserializeNumFrom4Bytes(content);
                _store[key] = content.ReadBytes(dataLength);
            }
        }

        private void SerializeNumAs2Bytes(Stream output, int num)
        {
            if (num < 0 || ushort.MaxValue < num)
            {
                throw new ArgumentOutOfRangeException("num", num, "The value cannot be serialized in two bytes.");
            }
            output.WriteByte((byte)(num >> 8));
            output.WriteByte((byte)(0xFF & num));
        }

        private int DeserializeNumFrom2Bytes(Stream content)
        {
            return content.ReadByte() << 8 | content.ReadByte();
        }

        private void SerializeNumAs3Bytes(Stream output, int num)
        {
            if (num < 0 || 0xFFFFFF < num)
            {
                throw new ArgumentOutOfRangeException("num", num, "The value cannot be serialized in three bytes.");
            }
            output.WriteByte((byte)(num >> 16));
            output.WriteByte((byte)(0xFF & (num >> 8)));
            output.WriteByte((byte)(0xFF & num));
        }

        private int DeserializeNumFrom3Bytes(Stream content)
        {
            return content.ReadByte() << 16 | content.ReadByte() << 8 | content.ReadByte();
        }

        private void SerializeNumAs4Bytes(Stream output, int num)
        {
            if (num < 0)
            {
                throw new ArgumentOutOfRangeException("num", num, "The value cannot be negative.");
            }
            output.WriteByte((byte)(num >> 24));
            output.WriteByte((byte)(0xFF & (num >> 16)));
            output.WriteByte((byte)(0xFF & (num >> 8)));
            output.WriteByte((byte)(0xFF & num));
        }

        private int DeserializeNumFrom4Bytes(Stream content)
        {
            return content.ReadByte() << 24 | content.ReadByte() << 16 | content.ReadByte() << 8 | content.ReadByte();
        }

        // Keys are stored in their utf-8 encoded state.
        // This saves us from de-serializing and re-serializing every key on every request.
        private class EncodedKey
        {
            private string _keyString;
            private int? _hashCode;

            internal EncodedKey(string key)
            {
                _keyString = key;
                KeyBytes = Encoding.UTF8.GetBytes(key);
            }

            public EncodedKey(byte[] key)
            {
                KeyBytes = key;
            }

            internal string KeyString
            {
                get
                {
                    if (_keyString == null)
                    {
                        _keyString = Encoding.UTF8.GetString(KeyBytes, 0, KeyBytes.Length);
                    }
                    return _keyString;
                }
            }

            internal byte[] KeyBytes { get; private set; }

            public override bool Equals(object obj)
            {
                var otherKey = obj as EncodedKey;
                if (otherKey == null)
                {
                    return false;
                }
                if (KeyBytes.Length != otherKey.KeyBytes.Length)
                {
                    return false;
                }
                if (_hashCode.HasValue && otherKey._hashCode.HasValue
                    && _hashCode.Value != otherKey._hashCode.Value)
                {
                    return false;
                }
                for (int i = 0; i < KeyBytes.Length; i++)
                {
                    if (KeyBytes[i] != otherKey.KeyBytes[i])
                    {
                        return false;
                    }
                }
                return true;
            }

            public override int GetHashCode()
            {
                if (!_hashCode.HasValue)
                {
                    _hashCode = SipHash.GetHashCode(KeyBytes);
                }
                return _hashCode.Value;
            }

            public override string ToString()
            {
                return KeyString;
            }
        }
    }
}