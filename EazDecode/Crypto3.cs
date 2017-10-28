﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace EazDecode
{
    internal class Crypto3
    {
        private const string base64Salt = "dZ58E5Xa0RKqscx+HA3eLBcOcAExpKXCkF9MODmm1wVk8NynKuzorgv8y50USvuaLvlpLbwJWb9hQQSGoZx9kw==";
        private static byte[] salt = Convert.FromBase64String(base64Salt);

        private SymmetricAlgorithm[] _algos = new SymmetricAlgorithm[5];
        private SymmetricAlgorithm _padder;
        private KeyedHashAlgorithm _kha;
        private HashAlgorithm _homebrewHasher;

        private RandomNumberGenerator _rng = new RNGCryptoServiceProvider();

        public Crypto3(string password)
        {
            //get a random provider
            var deriveBytes = new Rfc2898DeriveBytes(password, salt, 3000);

            //fill our list of algorithms
            for (int i = 0; i < _algos.Length; i++)
            {
                var algo = new SymAlgoLengthOptimized(new SymmetricAlgorithm[]
                {
                    new RijndaelManaged(),  //blocksize prob 128 or 256
                    new SymAlgoBlowfish(),  //blocksize 64 (8b)
                    new SymAlgoHomebrew(),  //blocksize 32 (4b)
                });

                algo.Key = deriveBytes.GetBytes(algo.KeySize / 8);
                algo.IV = deriveBytes.GetBytes(algo.IVSize / 8);
                _algos[i] = algo;
            }

            //create padder algorithm
            _padder = new SymAlgoPadder(new RijndaelManaged()) {Key = deriveBytes.GetBytes(32)};

            //create kha
            _kha = new KeyedHashAlgo(new HashAlgoPadder(new HashAlgoEncryption(new HashAlgoHomebrew(), new SymAlgoHomebrew(new byte[] {
                0xA3, 0x73, 0xF3, 0x68,
                0xA0, 0x4A, 0x89, 0xE9,
                0x92, 0xEC
            }))), deriveBytes.GetBytes(2));

            //create another hasher
            _homebrewHasher = new HashAlgoEncryption(new HashAlgoHomebrew(), new SymAlgoHomebrew(new byte[]
            {
                0xEA, 0x5F, 0x88, 0xF2,
                0xA2, 0x9C, 0x0F, 0xA9,
                0x70, 0x9E
            }));
        }

        public string Decrypt(byte[] enc)
        {
            enc = TrimChecksumByte(enc);
            enc = RunThroughAlgoArray(enc, false);
            enc = Depad(enc);
            enc = RemoveTrailingNullBytes(enc);
            enc = ApplyXor(enc);
            //TODO: SymbolDecompressor!!
            return "";
        }

        /// <summary>
        /// Mostly used for verification. Trims the last byte and uses it as a 
        /// checksum.
        /// </summary>
        /// <param name="input">Input byte[], including the checksum byte at 
        /// the end</param>
        /// <returns>Input without checksum byte</returns>
        private byte[] TrimChecksumByte(byte[] input)
        {
            if (input.Length == 0) return input;
            
            int newLen = input.Length - 1;
            lock (_kha) {
                byte hashFirstChar = _kha.ComputeHash(input, 0, newLen)[0];
                byte encLastChar = input[input.Length - 1];
                Debug.Assert(hashFirstChar == encLastChar);
            }

            byte[] ret = new byte[newLen];
            Buffer.BlockCopy(input, 0, ret, 0, newLen);
            return ret;
        }

        /// <summary>
        /// Uses the <seealso cref="_algos"/> array to encrypt and decrypt a 
        /// bunch of times.
        /// </summary>
        /// <param name="buffer">Bytes to process</param>
        /// <param name="encrypt">Whether to encrypt or decrypt</param>
        /// <returns>Encrypted or decrypted bytes, depending on <paramref name="encrypt"/></returns>
        private byte[] RunThroughAlgoArray(byte[] buffer, bool encrypt)
        {
            if (encrypt) {
                for (int i = 0; i < _algos.Length; i++)
                {
                    using (ICryptoTransform t = encrypt ? _algos[i].CreateEncryptor() : _algos[i].CreateDecryptor())
                        buffer = t.TransformFinalBlock(buffer, 0, buffer.Length);

                    encrypt = !encrypt;
                }
            } else {
                for (int i = _algos.Length - 1; i >= 0; i--)
                {
                    using (ICryptoTransform t = encrypt ? _algos[i].CreateEncryptor() : _algos[i].CreateDecryptor())
                        buffer = t.TransformFinalBlock(buffer, 0, buffer.Length);

                    encrypt = !encrypt;
                }
            }

            return buffer;
        }

        /// <summary>
        /// Uses the <seealso cref="_padder"/>'s decryptor to decrypt the 
        /// padding bytes.
        /// </summary>
        /// <param name="enc">Bytes to decrypt</param>
        /// <returns>The decrypted bytes</returns>
        private byte[] Depad(byte[] enc)
        {
            //TODO: prob better to use using
            var memoryStream = new MemoryStream();
            var cryptoStream = new CryptoStream(memoryStream, _padder.CreateDecryptor(), CryptoStreamMode.Write);
            try {
                cryptoStream.Write(enc, 0, enc.Length);
            }
            finally {
                ((IDisposable)cryptoStream).Dispose();
            }
            enc = memoryStream.ToArray();
            return enc;
        }

        /// <summary>
        /// Take the last byte of input <paramref name="data"/> and xor it with
        /// the rest of the array.
        /// </summary>
        /// <param name="data">Array including xor byte</param>
        /// <returns>Xored array w/o last byte</returns>
        private static byte[] ApplyXor(byte[] data)
        {
            if (data.Length == 0) return data;

            int newLen = data.Length - 1;
            Debug.Assert(newLen > 0);

            byte[] ret = new byte[newLen];
            Buffer.BlockCopy(data, 0, ret, 0, newLen);

            for (int i = 0; i < newLen; i++)
                ret[i] ^= data[newLen];

            return ret;
        }

        private static byte[] RemoveTrailingNullBytes(byte[] input)
        {
            int lastNonZero = input.Length;
            while (input[lastNonZero - 1] == 0) {
                lastNonZero--;
                Debug.Assert(lastNonZero != 0);
            }

            byte[] ret = new byte[lastNonZero];
            Buffer.BlockCopy(input, 0, ret, 0, lastNonZero);
            return ret;
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0002\u2002
        /// <para>
        /// This essentially encrypts 0x00, 0x01, 0x02, etc. for as long as we
        /// want, which can be used as padding.
        /// </para>
        /// </summary>
        internal sealed class SymAlgoPadder : SymmetricAlgorithm
        {
            private SymmetricAlgorithm _internalAlgo;

            public SymAlgoPadder(SymmetricAlgorithm internalAlgo)
            {
                LegalBlockSizesValue = new[] { new KeySizes(8, 8, 0) };
                LegalKeySizesValue = internalAlgo.LegalKeySizes;
                BlockSizeValue = 8;     //1 byte
                Mode = CipherMode.ECB;
                Padding = PaddingMode.None;
                internalAlgo.Mode = CipherMode.ECB;
                internalAlgo.Padding = PaddingMode.None;
                _internalAlgo = internalAlgo;
            }

            public override ICryptoTransform CreateEncryptor(byte[] key, byte[] iv) => new CryptTransPadder(_internalAlgo, key);
            public override ICryptoTransform CreateDecryptor(byte[] key, byte[] iv) => new CryptTransPadder(_internalAlgo, key);

            public override void GenerateKey() => KeyValue = _internalAlgo.Key;
            public override void GenerateIV() => IVValue = new byte[0];

            private sealed class CryptTransPadder : ICryptoTransform, IDisposable
            {
                private byte[] _block;
                private ICryptoTransform _encryptor;
                private Queue<byte> _queue = new Queue<byte>();
                
                public CryptTransPadder(SymmetricAlgorithm algo, byte[] key)
                {
                    _block = new byte[algo.BlockSize / 8];
                    _encryptor = algo.CreateEncryptor(key, new byte[algo.BlockSize / 8]);
                }
                
                public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
                {
                    byte[] array = new byte[inputCount];
                    TransformBlock(inputBuffer, inputOffset, inputCount, array, 0);
                    return array;
                }
                
                public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
                {
                    //xor every input byte with the encrypted one
                    for (int i = 0; i < inputCount; i++)
                        outputBuffer[i + outputOffset] = (byte) (inputBuffer[i + inputOffset] ^ Dequeue());

                    return inputCount;
                }
                
                private byte Dequeue()
                {
                    //if the queue is empty, fill it again
                    if (_queue.Count == 0)
                        EnqueueBytes();

                    //dequeue a byte and return it
                    return _queue.Dequeue();
                }
                
                private void EnqueueBytes()
                {
                    //encrypt the block
                    byte[] encrypted = new byte[_block.Length];
                    _encryptor.TransformBlock(_block, 0, _block.Length, encrypted, 0);

                    //increment the block by 1
                    IncrementBlock();

                    //enqueue all encrypted bytes
                    foreach (byte item in encrypted)
                        _queue.Enqueue(item);
                }

                /// <summary>
                /// Treat <seealso cref="_block"/> as an integer and increase it by 1.
                /// </summary>
                private void IncrementBlock()
                {
                    for (int i = _block.Length - 1; i >= 0; i--)
                    {
                        //increment buffer[i]
                        _block[i]++;

                        //if it is not not zero, break
                        if (_block[i] != 0) break;
                    }
                }
                public int InputBlockSize => 1;
                public int OutputBlockSize => 1;
                public bool CanTransformMultipleBlocks => true;
                public bool CanReuseTransform => false;

                public void Dispose() { }
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0006\u2001
        /// <para>
        /// Uses various algorithms in order of descending blocksizes, to prevent padding as much as possible.
        /// </para>
        /// </summary>
        internal sealed class SymAlgoLengthOptimized : SymmetricAlgorithm
        {
            public int IVSize;

            private SymmetricAlgorithm[] _algos;

            public SymAlgoLengthOptimized(IEnumerable<SymmetricAlgorithm> algos)
            {
                //sort input by blocksize
                var l = algos.ToList();
                l.Sort((x, y) => y.BlockSize.CompareTo(x.BlockSize));
                _algos = l.ToArray();

                //set all algos to ECB and get total key size
                int totalKeySize = 0;
                int lastBlockSize = -1;
                foreach (var alg in _algos) {
                    Debug.Assert(lastBlockSize != alg.BlockSize, $"BlockSize being equal to {nameof(lastBlockSize)} would throw an exception");
                    lastBlockSize = alg.BlockSize;
                    totalKeySize += alg.KeySize;
                    alg.Mode = CipherMode.ECB;
                    alg.Padding = PaddingMode.None;
                }

                //set algo settings
                BlockSizeValue = _algos[_algos.Length - 1].BlockSize;   //TODO: also last blocksize?
                LegalBlockSizesValue = new[] {new KeySizes(BlockSizeValue, BlockSizeValue, 0)};
                KeySizeValue = totalKeySize;
                LegalKeySizesValue = new[] {new KeySizes(totalKeySize, totalKeySize, 0)};
                IVSize = _algos[0].BlockSize;
                Mode = CipherMode.ECB;
                Padding = PaddingMode.None;
            }

            public override byte[] IV
            {
                get => base.IV;
                set => IVValue = (byte[]) value.Clone();
            }

            public override ICryptoTransform CreateEncryptor(byte[] key, byte[] iv) => GetCryptoTransform(key, iv, true);
            public override ICryptoTransform CreateDecryptor(byte[] key, byte[] iv) => GetCryptoTransform(key, iv, false);
            private ICryptoTransform GetCryptoTransform(byte[] key, byte[] iv, bool encrypt) => new CryptTrans1(_algos, key, iv, encrypt);

            public override void GenerateKey() => throw new NotSupportedException();
            public override void GenerateIV() => throw new NotSupportedException();

            private sealed class CryptTrans1 : ICryptoTransform, IDisposable
            {
                private SymmetricAlgorithm[] _algos;
                private ICryptoTransform[] _transforms;
                private int _blockSize;
                private bool _encrypt;
                private byte[] _iv;
                private byte[] _key;

                public int InputBlockSize => _blockSize;
                public int OutputBlockSize => _blockSize;
                public bool CanTransformMultipleBlocks => true;
                public bool CanReuseTransform => true;

                public CryptTrans1(SymmetricAlgorithm[] algos, byte[] key, byte[] iv, bool encrypt)
                {
                    _algos = algos;
                    _key = key;
                    _encrypt = encrypt;
                    _iv = iv;
                    _blockSize = algos[algos.Length - 1].BlockSize / 8;
                }

                public void Dispose()
                {
                    if (_transforms == null) return;
                    foreach (ICryptoTransform t in _transforms) t?.Dispose();
                    _transforms = null;
                }

                public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
                {
                    //copy input to output
                    Buffer.BlockCopy(inputBuffer, inputOffset, outputBuffer, outputOffset, inputCount);

                    //create ICryptoTransforms
                    PopulateTransforms();

                    //encrypt or decrypt
                    if (_encrypt)
                        Encrypt(outputBuffer, outputOffset, inputCount);
                    else
                        Decrypt(outputBuffer, outputOffset, inputCount);

                    //kind of useless in this case, but return input size
                    return inputCount;
                }

                private void PopulateTransforms()
                {
                    SymmetricAlgorithm[] algos = _algos;

                    //don't do this if we already did before
                    if (_transforms != null) return;

                    _transforms = new ICryptoTransform[algos.Length];

                    int totalKeySize = 0;
                    for (int i = 0; i < algos.Length; i++)
                    {
                        SymmetricAlgorithm symmetricAlgorithm = algos[i];

                        //get array with key and iv
                        int keySizeBytes = symmetricAlgorithm.KeySize / 8;
                        byte[] key = new byte[keySizeBytes];
                        Buffer.BlockCopy(_key, totalKeySize, key, 0, keySizeBytes);
                        totalKeySize += keySizeBytes;
                        byte[] iv = new byte[symmetricAlgorithm.BlockSize / 8];

                        //get actual transform
                        ICryptoTransform cryptoTransform = _encrypt 
                            ? symmetricAlgorithm.CreateEncryptor(key, iv) 
                            : symmetricAlgorithm.CreateDecryptor(key, iv);

                        //sanity checks
                        Debug.Assert(cryptoTransform.CanReuseTransform);
                        Debug.Assert(symmetricAlgorithm.BlockSize == cryptoTransform.InputBlockSize * 8);
                        Debug.Assert(cryptoTransform.InputBlockSize == cryptoTransform.OutputBlockSize);

                        //store in array
                        _transforms[i] = cryptoTransform;
                    }
                }

                private void Encrypt(byte[] buffer, int offset, int count)
                {
                    //store iv in block
                    byte[] block = new byte[_iv.Length];
                    Buffer.BlockCopy(_iv, 0, block, 0, block.Length);

                    int lastOffset = 0;
                    foreach (ICryptoTransform transform in _transforms)
                    {
                        //calculate size of current "chunk"
                        int blockSize = transform.InputBlockSize;
                        int currentCount = count - lastOffset & ~(blockSize - 1);  //count - rounded lastOffset, eg: count - lastOffset & 0b11111111_11000000
                        int nextOffset = lastOffset + currentCount;

                        for (int i = lastOffset; i < nextOffset; i += blockSize)
                        {
                            //xor buffer with block
                            int bufferOffset = i + offset;
                            XorArray(buffer, bufferOffset, block, 0, blockSize);

                            //decrypt buffer to buffer
                            if (transform.TransformBlock(buffer, bufferOffset, blockSize, buffer, bufferOffset) != blockSize) throw new Exception();

                            //copy buffer to block
                            Buffer.BlockCopy(buffer, bufferOffset, block, 0, blockSize);
                        }

                        //update lastOffset
                        lastOffset = nextOffset;

                        //if we're at the end, stop
                        if (nextOffset == count)
                            break;
                    }
                }

                private void Decrypt(byte[] buffer, int offset, int count)
                {
                    //allocate buffers
                    byte[] block = new byte[_iv.Length];
                    Buffer.BlockCopy(_iv, 0, block, 0, block.Length);
                    byte[] tempBuffer = new byte[block.Length];

                    int lastOffset = 0;
                    foreach (ICryptoTransform transform in _transforms)
                    {
                        //calculate size of current "chunk"
                        int blockSize = transform.InputBlockSize;
                        int currentCount = count - lastOffset & ~(blockSize - 1);   //how much we're doing now
                        int nextOffset = lastOffset + currentCount;

                        for (int i = lastOffset; i < nextOffset; i += blockSize)
                        {
                            //copy buffer to tempBuffer
                            int bufferOffset = i + offset;
                            Buffer.BlockCopy(buffer, bufferOffset, tempBuffer, 0, blockSize);

                            //decrypt buffer into buffer
                            if (transform.TransformBlock(buffer, bufferOffset, blockSize, buffer, bufferOffset) != blockSize) throw new Exception();

                            //xor buffer with block
                            XorArray(buffer, bufferOffset, block, 0, blockSize);

                            //copy tempBuffer to block to xor the next time
                            Buffer.BlockCopy(tempBuffer, 0, block, 0, blockSize);
                        }

                        //update lastOffset
                        lastOffset = nextOffset;

                        //if we're at the end, stop
                        if (nextOffset == count)
                            break;
                    }
                }

                public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
                {
                    byte[] array = new byte[inputCount];
                    TransformBlock(inputBuffer, inputOffset, inputCount, array, 0);
                    return array;
                }

                private static void XorArray(byte[] arr, int offsetArr, byte[] xor, int offsetXor, int size)
                {
                    for (int i = 0; i < size; i++)
                    {
                        arr[offsetArr + i] ^= xor[offsetXor + i];
                    }
                }
            }
        }

        /// <inheritdoc />
        /// <summary>
        ///  \u0008\u2001
        /// <para>
        /// Implementation of Blowfish Cipher
        /// </para>
        /// </summary>
        internal sealed class SymAlgoBlowfish : SymmetricAlgorithm
        {
            private object _lock = new object();
            private byte[] _key;
            private BlowfishKey _blowfishKey;

            public SymAlgoBlowfish()
            {
                LegalBlockSizesValue = new[] { new KeySizes(64, 64, 0) };
                LegalKeySizesValue = new[] { new KeySizes(32, 448, 32) };
                BlockSizeValue = 64;
                KeySizeValue = 256;
            }

            public override ICryptoTransform CreateEncryptor(byte[] key, byte[] iv) => GetCryptoTransform(key, iv, true);
            public override ICryptoTransform CreateDecryptor(byte[] key, byte[] iv) => GetCryptoTransform(key, iv, false);

            private ICryptoTransform GetCryptoTransform(byte[] key, byte[] iv, bool encrypt)
            {
                return new SymAlgoBlowfish.CryptTrans2(GetKey(key), encrypt);
            }

            public override void GenerateKey() => throw new NotImplementedException();
            public override void GenerateIV() => throw new NotImplementedException();

            private BlowfishKey GetKey(byte[] key)
            {
                BlowfishKey blowfishKey = null;
                lock (_lock) {
                    //if keys match, use previous one
                    if (_key != null && key.Length == _key.Length && !key.Where((t, i) => t != _key[i]).Any())
                        blowfishKey = _blowfishKey;
                }
                //and also return it
                if (blowfishKey != null)
                    return blowfishKey;

                //otherwise, make a new one
                blowfishKey = new BlowfishKey(key);
                lock (_lock)
                {
                    _key = key;
                    _blowfishKey = blowfishKey;
                }
                return blowfishKey;
            }

            private sealed class CryptTrans2 : ICryptoTransform, IDisposable
            {
                private BlowfishKey _blowfishKey;
                private bool _encrypt;

                public CryptTrans2(BlowfishKey key, bool encrypt)
                {
                    _blowfishKey = key;
                    _encrypt = encrypt;
                }
                
                public int InputBlockSize => 8;
                public int OutputBlockSize => 8;
                public bool CanTransformMultipleBlocks => true;
                public bool CanReuseTransform => true;

                public void Dispose() { }

                public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
                {
                    for (int i = 0; i < inputCount; i += 8)
                    {
                        if (_encrypt)
                            _blowfishKey.Encrypt(inputBuffer, inputOffset + i, outputBuffer, outputOffset + i);
                        else
                            _blowfishKey.Decrypt(inputBuffer, inputOffset + i, outputBuffer, outputOffset + i);
                    }
                    return inputCount;
                }

                public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
                {
                    byte[] array = new byte[inputCount];
                    TransformBlock(inputBuffer, inputOffset, inputCount, array, 0);
                    return array;
                }
            }

            private sealed class BlowfishKey
            {
                #region keys
                private static readonly uint[] P = {
                    0x243F6A88u, 0x85A308D3u, 0x13198A2Eu, 0x03707344u,
                    0xA4093822u, 0x299F31D0u, 0x082EFA98u, 0xEC4E6C89u,
                    0x452821E6u, 0x38D01377u, 0xBE5466CFu, 0x34E90C6Cu,
                    0xC0AC29B7u, 0xC97C50DDu, 0x3F84D5B5u, 0xB5470917u,
                    0x9216D5D9u, 0x8979FB1Bu
                };

                private static readonly uint[,] S = {
                    {
                        0xD1310BA6u, 0x98DFB5ACu, 0x2FFD72DBu, 0xD01ADFB7u, 0xB8E1AFEDu, 0x6A267E96u, 0xBA7C9045u, 0xF12C7F99u, 0x24A19947u, 0xB3916CF7u, 0x0801F2E2u, 0x858EFC16u, 0x636920D8u, 0x71574E69u, 0xA458FEA3u, 0xF4933D7Eu,
                        0x0D95748Fu, 0x728EB658u, 0x718BCD58u, 0x82154AEEu, 0x7B54A41Du, 0xC25A59B5u, 0x9C30D539u, 0x2AF26013u, 0xC5D1B023u, 0x286085F0u, 0xCA417918u, 0xB8DB38EFu, 0x8E79DCB0u, 0x603A180Eu, 0x6C9E0E8Bu, 0xB01E8A3Eu,
                        0xD71577C1u, 0xBD314B27u, 0x78AF2FDAu, 0x55605C60u, 0xE65525F3u, 0xAA55AB94u, 0x57489862u, 0x63E81440u, 0x55CA396Au, 0x2AAB10B6u, 0xB4CC5C34u, 0x1141E8CEu, 0xA15486AFu, 0x7C72E993u, 0xB3EE1411u, 0x636FBC2Au,
                        0x2BA9C55Du, 0x741831F6u, 0xCE5C3E16u, 0x9B87931Eu, 0xAFD6BA33u, 0x6C24CF5Cu, 0x7A325381u, 0x28958677u, 0x3B8F4898u, 0x6B4BB9AFu, 0xC4BFE81Bu, 0x66282193u, 0x61D809CCu, 0xFB21A991u, 0x487CAC60u, 0x5DEC8032u,
                        0xEF845D5Du, 0xE98575B1u, 0xDC262302u, 0xEB651B88u, 0x23893E81u, 0xD396ACC5u, 0x0F6D6FF3u, 0x83F44239u, 0x2E0B4482u, 0xA4842004u, 0x69C8F04Au, 0x9E1F9B5Eu, 0x21C66842u, 0xF6E96C9Au, 0x670C9C61u, 0xABD388F0u,
                        0x6A51A0D2u, 0xD8542F68u, 0x960FA728u, 0xAB5133A3u, 0x6EEF0B6Cu, 0x137A3BE4u, 0xBA3BF050u, 0x7EFB2A98u, 0xA1F1651Du, 0x39AF0176u, 0x66CA593Eu, 0x82430E88u, 0x8CEE8619u, 0x456F9FB4u, 0x7D84A5C3u, 0x3B8B5EBEu,
                        0xE06F75D8u, 0x85C12073u, 0x401A449Fu, 0x56C16AA6u, 0x4ED3AA62u, 0x363F7706u, 0x1BFEDF72u, 0x429B023Du, 0x37D0D724u, 0xD00A1248u, 0xDB0FEAD3u, 0x49F1C09Bu, 0x075372C9u, 0x80991B7Bu, 0x25D479D8u, 0xF6E8DEF7u,
                        0xE3FE501Au, 0xB6794C3Bu, 0x976CE0BDu, 0x04C006BAu, 0xC1A94FB6u, 0x409F60C4u, 0x5E5C9EC2u, 0x196A2463u, 0x68FB6FAFu, 0x3E6C53B5u, 0x1339B2EBu, 0x3B52EC6Fu, 0x6DFC511Fu, 0x9B30952Cu, 0xCC814544u, 0xAF5EBD09u,
                        0xBEE3D004u, 0xDE334AFDu, 0x660F2807u, 0x192E4BB3u, 0xC0CBA857u, 0x45C8740Fu, 0xD20B5F39u, 0xB9D3FBDBu, 0x5579C0BDu, 0x1A60320Au, 0xD6A100C6u, 0x402C7279u, 0x679F25FEu, 0xFB1FA3CCu, 0x8EA5E9F8u, 0xDB3222F8u,
                        0x3C7516DFu, 0xFD616B15u, 0x2F501EC8u, 0xAD0552ABu, 0x323DB5FAu, 0xFD238760u, 0x53317B48u, 0x3E00DF82u, 0x9E5C57BBu, 0xCA6F8CA0u, 0x1A87562Eu, 0xDF1769DBu, 0xD542A8F6u, 0x287EFFC3u, 0xAC6732C6u, 0x8C4F5573u,
                        0x695B27B0u, 0xBBCA58C8u, 0xE1FFA35Du, 0xB8F011A0u, 0x10FA3D98u, 0xFD2183B8u, 0x4AFCB56Cu, 0x2DD1D35Bu, 0x9A53E479u, 0xB6F84565u, 0xD28E49BCu, 0x4BFB9790u, 0xE1DDF2DAu, 0xA4CB7E33u, 0x62FB1341u, 0xCEE4C6E8u,
                        0xEF20CADAu, 0x36774C01u, 0xD07E9EFEu, 0x2BF11FB4u, 0x95DBDA4Du, 0xAE909198u, 0xEAAD8E71u, 0x6B93D5A0u, 0xD08ED1D0u, 0xAFC725E0u, 0x8E3C5B2Fu, 0x8E7594B7u, 0x8FF6E2FBu, 0xF2122B64u, 0x8888B812u, 0x900DF01Cu,
                        0x4FAD5EA0u, 0x688FC31Cu, 0xD1CFF191u, 0xB3A8C1ADu, 0x2F2F2218u, 0xBE0E1777u, 0xEA752DFEu, 0x8B021FA1u, 0xE5A0CC0Fu, 0xB56F74E8u, 0x18ACF3D6u, 0xCE89E299u, 0xB4A84FE0u, 0xFD13E0B7u, 0x7CC43B81u, 0xD2ADA8D9u,
                        0x165FA266u, 0x80957705u, 0x93CC7314u, 0x211A1477u, 0xE6AD2065u, 0x77B5FA86u, 0xC75442F5u, 0xFB9D35CFu, 0xEBCDAF0Cu, 0x7B3E89A0u, 0xD6411BD3u, 0xAE1E7E49u, 0x00250E2Du, 0x2071B35Eu, 0x226800BBu, 0x57B8E0AFu,
                        0x2464369Bu, 0xF009B91Eu, 0x5563911Du, 0x59DFA6AAu, 0x78C14389u, 0xD95A537Fu, 0x207D5BA2u, 0x02E5B9C5u, 0x83260376u, 0x6295CFA9u, 0x11C81968u, 0x4E734A41u, 0xB3472DCAu, 0x7B14A94Au, 0x1B510052u, 0x9A532915u,
                        0xD60F573Fu, 0xBC9BC6E4u, 0x2B60A476u, 0x81E67400u, 0x08BA6FB5u, 0x571BE91Fu, 0xF296EC6Bu, 0x2A0DD915u, 0xB6636521u, 0xE7B9F9B6u, 0xFF34052Eu, 0xC5855664u, 0x53B02D5Du, 0xA99F8FA1u, 0x08BA4799u, 0x6E85076Au
                    }, {
                        0x4B7A70E9u, 0xB5B32944u, 0xDB75092Eu, 0xC4192623u, 0xAD6EA6B0u, 0x49A7DF7Du, 0x9CEE60B8u, 0x8FEDB266u, 0xECAA8C71u, 0x699A17FFu, 0x5664526Cu, 0xC2B19EE1u, 0x193602A5u, 0x75094C29u, 0xA0591340u, 0xE4183A3Eu,
                        0x3F54989Au, 0x5B429D65u, 0x6B8FE4D6u, 0x99F73FD6u, 0xA1D29C07u, 0xEFE830F5u, 0x4D2D38E6u, 0xF0255DC1u, 0x4CDD2086u, 0x8470EB26u, 0x6382E9C6u, 0x021ECC5Eu, 0x09686B3Fu, 0x3EBAEFC9u, 0x3C971814u, 0x6B6A70A1u,
                        0x687F3584u, 0x52A0E286u, 0xB79C5305u, 0xAA500737u, 0x3E07841Cu, 0x7FDEAE5Cu, 0x8E7D44ECu, 0x5716F2B8u, 0xB03ADA37u, 0xF0500C0Du, 0xF01C1F04u, 0x0200B3FFu, 0xAE0CF51Au, 0x3CB574B2u, 0x25837A58u, 0xDC0921BDu,
                        0xD19113F9u, 0x7CA92FF6u, 0x94324773u, 0x22F54701u, 0x3AE5E581u, 0x37C2DADCu, 0xC8B57634u, 0x9AF3DDA7u, 0xA9446146u, 0x0FD0030Eu, 0xECC8C73Eu, 0xA4751E41u, 0xE238CD99u, 0x3BEA0E2Fu, 0x3280BBA1u, 0x183EB331u,
                        0x4E548B38u, 0x4F6DB908u, 0x6F420D03u, 0xF60A04BFu, 0x2CB81290u, 0x24977C79u, 0x5679B072u, 0xBCAF89AFu, 0xDE9A771Fu, 0xD9930810u, 0xB38BAE12u, 0xDCCF3F2Eu, 0x5512721Fu, 0x2E6B7124u, 0x501ADDE6u, 0x9F84CD87u,
                        0x7A584718u, 0x7408DA17u, 0xBC9F9ABCu, 0xE94B7D8Cu, 0xEC7AEC3Au, 0xDB851DFAu, 0x63094366u, 0xC464C3D2u, 0xEF1C1847u, 0x3215D908u, 0xDD433B37u, 0x24C2BA16u, 0x12A14D43u, 0x2A65C451u, 0x50940002u, 0x133AE4DDu,
                        0x71DFF89Eu, 0x10314E55u, 0x81AC77D6u, 0x5F11199Bu, 0x043556F1u, 0xD7A3C76Bu, 0x3C11183Bu, 0x5924A509u, 0xF28FE6EDu, 0x97F1FBFAu, 0x9EBABF2Cu, 0x1E153C6Eu, 0x86E34570u, 0xEAE96FB1u, 0x860E5E0Au, 0x5A3E2AB3u,
                        0x771FE71Cu, 0x4E3D06FAu, 0x2965DCB9u, 0x99E71D0Fu, 0x803E89D6u, 0x5266C825u, 0x2E4CC978u, 0x9C10B36Au, 0xC6150EBAu, 0x94E2EA78u, 0xA5FC3C53u, 0x1E0A2DF4u, 0xF2F74EA7u, 0x361D2B3Du, 0x1939260Fu, 0x19C27960u,
                        0x5223A708u, 0xF71312B6u, 0xEBADFE6Eu, 0xEAC31F66u, 0xE3BC4595u, 0xA67BC883u, 0xB17F37D1u, 0x018CFF28u, 0xC332DDEFu, 0xBE6C5AA5u, 0x65582185u, 0x68AB9802u, 0xEECEA50Fu, 0xDB2F953Bu, 0x2AEF7DADu, 0x5B6E2F84u,
                        0x1521B628u, 0x29076170u, 0xECDD4775u, 0x619F1510u, 0x13CCA830u, 0xEB61BD96u, 0x0334FE1Eu, 0xAA0363CFu, 0xB5735C90u, 0x4C70A239u, 0xD59E9E0Bu, 0xCBAADE14u, 0xEECC86BCu, 0x60622CA7u, 0x9CAB5CABu, 0xB2F3846Eu,
                        0x648B1EAFu, 0x19BDF0CAu, 0xA02369B9u, 0x655ABB50u, 0x40685A32u, 0x3C2AB4B3u, 0x319EE9D5u, 0xC021B8F7u, 0x9B540B19u, 0x875FA099u, 0x95F7997Eu, 0x623D7DA8u, 0xF837889Au, 0x97E32D77u, 0x11ED935Fu, 0x16681281u,
                        0x0E358829u, 0xC7E61FD6u, 0x96DEDFA1u, 0x7858BA99u, 0x57F584A5u, 0x1B227263u, 0x9B83C3FFu, 0x1AC24696u, 0xCDB30AEBu, 0x532E3054u, 0x8FD948E4u, 0x6DBC3128u, 0x58EBF2EFu, 0x34C6FFEAu, 0xFE28ED61u, 0xEE7C3C73u,
                        0x5D4A14D9u, 0xE864B7E3u, 0x42105D14u, 0x203E13E0u, 0x45EEE2B6u, 0xA3AAABEAu, 0xDB6C4F15u, 0xFACB4FD0u, 0xC742F442u, 0xEF6ABBB5u, 0x654F3B1Du, 0x41CD2105u, 0xD81E799Eu, 0x86854DC7u, 0xE44B476Au, 0x3D816250u,
                        0xCF62A1F2u, 0x5B8D2646u, 0xFC8883A0u, 0xC1C7B6A3u, 0x7F1524C3u, 0x69CB7492u, 0x47848A0Bu, 0x5692B285u, 0x095BBF00u, 0xAD19489Du, 0x1462B174u, 0x23820E00u, 0x58428D2Au, 0x0C55F5EAu, 0x1DADF43Eu, 0x233F7061u,
                        0x3372F092u, 0x8D937E41u, 0xD65FECF1u, 0x6C223BDBu, 0x7CDE3759u, 0xCBEE7460u, 0x4085F2A7u, 0xCE77326Eu, 0xA6078084u, 0x19F8509Eu, 0xE8EFD855u, 0x61D99735u, 0xA969A7AAu, 0xC50C06C2u, 0x5A04ABFCu, 0x800BCADCu,
                        0x9E447A2Eu, 0xC3453484u, 0xFDD56705u, 0x0E1E9EC9u, 0xDB73DBD3u, 0x105588CDu, 0x675FDA79u, 0xE3674340u, 0xC5C43465u, 0x713E38D8u, 0x3D28F89Eu, 0xF16DFF20u, 0x153E21E7u, 0x8FB03D4Au, 0xE6E39F2Bu, 0xDB83ADF7u
                    }, {
                        0xE93D5A68u, 0x948140F7u, 0xF64C261Cu, 0x94692934u, 0x411520F7u, 0x7602D4F7u, 0xBCF46B2Eu, 0xD4A20068u, 0xD4082471u, 0x3320F46Au, 0x43B7D4B7u, 0x500061AFu, 0x1E39F62Eu, 0x97244546u, 0x14214F74u, 0xBF8B8840u,
                        0x4D95FC1Du, 0x96B591AFu, 0x70F4DDD3u, 0x66A02F45u, 0xBFBC09ECu, 0x03BD9785u, 0x7FAC6DD0u, 0x31CB8504u, 0x96EB27B3u, 0x55FD3941u, 0xDA2547E6u, 0xABCA0A9Au, 0x28507825u, 0x530429F4u, 0x0A2C86DAu, 0xE9B66DFBu,
                        0x68DC1462u, 0xD7486900u, 0x680EC0A4u, 0x27A18DEEu, 0x4F3FFEA2u, 0xE887AD8Cu, 0xB58CE006u, 0x7AF4D6B6u, 0xAACE1E7Cu, 0xD3375FECu, 0xCE78A399u, 0x406B2A42u, 0x20FE9E35u, 0xD9F385B9u, 0xEE39D7ABu, 0x3B124E8Bu,
                        0x1DC9FAF7u, 0x4B6D1856u, 0x26A36631u, 0xEAE397B2u, 0x3A6EFA74u, 0xDD5B4332u, 0x6841E7F7u, 0xCA7820FBu, 0xFB0AF54Eu, 0xD8FEB397u, 0x454056ACu, 0xBA489527u, 0x55533A3Au, 0x20838D87u, 0xFE6BA9B7u, 0xD096954Bu,
                        0x55A867BCu, 0xA1159A58u, 0xCCA92963u, 0x99E1DB33u, 0xA62A4A56u, 0x3F3125F9u, 0x5EF47E1Cu, 0x9029317Cu, 0xFDF8E802u, 0x04272F70u, 0x80BB155Cu, 0x05282CE3u, 0x95C11548u, 0xE4C66D22u, 0x48C1133Fu, 0xC70F86DCu,
                        0x07F9C9EEu, 0x41041F0Fu, 0x404779A4u, 0x5D886E17u, 0x325F51EBu, 0xD59BC0D1u, 0xF2BCC18Fu, 0x41113564u, 0x257B7834u, 0x602A9C60u, 0xDFF8E8A3u, 0x1F636C1Bu, 0x0E12B4C2u, 0x02E1329Eu, 0xAF664FD1u, 0xCAD18115u,
                        0x6B2395E0u, 0x333E92E1u, 0x3B240B62u, 0xEEBEB922u, 0x85B2A20Eu, 0xE6BA0D99u, 0xDE720C8Cu, 0x2DA2F728u, 0xD0127845u, 0x95B794FDu, 0x647D0862u, 0xE7CCF5F0u, 0x5449A36Fu, 0x877D48FAu, 0xC39DFD27u, 0xF33E8D1Eu,
                        0x0A476341u, 0x992EFF74u, 0x3A6F6EABu, 0xF4F8FD37u, 0xA812DC60u, 0xA1EBDDF8u, 0x991BE14Cu, 0xDB6E6B0Du, 0xC67B5510u, 0x6D672C37u, 0x2765D43Bu, 0xDCD0E804u, 0xF1290DC7u, 0xCC00FFA3u, 0xB5390F92u, 0x690FED0Bu,
                        0x667B9FFBu, 0xCEDB7D9Cu, 0xA091CF0Bu, 0xD9155EA3u, 0xBB132F88u, 0x515BAD24u, 0x7B9479BFu, 0x763BD6EBu, 0x37392EB3u, 0xCC115979u, 0x8026E297u, 0xF42E312Du, 0x6842ADA7u, 0xC66A2B3Bu, 0x12754CCCu, 0x782EF11Cu,
                        0x6A124237u, 0xB79251E7u, 0x06A1BBE6u, 0x4BFB6350u, 0x1A6B1018u, 0x11CAEDFAu, 0x3D25BDD8u, 0xE2E1C3C9u, 0x44421659u, 0x0A121386u, 0xD90CEC6Eu, 0xD5ABEA2Au, 0x64AF674Eu, 0xDA86A85Fu, 0xBEBFE988u, 0x64E4C3FEu,
                        0x9DBC8057u, 0xF0F7C086u, 0x60787BF8u, 0x6003604Du, 0xD1FD8346u, 0xF6381FB0u, 0x7745AE04u, 0xD736FCCCu, 0x83426B33u, 0xF01EAB71u, 0xB0804187u, 0x3C005E5Fu, 0x77A057BEu, 0xBDE8AE24u, 0x55464299u, 0xBF582E61u,
                        0x4E58F48Fu, 0xF2DDFDA2u, 0xF474EF38u, 0x8789BDC2u, 0x5366F9C3u, 0xC8B38E74u, 0xB475F255u, 0x46FCD9B9u, 0x7AEB2661u, 0x8B1DDF84u, 0x846A0E79u, 0x915F95E2u, 0x466E598Eu, 0x20B45770u, 0x8CD55591u, 0xC902DE4Cu,
                        0xB90BACE1u, 0xBB8205D0u, 0x11A86248u, 0x7574A99Eu, 0xB77F19B6u, 0xE0A9DC09u, 0x662D09A1u, 0xC4324633u, 0xE85A1F02u, 0x09F0BE8Cu, 0x4A99A025u, 0x1D6EFE10u, 0x1AB93D1Du, 0x0BA5A4DFu, 0xA186F20Fu, 0x2868F169u,
                        0xDCB7DA83u, 0x573906FEu, 0xA1E2CE9Bu, 0x4FCD7F52u, 0x50115E01u, 0xA70683FAu, 0xA002B5C4u, 0x0DE6D027u, 0x9AF88C27u, 0x773F8641u, 0xC3604C06u, 0x61A806B5u, 0xF0177A28u, 0xC0F586E0u, 0x006058AAu, 0x30DC7D62u,
                        0x11E69ED7u, 0x2338EA63u, 0x53C2DD94u, 0xC2C21634u, 0xBBCBEE56u, 0x90BCB6DEu, 0xEBFC7DA1u, 0xCE591D76u, 0x6F05E409u, 0x4B7C0188u, 0x39720A3Du, 0x7C927C24u, 0x86E3725Fu, 0x724D9DB9u, 0x1AC15BB4u, 0xD39EB8FCu,
                        0xED545578u, 0x08FCA5B5u, 0xD83D7CD3u, 0x4DAD0FC4u, 0x1E50EF5Eu, 0xB161E6F8u, 0xA28514D9u, 0x6C51133Cu, 0x6FD5C7E7u, 0x56E14EC4u, 0x362ABFCEu, 0xDDC6C837u, 0xD79A3234u, 0x92638212u, 0x670EFA8Eu, 0x406000E0u
                    }, {
                        0x3A39CE37u, 0xD3FAF5CFu, 0xABC27737u, 0x5AC52D1Bu, 0x5CB0679Eu, 0x4FA33742u, 0xD3822740u, 0x99BC9BBEu, 0xD5118E9Du, 0xBF0F7315u, 0xD62D1C7Eu, 0xC700C47Bu, 0xB78C1B6Bu, 0x21A19045u, 0xB26EB1BEu, 0x6A366EB4u,
                        0x5748AB2Fu, 0xBC946E79u, 0xC6A376D2u, 0x6549C2C8u, 0x530FF8EEu, 0x468DDE7Du, 0xD5730A1Du, 0x4CD04DC6u, 0x2939BBDBu, 0xA9BA4650u, 0xAC9526E8u, 0xBE5EE304u, 0xA1FAD5F0u, 0x6A2D519Au, 0x63EF8CE2u, 0x9A86EE22u,
                        0xC089C2B8u, 0x43242EF6u, 0xA51E03AAu, 0x9CF2D0A4u, 0x83C061BAu, 0x9BE96A4Du, 0x8FE51550u, 0xBA645BD6u, 0x2826A2F9u, 0xA73A3AE1u, 0x4BA99586u, 0xEF5562E9u, 0xC72FEFD3u, 0xF752F7DAu, 0x3F046F69u, 0x77FA0A59u,
                        0x80E4A915u, 0x87B08601u, 0x9B09E6ADu, 0x3B3EE593u, 0xE990FD5Au, 0x9E34D797u, 0x2CF0B7D9u, 0x022B8B51u, 0x96D5AC3Au, 0x017DA67Du, 0xD1CF3ED6u, 0x7C7D2D28u, 0x1F9F25CFu, 0xADF2B89Bu, 0x5AD6B472u, 0x5A88F54Cu,
                        0xE029AC71u, 0xE019A5E6u, 0x47B0ACFDu, 0xED93FA9Bu, 0xE8D3C48Du, 0x283B57CCu, 0xF8D56629u, 0x79132E28u, 0x785F0191u, 0xED756055u, 0xF7960E44u, 0xE3D35E8Cu, 0x15056DD4u, 0x88F46DBAu, 0x03A16125u, 0x0564F0BDu,
                        0xC3EB9E15u, 0x3C9057A2u, 0x97271AECu, 0xA93A072Au, 0x1B3F6D9Bu, 0x1E6321F5u, 0xF59C66FBu, 0x26DCF319u, 0x7533D928u, 0xB155FDF5u, 0x03563482u, 0x8ABA3CBBu, 0x28517711u, 0xC20AD9F8u, 0xABCC5167u, 0xCCAD925Fu,
                        0x4DE81751u, 0x3830DC8Eu, 0x379D5862u, 0x9320F991u, 0xEA7A90C2u, 0xFB3E7BCEu, 0x5121CE64u, 0x774FBE32u, 0xA8B6E37Eu, 0xC3293D46u, 0x48DE5369u, 0x6413E680u, 0xA2AE0810u, 0xDD6DB224u, 0x69852DFDu, 0x09072166u,
                        0xB39A460Au, 0x6445C0DDu, 0x586CDECFu, 0x1C20C8AEu, 0x5BBEF7DDu, 0x1B588D40u, 0xCCD2017Fu, 0x6BB4E3BBu, 0xDDA26A7Eu, 0x3A59FF45u, 0x3E350A44u, 0xBCB4CDD5u, 0x72EACEA8u, 0xFA6484BBu, 0x8D6612AEu, 0xBF3C6F47u,
                        0xD29BE463u, 0x542F5D9Eu, 0xAEC2771Bu, 0xF64E6370u, 0x740E0D8Du, 0xE75B1357u, 0xF8721671u, 0xAF537D5Du, 0x4040CB08u, 0x4EB4E2CCu, 0x34D2466Au, 0x0115AF84u, 0xE1B00428u, 0x95983A1Du, 0x06B89FB4u, 0xCE6EA048u,
                        0x6F3F3B82u, 0x3520AB82u, 0x011A1D4Bu, 0x277227F8u, 0x611560B1u, 0xE7933FDCu, 0xBB3A792Bu, 0x344525BDu, 0xA08839E1u, 0x51CE794Bu, 0x2F32C9B7u, 0xA01FBAC9u, 0xE01CC87Eu, 0xBCC7D1F6u, 0xCF0111C3u, 0xA1E8AAC7u,
                        0x1A908749u, 0xD44FBD9Au, 0xD0DADECBu, 0xD50ADA38u, 0x0339C32Au, 0xC6913667u, 0x8DF9317Cu, 0xE0B12B4Fu, 0xF79E59B7u, 0x43F5BB3Au, 0xF2D519FFu, 0x27D9459Cu, 0xBF97222Cu, 0x15E6FC2Au, 0x0F91FC71u, 0x9B941525u,
                        0xFAE59361u, 0xCEB69CEBu, 0xC2A86459u, 0x12BAA8D1u, 0xB6C1075Eu, 0xE3056A0Cu, 0x10D25065u, 0xCB03A442u, 0xE0EC6E0Eu, 0x1698DB3Bu, 0x4C98A0BEu, 0x3278E964u, 0x9F1F9532u, 0xE0D392DFu, 0xD3A0342Bu, 0x8971F21Eu,
                        0x1B0A7441u, 0x4BA3348Cu, 0xC5BE7120u, 0xC37632D8u, 0xDF359F8Du, 0x9B992F2Eu, 0xE60B6F47u, 0x0FE3F11Du, 0xE54CDA54u, 0x1EDAD891u, 0xCE6279CFu, 0xCD3E7E6Fu, 0x1618B166u, 0xFD2C1D05u, 0x848FD2C5u, 0xF6FB2299u,
                        0xF523F357u, 0xA6327623u, 0x93A83531u, 0x56CCCD02u, 0xACF08162u, 0x5A75EBB5u, 0x6E163697u, 0x88D273CCu, 0xDE966292u, 0x81B949D0u, 0x4C50901Bu, 0x71C65614u, 0xE6C6C7BDu, 0x327A140Au, 0x45E1D006u, 0xC3F27B9Au,
                        0xC9AA53FDu, 0x62A80F00u, 0xBB25BFE2u, 0x35BDD2F6u, 0x71126905u, 0xB2040222u, 0xB6CBCF7Cu, 0xCD769C2Bu, 0x53113EC0u, 0x1640E3D3u, 0x38ABBD60u, 0x2547ADF0u, 0xBA38209Cu, 0xF746CE76u, 0x77AFA1C5u, 0x20756060u,
                        0x85CBFE4Eu, 0x8AE88DD8u, 0x7AAAF9B0u, 0x4CF9AA7Eu, 0x1948C25Cu, 0x02FB8A8Cu, 0x01C36AE4u, 0xD6EBE1F9u, 0x90D4F869u, 0xA65CDEA0u, 0x3F09252Du, 0xC208E69Fu, 0xB74E6132u, 0xCE77E25Bu, 0x578FDFE3u, 0x3AC372E6u
                    }
                };
                #endregion

                private uint[] _p;
                private uint[,] _s;

                public BlowfishKey(byte[] key)
                {
                    _p = (uint[])P.Clone();
                    _s = (uint[,])S.Clone();

                    short i = 0;
                    for (short j = 0; j < 18; j += 1)
                    {
                        uint data = 0u;
                        for (short k = 0; k < 4; k += 1)
                        {
                            data = data << 8 | key[i];
                            i += 1;
                            if (i >= key.Length)
                            {
                                i = 0;
                            }
                        }
                        _p[j] ^= data;
                    }

                    uint a = 0u;
                    uint b = 0u;
                    for (short j = 0; j < 18; j += 2)
                    {
                        ProcessTableE(ref a, ref b);
                        _p[j + 0] = a;
                        _p[j + 1] = b;
                    }

                    for (short j = 0; j < 4; j += 1)
                    {
                        for (i = 0; i < 256; i += 2)
                        {
                            ProcessTableE(ref a, ref b);
                            _s[j, i + 0] = a;
                            _s[j, i + 1] = b;
                        }
                    }
                }

                private uint F(uint val)
                {
                    ushort num1 = (ushort)(val & 0xFF);
                    val >>= 8;
                    ushort num2 = (ushort)(val & 0xFF);
                    val >>= 8;
                    ushort num3 = (ushort)(val & 0xFF);
                    val >>= 8;
                    ushort num4 = (ushort)(val & 0xFF);
                    return (_s[0, num4] + _s[1, num3] ^ _s[2, num2]) + _s[3, num1];
                }

                public void Encrypt(byte[] inputBuffer, int inputOffset, byte[] outputBuffer, int outputOffset)
                {
                    uint num = (uint)(inputBuffer[inputOffset] << 24 
                        | inputBuffer[inputOffset + 1] << 16 
                        | inputBuffer[inputOffset + 2] << 8 
                        | inputBuffer[inputOffset + 3]);
                    uint num2 = (uint)(inputBuffer[inputOffset + 4] << 24 
                        | inputBuffer[inputOffset + 5] << 16 
                        | inputBuffer[inputOffset + 6] << 8 
                        | inputBuffer[inputOffset + 7]);

                    ProcessTableE(ref num, ref num2);

                    outputBuffer[outputOffset + 0] = (byte)(num >> 24);
                    outputBuffer[outputOffset + 1] = (byte)(num >> 16);
                    outputBuffer[outputOffset + 2] = (byte)(num >> 8);
                    outputBuffer[outputOffset + 3] = (byte)num;
                    outputBuffer[outputOffset + 4] = (byte)(num2 >> 24);
                    outputBuffer[outputOffset + 5] = (byte)(num2 >> 16);
                    outputBuffer[outputOffset + 6] = (byte)(num2 >> 8);
                    outputBuffer[outputOffset + 7] = (byte)num2;
                }

                private void ProcessTableE(ref uint a, ref uint b)
                {
                    for (short i = 0; i < 16; i++)
                    {
                        a ^= _p[i];
                        b = F(a) ^ b;

                        //swap
                        uint temp = a;
                        a = b;
                        b = temp;
                    }

                    //swap
                    uint temp2 = a;
                    a = b;
                    b = temp2;

                    a ^= _p[17];
                    b ^= _p[16];
                }

                public void Decrypt(byte[] inputBuffer, int inputOffset, byte[] outputBuffer, int outputOffset)
                {
                    uint num = (uint)(inputBuffer[inputOffset] << 24 
                        | inputBuffer[inputOffset + 1] << 16 
                        | inputBuffer[inputOffset + 2] << 8 
                        | inputBuffer[inputOffset + 3]);

                    uint num2 = (uint)(inputBuffer[inputOffset + 4] << 24 
                        | inputBuffer[inputOffset + 5] << 16 
                        | inputBuffer[inputOffset + 6] << 8 
                        | inputBuffer[inputOffset + 7]);

                    ProcessTableD(ref num, ref num2);

                    outputBuffer[outputOffset + 0] = (byte)(num >> 24);
                    outputBuffer[outputOffset + 1] = (byte)(num >> 16);
                    outputBuffer[outputOffset + 2] = (byte)(num >> 8);
                    outputBuffer[outputOffset + 3] = (byte)num;
                    outputBuffer[outputOffset + 4] = (byte)(num2 >> 24);
                    outputBuffer[outputOffset + 5] = (byte)(num2 >> 16);
                    outputBuffer[outputOffset + 6] = (byte)(num2 >> 8);
                    outputBuffer[outputOffset + 7] = (byte)num2;
                }

                private void ProcessTableD(ref uint a, ref uint b)
                {
                    for (short i = 17; i > 1; i--)
                    {
                        a ^= _p[i];
                        b = F(a) ^ b;

                        //swap
                        uint temp = a;
                        a = b;
                        b = temp;
                    }

                    //swap
                    uint temp2 = a;
                    a = b;
                    b = temp2;

                    a ^= _p[0];
                    b ^= _p[1];
                }
            }
        }

        /// <inheritdoc />
        /// <summary>
        ///  \u000E\u2002
        /// <para>Some self-made cipher</para>
        /// </summary>
        internal sealed class SymAlgoHomebrew : SymmetricAlgorithm
        {
            private static readonly byte[] _bigKey = {
                0xA3, 0xD7, 0x09, 0x83, 0xF8, 0x48, 0xF6, 0xF4,
                0xB3, 0x21, 0x15, 0x78, 0x99, 0xB1, 0xAF, 0xF9,
                0xE7, 0x2D, 0x4D, 0x8A, 0xCE, 0x4C, 0xCA, 0x2E,
                0x52, 0x95, 0xD9, 0x1E, 0x4E, 0x38, 0x44, 0x28,
                0x0A, 0xDF, 0x02, 0xA0, 0x17, 0xF1, 0x60, 0x68,
                0x12, 0xB7, 0x7A, 0xC3, 0xE9, 0xFA, 0x3D, 0x53,
                0x96, 0x84, 0x6B, 0xBA, 0xF2, 0x63, 0x9A, 0x19,
                0x7C, 0xAE, 0xE5, 0xF5, 0xF7, 0x16, 0x6A, 0xA2,
                0x39, 0xB6, 0x7B, 0x0F, 0xC1, 0x93, 0x81, 0x1B,
                0xEE, 0xB4, 0x1A, 0xEA, 0xD0, 0x91, 0x2F, 0xB8,
                0x55, 0xB9, 0xDA, 0x85, 0x3F, 0x41, 0xBF, 0xE0,
                0x5A, 0x58, 0x80, 0x5F, 0x66, 0x0B, 0xD8, 0x90,
                0x35, 0xD5, 0xC0, 0xA7, 0x33, 0x06, 0x65, 0x69,
                0x45, 0x00, 0x94, 0x56, 0x6D, 0x98, 0x9B, 0x76,
                0x97, 0xFC, 0xB2, 0xC2, 0xB0, 0xFE, 0xDB, 0x20,
                0xE1, 0xEB, 0xD6, 0xE4, 0xDD, 0x47, 0x4A, 0x1D,
                0x42, 0xED, 0x9E, 0x6E, 0x49, 0x3C, 0xCD, 0x43,
                0x27, 0xD2, 0x07, 0xD4, 0xDE, 0xC7, 0x67, 0x18,
                0x89, 0xCB, 0x30, 0x1F, 0x8D, 0xC6, 0x8F, 0xAA,
                0xC8, 0x74, 0xDC, 0xC9, 0x5D, 0x5C, 0x31, 0xA4,
                0x70, 0x88, 0x61, 0x2C, 0x9F, 0x0D, 0x2B, 0x87,
                0x50, 0x82, 0x54, 0x64, 0x26, 0x7D, 0x03, 0x40,
                0x34, 0x4B, 0x1C, 0x73, 0xD1, 0xC4, 0xFD, 0x3B,
                0xCC, 0xFB, 0x7F, 0xAB, 0xE6, 0x3E, 0x5B, 0xA5,
                0xAD, 0x04, 0x23, 0x9C, 0x14, 0x51, 0x22, 0xF0,
                0x29, 0x79, 0x71, 0x7E, 0xFF, 0x8C, 0x0E, 0xE2,
                0x0C, 0xEF, 0xBC, 0x72, 0x75, 0x6F, 0x37, 0xA1,
                0xEC, 0xD3, 0x8E, 0x62, 0x8B, 0x86, 0x10, 0xE8,
                0x08, 0x77, 0x11, 0xBE, 0x92, 0x4F, 0x24, 0xC5,
                0x32, 0x36, 0x9D, 0xCF, 0xF3, 0xA6, 0xBB, 0xAC,
                0x5E, 0x6C, 0xA9, 0x13, 0x57, 0x25, 0xB5, 0xE3,
                0xBD, 0xA8, 0x3A, 0x01, 0x05, 0x59, 0x2A, 0x46
            };

            public SymAlgoHomebrew()
            {
                LegalBlockSizesValue = new[] {
                    new KeySizes(32, 32, 0)
                };
                LegalKeySizesValue = new[] {
                    new KeySizes(80, 80, 0)
                };
                BlockSizeValue = 32;
                KeySizeValue = 80;
                ModeValue = CipherMode.ECB;
                PaddingValue = PaddingMode.None;
            }

            public SymAlgoHomebrew(byte[] key) : this()
            {
                Key = key;
            }

            public override ICryptoTransform CreateDecryptor(byte[] key, byte[] iv) => GetTransform(key, iv, false);
            public override ICryptoTransform CreateEncryptor(byte[] key, byte[] iv) => GetTransform(key, iv, true);

            private ICryptoTransform GetTransform(byte[] key, byte[] iv, bool encrypt) => new CryptTrans3(key, encrypt);

            public override void GenerateIV() => IV = new byte[BlockSize/8];
            public override void GenerateKey() => Key = new byte[0];

            public byte[] Encrypt(byte[] bytes) => DoCrypto(bytes, true);
            public byte[] Decrypt(byte[] bytes) => DoCrypto(bytes, false);

            public uint Encrypt(uint val) => GetUInt(Encrypt(GetBytes(val)));
            public uint Decrypt(uint val) => GetUInt(Decrypt(GetBytes(val)));
            private static byte[] GetBytes(uint val) => new[] {(byte) (val >> 24), (byte) (val >> 16), (byte) (val >> 8), (byte) val};

            public int Encrypt(int val) => GetInt(Encrypt(GetBytes(val)));
            public int Decrypt(int val) => GetInt(Decrypt(GetBytes(val)));
            private static byte[] GetBytes(int val) => new[] {(byte) (val >> 24), (byte) (val >> 16), (byte) (val >> 8), (byte) val};

            private static uint GetUInt(byte[] val) => (uint)(val[0] << 24 | val[1] << 16 | val[2] << 8 | val[3]);
            private static int GetInt(byte[] val) => val[0] << 24 | val[1] << 16 | val[2] << 8 | val[3];

            private static ushort BadCrypto(byte[] key, int keyIndex, ushort seed)
            {
                byte hi = (byte)(seed >> 8 & 255);
                byte lo = (byte)(seed & 255);
                byte b1 = (byte)(_bigKey[lo ^ key[(4 * keyIndex + 0) % 10]] ^ hi);
                byte b2 = (byte)(_bigKey[b1 ^ key[(4 * keyIndex + 1) % 10]] ^ lo);
                byte b3 = (byte)(_bigKey[b2 ^ key[(4 * keyIndex + 2) % 10]] ^ b1);
                byte b4 = (byte)(_bigKey[b3 ^ key[(4 * keyIndex + 3) % 10]] ^ b2);
                return (ushort)((b3 << 8) + b4);
            }

            private byte[] DoCrypto(byte[] buffer, bool encrypt)
            {
                byte[] array = new byte[buffer.Length];
                for (int i = 0; i < buffer.Length; i += 4)
                {
                    TransformBlock(Key, buffer, i, array, i, encrypt);
                }
                return array;
            }

            private static void TransformBlock(byte[] key, byte[] inputBuffer, int inputOffset, byte[] outputBuffer, int outputOffset, bool encrypt)
            {
                int num1 = encrypt ? 1 : -1;
                int num2 = encrypt ? 0 : 23;

                ushort num3 = (ushort)((inputBuffer[inputOffset + 0] << 8) + inputBuffer[inputOffset + 1]);
                ushort num4 = (ushort)((inputBuffer[inputOffset + 2] << 8) + inputBuffer[inputOffset + 3]);

                for (int i = 0; i < 12; i++)
                {
                    num4 ^= (ushort)(BadCrypto(key, num2, num3) ^ num2);
                    num2 += num1;
                    num3 ^= (ushort)(BadCrypto(key, num2, num4) ^ num2);
                    num2 += num1;
                }

                outputBuffer[outputOffset + 0] = (byte)(num4 >> 8);
                outputBuffer[outputOffset + 1] = (byte)(num4 & 255);
                outputBuffer[outputOffset + 2] = (byte)(num3 >> 8);
                outputBuffer[outputOffset + 3] = (byte)(num3 & 255);
            }

            private sealed class CryptTrans3 : ICryptoTransform, IDisposable
            {
                private byte[] _key;
                private bool _encrypt;

                public int InputBlockSize => 4;
                public int OutputBlockSize => 4;
                public bool CanTransformMultipleBlocks => true;
                public bool CanReuseTransform => true;

                public CryptTrans3(byte[] key, bool encrypt)
                {
                    _key = key;
                    _encrypt = encrypt;
                }

                public void Dispose() { }

                public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount, byte[] outputBuffer, int outputOffset)
                {
                    for (int i = 0; i < inputCount; i += 4)
                        SymAlgoHomebrew.TransformBlock(_key, inputBuffer, inputOffset + i, outputBuffer, outputOffset + i, _encrypt);

                    return inputCount;
                }

                public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
                {
                    byte[] array = new byte[inputCount];
                    TransformBlock(inputBuffer, inputOffset, inputCount, array, 0);
                    return array;
                }
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0005\u2002
        /// <para>
        /// Homebrew hasher consisting of xoring and multiplying
        /// </para>
        /// </summary>
        internal sealed class HashAlgoHomebrew : HashAlgorithm
        {
            private const uint IV = 0x811C9DC5;
            private const uint MultKey = 0x1000193;
            private uint _hash;

            public HashAlgoHomebrew()
            {
                HashSizeValue = 32;
                InitSeed();
            }

            public override void Initialize() => InitSeed();
            private void InitSeed() => _hash = IV;

            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                _hash = DoHash(_hash, array, ibStart, cbSize);
            }

            protected override byte[] HashFinal()
            {
                return new[] {
                    (byte) (_hash >> 24),
                    (byte) (_hash >> 16),
                    (byte) (_hash >> 8),
                    (byte) (_hash >> 0)
                };
            }

            public static int DoHash(byte[] array) => (int)DoHash(IV, array, 0, array.Length);

            private static uint DoHash(uint seed, byte[] array, int ibStart, int cbSize)
            {
                for (int i = ibStart; i < ibStart + cbSize; i++)
                    seed = (seed ^ array[i]) * MultKey;

                return seed;
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0003\u2002
        /// <para>
        /// Uses a <seealso cref="SymmetricAlgorithm"/> to return an encrypted hash of an empty buffer.
        /// </para>
        /// </summary>
        internal sealed class HashAlgoEncryption : HashAlgorithm
        {
            private readonly HashAlgorithm _hashAlgo;
            private readonly SymmetricAlgorithm _symAlgo;

            public HashAlgoEncryption(HashAlgorithm h, SymmetricAlgorithm s)
            {
                HashSizeValue = h.HashSize;
                _hashAlgo = h;
                _symAlgo = s;
            }
            
            public override bool CanReuseTransform => _hashAlgo.CanReuseTransform;
            public override bool CanTransformMultipleBlocks => _hashAlgo.CanTransformMultipleBlocks;

            public override void Initialize() => _hashAlgo.Initialize();

            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                _hashAlgo.TransformBlock(array, ibStart, cbSize, array, ibStart);
            }

            protected override byte[] HashFinal()
            {
                //hash empty byte array
                _hashAlgo.TransformFinalBlock(new byte[0], 0, 0);

                //return encrypted hash
                using (ICryptoTransform trans = _symAlgo.CreateEncryptor())
                    return trans.TransformFinalBlock(_hashAlgo.Hash, 0, _hashAlgo.Hash.Length);
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0006\u2002
        /// <para>
        /// Hashes a 0 byte using passed <seealso cref="HashAlgorithm"/>.
        /// </para>
        /// </summary>
        internal sealed class HashAlgoPadder : HashAlgorithm
        {
            private HashAlgorithm _hashAlgo;

            public HashAlgoPadder(HashAlgorithm hashAlgo)
            {
                HashSizeValue = 8;
                _hashAlgo = hashAlgo;
            }
            
            public override bool CanReuseTransform => _hashAlgo.CanReuseTransform;
            public override bool CanTransformMultipleBlocks => _hashAlgo.CanTransformMultipleBlocks;

            public override void Initialize() => _hashAlgo.Initialize();

            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                _hashAlgo.TransformBlock(array, ibStart, cbSize, array, ibStart);
            }

            protected override byte[] HashFinal()
            {
                _hashAlgo.TransformFinalBlock(new byte[0], 0, 0);

                return new [] { _hashAlgo.Hash[0] };
            }
        }

        /// <inheritdoc />
        /// <summary>
        /// \u0008\u2002
        /// <para>
        /// Homebrew hashing chain
        /// </para>
        /// </summary>
        internal sealed class KeyedHashAlgo : KeyedHashAlgorithm
        {
            private HashAlgorithm _hashAlgo1;
            private HashAlgorithm _hashAlgo2;

            private byte[] _buffer1;
            private byte[] _buffer2;

            private bool _seeded;   //could also mean "in use"

            private int _hashSize = 64; //default value never used

            public KeyedHashAlgo(HashAlgorithm hashAlgo, byte[] key)
            {
                SetMyHashSize(hashAlgo.HashSize / 8);
                _hashAlgo1 = hashAlgo;
                _hashAlgo2 = hashAlgo;
                SetKey(key);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    ((IDisposable) _hashAlgo1)?.Dispose();
                    ((IDisposable) _hashAlgo2)?.Dispose();

                    if (_buffer1 != null) Array.Clear(_buffer1, 0, _buffer1.Length);
                    if (_buffer2 != null) Array.Clear(_buffer2, 0, _buffer2.Length);
                }
                base.Dispose(disposing);
            }

            protected override void HashCore(byte[] array, int ibStart, int cbSize)
            {
                if (!_seeded)
                {
                    _hashAlgo1.TransformBlock(_buffer1, 0, _buffer1.Length, _buffer1, 0);
                    _seeded = true;
                }
                _hashAlgo1.TransformBlock(array, ibStart, cbSize, array, ibStart);
            }

            protected override byte[] HashFinal()
            {
                //hash buffer1 contents if not done yet
                if (!_seeded) {
                    _hashAlgo1.TransformBlock(_buffer1, 0, _buffer1.Length, _buffer1, 0);
                    _seeded = true;
                }

                //get hash from _hashAlgo1
                byte[] zeroBuffer = new byte[0];
                _hashAlgo1.TransformFinalBlock(zeroBuffer, 0, 0);
                byte[] hash = _hashAlgo1.Hash;

                //make sure we start fresh
                if (_hashAlgo2 == _hashAlgo1)
                    _hashAlgo1.Initialize();

                //hash _buffer2, hash hash, then run the original zero buffer (which is the same as hash?) through
                _hashAlgo2.TransformBlock(_buffer2, 0, _buffer2.Length, _buffer2, 0);
                _hashAlgo2.TransformBlock(hash, 0, hash.Length, hash, 0);
                _hashAlgo2.TransformFinalBlock(zeroBuffer, 0, 0);

                //reset seeded var
                _seeded = false;

                return _hashAlgo2.Hash;
            }

            public override void Initialize()
            {
                _hashAlgo1.Initialize();
                _hashAlgo2.Initialize();
                _seeded = false;
            }

            private void SetKey(byte[] key)
            {
                //reset buffers
                _buffer1 = null;
                _buffer2 = null;

                //hash key if it is too small, else use it as is
                KeyValue = key.Length > GetMyHashSize() 
                    ? _hashAlgo1.ComputeHash(key) 
                    : (byte[]) key.Clone();

                //init buffers
                InitBuffers();
            }

            private void InitBuffers()
            {
                int hashSize = GetMyHashSize();

                //init buffers
                if (_buffer1 == null) _buffer1 = new byte[hashSize];
                if (_buffer2 == null) _buffer2 = new byte[hashSize];

                //fill buffers with constants
                for (int i = 0; i < hashSize; i++)
                {
                    _buffer1[i] = 54;
                    _buffer2[i] = 92;
                }

                //xor buffers with key
                for (int i = 0; i < KeyValue.Length; i++)
                {
                    _buffer1[i] ^= KeyValue[i];
                    _buffer2[i] ^= KeyValue[i];
                }
            }

            private int GetMyHashSize() => _hashSize;
            private void SetMyHashSize(int val) => _hashSize = val;

            public override byte[] Key
            {
                get => (byte[])KeyValue.Clone();
                set => SetKey(value);
            }
        }
    }
}