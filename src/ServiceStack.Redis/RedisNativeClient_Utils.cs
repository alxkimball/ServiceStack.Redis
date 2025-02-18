//
// redis-sharp.cs: ECMA CLI Binding to the Redis key-value storage system
//
// Authors:
//   Miguel de Icaza (miguel@gnome.org)
//
// Copyright 2010 Novell, Inc.
//
// Licensed under the same terms of Redis: new BSD license.
//

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ServiceStack.Text;

namespace ServiceStack.Redis
{
    public partial class RedisNativeClient
    {
        private const string OK = "OK";
        private const string QUEUED = "QUEUED";
        private static Timer UsageTimer;
        private static int __requestsPerHour = 0;
        private const int Unknown = -1;
        public int ServerVersionNumber { get; set; }

        public int AssertServerVersionNumber()
        {
            if (ServerVersionNumber == 0)
                AssertConnectedSocket();

            return ServerVersionNumber;
        }

        public static void DisposeTimers()
        {
            if (UsageTimer == null) return;
            try
            {
                UsageTimer.Dispose();
            }
            finally
            {
                UsageTimer = null;
            }
        }

        private void Connect()
        {
            if (UsageTimer == null)
            {
                //Save Timer Resource for licensed usage
                if (!LicenseUtils.HasLicensedFeature(LicenseFeature.Redis))
                {
                    UsageTimer = new Timer(delegate
                    {
                        __requestsPerHour = 0;
                    }, null, TimeSpan.FromMilliseconds(0), TimeSpan.FromHours(1));
                }
            }

            socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                SendTimeout = SendTimeout,
                ReceiveTimeout = ReceiveTimeout
            };
            try
            {
                if (ConnectTimeout <= 0)
                {
                    socket.Connect(Host, Port);
                }
                else
                {
                    var connectResult = socket.BeginConnect(Host, Port, null, null);
                    connectResult.AsyncWaitHandle.WaitOne(ConnectTimeout, true);
                }

                if (!socket.Connected)
                {
                    socket.Close();
                    socket = null;
                    DeactivatedAt = DateTime.UtcNow;
                    return;
                }

                Stream networkStream = new NetworkStream(socket);

                if (Ssl)
                {
                    if (Env.IsMono)
                    {
                        //Mono doesn't support EncryptionPolicy
                        sslStream = new SslStream(networkStream,
                            leaveInnerStreamOpen: false,
                            userCertificateValidationCallback: RedisConfig.CertificateValidationCallback,
                            userCertificateSelectionCallback: RedisConfig.CertificateSelectionCallback);
                    }
                    else
                    {
                        var ctor = typeof(SslStream).GetConstructors()
                            .First(x => x.GetParameters().Length == 5);

                        var policyType = AssemblyUtils.FindType("System.Net.Security.EncryptionPolicy");
                        var policyValue = Enum.Parse(policyType, "RequireEncryption");

                        sslStream = (SslStream)ctor.Invoke(new[] {
                            networkStream,
                            false,
                            RedisConfig.CertificateValidationCallback,
                            RedisConfig.CertificateSelectionCallback,
                            policyValue,
                        });
                    }

                    sslStream.AuthenticateAsClient(Host);

                    if (!sslStream.IsEncrypted)
                        throw new Exception("Could not establish an encrypted connection to " + Host);

                    networkStream = sslStream;
                }

                Bstream = new BufferedStream(networkStream, 16 * 1024);

                if (!string.IsNullOrEmpty(Password))
                    SendExpectSuccess(Commands.Auth, Password.ToUtf8Bytes());

                if (db != 0)
                    SendExpectSuccess(Commands.Select, db.ToUtf8Bytes());

                if (Client != null)
                    SendExpectSuccess(Commands.Client, Commands.SetName, Client.ToUtf8Bytes());

                try
                {
                    if (ServerVersionNumber == 0)
                    {
                        var parts = ServerVersion.Split('.');
                        var version = int.Parse(parts[0]) * 1000;
                        if (parts.Length > 1)
                            version += int.Parse(parts[1]) * 100;
                        if (parts.Length > 2)
                            version += int.Parse(parts[2]);

                        ServerVersionNumber = version;
                    }
                }
                catch (Exception)
                {
                    //Twemproxy doesn't support the INFO command so automatically closes the socket
                    //Fallback to ServerVersionNumber=Unknown then try re-connecting
                    ServerVersionNumber = Unknown;
                    Connect();
                    return;
                }

                var ipEndpoint = socket.LocalEndPoint as IPEndPoint;
                clientPort = ipEndpoint != null ? ipEndpoint.Port : -1;
                lastCommand = null;
                lastSocketException = null;
                LastConnectedAtTimestamp = Stopwatch.GetTimestamp();

                OnConnected();

                if (ConnectionFilter != null)
                {
                    ConnectionFilter(this);
                }
            }
            catch (SocketException)
            {
                log.Error(ErrorConnect.Fmt(Host, Port));
                throw;
            }
        }

        public static string ErrorConnect = "Could not connect to redis Instance at {0}:{1}";

        public virtual void OnConnected()
        {
        }

        protected string ReadLine()
        {
            var sb = new StringBuilder();

            int c;
            while ((c = Bstream.ReadByte()) != -1)
            {
                if (c == '\r')
                    continue;
                if (c == '\n')
                    break;
                sb.Append((char)c);
            }
            return sb.ToString();
        }

        public bool IsSocketConnected()
        {
            var part1 = socket.Poll(1000, SelectMode.SelectRead);
            var part2 = (socket.Available == 0);
            return !(part1 & part2);
        }

        internal bool AssertConnectedSocket()
        {
            try
            {
                TryConnectIfNeeded();
                var isConnected = socket != null;
                return isConnected;
            }
            catch (SocketException ex)
            {
                log.Error(ErrorConnect.Fmt(Host, Port));

                if (socket != null)
                    socket.Close();

                socket = null;

                DeactivatedAt = DateTime.UtcNow;
                var message = "" + Host + ":" + Port;
                var throwEx = new RedisException(message, ex);
                log.Error(throwEx.Message, ex);
                throw throwEx;
            }
        }

        private void TryConnectIfNeeded()
        {
            if (LastConnectedAtTimestamp > 0)
            {
                var now = Stopwatch.GetTimestamp();
                var elapsedSecs = (now - LastConnectedAtTimestamp) / Stopwatch.Frequency;

                if (socket == null || (elapsedSecs > IdleTimeOutSecs && !socket.IsConnected()))
                {
                    Reconnect();
                }
                LastConnectedAtTimestamp = now;
            }

            if (socket == null)
            {
                Connect();
            }
        }

        private bool Reconnect()
        {
            var previousDb = db;

            SafeConnectionClose();
            Connect(); //sets db to 0

            if (previousDb != RedisConfig.DefaultDb) this.Db = previousDb;

            return socket != null;
        }

        private RedisResponseException CreateResponseError(string error)
        {
            DeactivatedAt = DateTime.UtcNow;

            if (!RedisConfig.DisableVerboseLogging)
            {
                var safeLastCommand = string.IsNullOrEmpty(Password) 
                    ? lastCommand 
                    : (lastCommand ?? "").Replace(Password, "");

                if (!string.IsNullOrEmpty(safeLastCommand))
                    error = string.Format("{0}, LastCommand:'{1}', srcPort:{2}", error, safeLastCommand, clientPort);
            }

            var throwEx = new RedisResponseException(error);
            log.Error(error);
            throw throwEx;
        }

        private RedisRetryableException CreateNoMoreDataError()
        {
            Reconnect();
            return CreateRetryableResponseError("No more data");
        }

        private RedisRetryableException CreateRetryableResponseError(string error)
        {
            string safeLastCommand = string.IsNullOrEmpty(Password) ? lastCommand : (lastCommand ?? "").Replace(Password, "");

            var throwEx = new RedisRetryableException(
                string.Format("{0}, sPort: {1}, LastCommand: {2}",
                    error, clientPort, safeLastCommand));
            log.Error(throwEx.Message);
            throw throwEx;
        }

        private RedisException CreateConnectionError()
        {
            DeactivatedAt = DateTime.UtcNow;
            var throwEx = new RedisException(
                string.Format("Unable to Connect: sPort: {0}",
                    clientPort), lastSocketException);
            log.Error(throwEx.Message);
            throw throwEx;
        }

        private static byte[] GetCmdBytes(char cmdPrefix, int noOfLines)
        {
            var strLines = noOfLines.ToString();
            var strLinesLength = strLines.Length;

            var cmdBytes = new byte[1 + strLinesLength + 2];
            cmdBytes[0] = (byte)cmdPrefix;

            for (var i = 0; i < strLinesLength; i++)
                cmdBytes[i + 1] = (byte)strLines[i];

            cmdBytes[1 + strLinesLength] = 0x0D; // \r
            cmdBytes[2 + strLinesLength] = 0x0A; // \n

            return cmdBytes;
        }

        /// <summary>
        /// Command to set multuple binary safe arguments
        /// </summary>
        /// <param name="cmdWithBinaryArgs"></param>
        /// <returns></returns>
        protected void WriteCommandToSendBuffer(params byte[][] cmdWithBinaryArgs)
        {
            if (Pipeline == null && Transaction == null)
            {
                Interlocked.Increment(ref __requestsPerHour);
                if (__requestsPerHour % 20 == 0)
                    LicenseUtils.AssertValidUsage(LicenseFeature.Redis, QuotaType.RequestsPerHour, __requestsPerHour);
            }

            if (log.IsDebugEnabled && !RedisConfig.DisableVerboseLogging)
                CmdLog(cmdWithBinaryArgs);

            //Total command lines count
            WriteAllToSendBuffer(cmdWithBinaryArgs);
        }

        public void WriteAllToSendBuffer(params byte[][] cmdWithBinaryArgs)
        {
            WriteToSendBuffer(GetCmdBytes('*', cmdWithBinaryArgs.Length));

            foreach (var safeBinaryValue in cmdWithBinaryArgs)
            {
                WriteToSendBuffer(GetCmdBytes('$', safeBinaryValue.Length));
                WriteToSendBuffer(safeBinaryValue);
                WriteToSendBuffer(endData);
            }
        }

        readonly IList<ArraySegment<byte>> cmdBuffer = new List<ArraySegment<byte>>();
        byte[] currentBuffer = BufferPool.GetBuffer();
        int currentBufferIndex;

        public void WriteToSendBuffer(byte[] cmdBytes)
        {
            if (CouldAddToCurrentBuffer(cmdBytes)) return;

            PushCurrentBuffer();

            if (CouldAddToCurrentBuffer(cmdBytes)) return;

            var bytesCopied = 0;
            while (bytesCopied < cmdBytes.Length)
            {
                var copyOfBytes = BufferPool.GetBuffer(cmdBytes.Length);
                var bytesToCopy = Math.Min(cmdBytes.Length - bytesCopied, copyOfBytes.Length);
                Buffer.BlockCopy(cmdBytes, bytesCopied, copyOfBytes, 0, bytesToCopy);
                cmdBuffer.Add(new ArraySegment<byte>(copyOfBytes, 0, bytesToCopy));
                bytesCopied += bytesToCopy;
            }
        }

        private bool CouldAddToCurrentBuffer(byte[] cmdBytes)
        {
            if (cmdBytes.Length + currentBufferIndex < RedisConfig.BufferLength)
            {
                Buffer.BlockCopy(cmdBytes, 0, currentBuffer, currentBufferIndex, cmdBytes.Length);
                currentBufferIndex += cmdBytes.Length;
                return true;
            }
            return false;
        }

        private void PushCurrentBuffer()
        {
            cmdBuffer.Add(new ArraySegment<byte>(currentBuffer, 0, currentBufferIndex));
            currentBuffer = BufferPool.GetBuffer();
            currentBufferIndex = 0;
        }

        public Action OnBeforeFlush { get; set; }

        internal void FlushAndResetSendBuffer()
        {
            FlushSendBuffer();
            ResetSendBuffer();
        }

        internal void FlushSendBuffer()
        {
            if (currentBufferIndex > 0)
                PushCurrentBuffer();

            if (cmdBuffer.Count > 0)
            {
                if (OnBeforeFlush != null)
                    OnBeforeFlush();

                if (!Env.IsMono && sslStream == null)
                {
                    socket.Send(cmdBuffer); //Optimized for Windows
                }
                else
                {
                    //Sendling IList<ArraySegment> Throws 'Message to Large' SocketException in Mono
                    foreach (var segment in cmdBuffer)
                    {
                        var buffer = segment.Array;
                        if (sslStream == null)
                        {
                            socket.Send(buffer, segment.Offset, segment.Count, SocketFlags.None);
                        }
                        else
                        {
                            sslStream.Write(buffer, segment.Offset, segment.Count);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// reset buffer index in send buffer
        /// </summary>
        public void ResetSendBuffer()
        {
            currentBufferIndex = 0;
            for (int i = cmdBuffer.Count - 1; i >= 0; i--)
            {
                var buffer = cmdBuffer[i].Array;
                BufferPool.ReleaseBufferToPool(ref buffer);
                cmdBuffer.RemoveAt(i);
            }
        }

        private int SafeReadByte()
        {
            return Bstream.ReadByte();
        }

        protected T SendReceive<T>(byte[][] cmdWithBinaryArgs, 
            Func<T> fn, 
            Action<Func<T>> completePipelineFn = null, 
            bool sendWithoutRead = false)
        {
            var i = 0;
            Exception originalEx = null;

            var firstAttempt = DateTime.UtcNow;

            while (true)
            {
                try
                {
                    TryConnectIfNeeded();

                    if (i == 0) //only write to buffer once
                        WriteCommandToSendBuffer(cmdWithBinaryArgs);

                    if (Pipeline == null) //pipeline will handle flush if in pipeline
                    {
                        FlushSendBuffer();
                    }
                    else if (!sendWithoutRead)
                    {
                        if (completePipelineFn == null)
                            throw new NotSupportedException("Pipeline is not supported.");

                        completePipelineFn(fn);
                        return default(T);
                    }

                    var result = default(T);
                    if (fn != null)
                        result = fn();

                    if (Pipeline == null)
                        ResetSendBuffer();

                    if (i > 0)
                        Interlocked.Increment(ref RedisState.TotalRetrySuccess);

                    Interlocked.Increment(ref RedisState.TotalCommandsSent);

                    return result;
                }
                catch (Exception outerEx)
                {
                    var retryableEx = outerEx as RedisRetryableException;
                    if (retryableEx == null && outerEx is RedisException)
                        throw;

                    var ex = retryableEx ?? GetRetryableException(outerEx);
                    if (ex == null)
                        throw CreateConnectionError();

                    if (originalEx == null)
                        originalEx = ex;

                    var retry = DateTime.UtcNow - firstAttempt < retryTimeout;
                    if (!retry)
                    {
                        if (Pipeline == null)
                            ResetSendBuffer();

                        Interlocked.Increment(ref RedisState.TotalRetryTimedout);
                        throw CreateRetryTimeoutException(retryTimeout, originalEx);
                    }

                    Interlocked.Increment(ref RedisState.TotalRetryCount);
                    Thread.Sleep(GetBackOffMultiplier(++i));
                }
            }
        }

        private RedisException CreateRetryTimeoutException(TimeSpan retryTimeout, Exception originalEx)
        {
            DeactivatedAt = DateTime.UtcNow;
            var message = "Exceeded timeout of {0}".Fmt(retryTimeout);
            log.Error(message);
            return new RedisException(message, originalEx);
        }

        private Exception GetRetryableException(Exception outerEx)
        {
            // several stream commands wrap SocketException in IOException
            var socketEx = outerEx.InnerException as SocketException
                ?? outerEx as SocketException;

            if (socketEx == null)
                return null;

            log.Error("SocketException in SendReceive, retrying...", socketEx);
            lastSocketException = socketEx;

            if (socket != null)
                socket.Close();

            socket = null;
            return socketEx;
        }

        private static int GetBackOffMultiplier(int i)
        {
            var nextTryMs = (2 ^ i) * RedisConfig.BackOffMultiplier;
            return nextTryMs;
        }

        protected void SendWithoutRead(params byte[][] cmdWithBinaryArgs)
        {
            SendReceive<long>(cmdWithBinaryArgs, null, null, sendWithoutRead: true);
        }

        protected void SendExpectSuccess(params byte[][] cmdWithBinaryArgs)
        {
            //Turn Action into Func Hack
            var completePipelineFn = Pipeline != null
                ? f => { Pipeline.CompleteVoidQueuedCommand(() => f()); }
            : (Action<Func<long>>)null;

            SendReceive(cmdWithBinaryArgs, ExpectSuccessFn, completePipelineFn);
        }

        protected long SendExpectLong(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadLong, Pipeline != null ? Pipeline.CompleteLongQueuedCommand : (Action<Func<long>>)null);
        }

        protected byte[] SendExpectData(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadData, Pipeline != null ? Pipeline.CompleteBytesQueuedCommand : (Action<Func<byte[]>>)null);
        }

        protected double SendExpectDouble(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadDouble, Pipeline != null ? Pipeline.CompleteDoubleQueuedCommand : (Action<Func<double>>)null);
        }

        protected string SendExpectCode(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ExpectCode, Pipeline != null ? Pipeline.CompleteStringQueuedCommand : (Action<Func<string>>)null);
        }

        protected byte[][] SendExpectMultiData(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadMultiData, Pipeline != null ? Pipeline.CompleteMultiBytesQueuedCommand : (Action<Func<byte[][]>>)null)
                ?? new byte[0][];
        }

        protected object[] SendExpectDeeplyNestedMultiData(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadDeeplyNestedMultiData);
        }

        protected RedisData SendExpectComplexResponse(params byte[][] cmdWithBinaryArgs)
        {
            return SendReceive(cmdWithBinaryArgs, ReadComplexResponse);
        }

        protected string SendExpectString(params byte[][] cmdWithBinaryArgs)
        {
            var bytes = SendExpectData(cmdWithBinaryArgs);
            return bytes.FromUtf8Bytes();
        }

        protected void Log(string fmt, params object[] args)
        {
            if (RedisConfig.DisableVerboseLogging)
                return;

            log.DebugFormat("{0}", string.Format(fmt, args).Trim());
        }

        protected void CmdLog(byte[][] args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                var strArg = arg.FromUtf8Bytes();
                if (strArg == Password) continue;

                if (sb.Length > 0)
                    sb.Append(" ");

                sb.Append(strArg);

                if (sb.Length > 100)
                    break;
            }
            this.lastCommand = sb.ToString();
            if (this.lastCommand.Length > 100)
            {
                this.lastCommand = this.lastCommand.Substring(0, 100) + "...";
            }

            log.Debug("S: " + this.lastCommand);
        }

        //Turn Action into Func Hack
        protected long ExpectSuccessFn()
        {
            ExpectSuccess();
            return 0;
        }

        protected void ExpectSuccess()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();

            if (log.IsDebugEnabled)
                Log((char)c + s);

            if (c == '-')
                throw CreateResponseError(s.StartsWith("ERR") && s.Length >= 4 ? s.Substring(4) : s);
        }

        private void ExpectWord(string word)
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();

            if (log.IsDebugEnabled)
                Log((char)c + s);

            if (c == '-')
                throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

            if (s != word)
                throw CreateResponseError(string.Format("Expected '{0}' got '{1}'", word, s));
        }

        private string ExpectCode()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();

            if (log.IsDebugEnabled)
                Log((char)c + s);

            if (c == '-')
                throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

            return s;
        }

        internal void ExpectOk()
        {
            ExpectWord(OK);
        }

        internal void ExpectQueued()
        {
            ExpectWord(QUEUED);
        }

        public long ReadLong()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();

            if (log.IsDebugEnabled)
                Log("R: {0}", s);

            if (c == '-')
                throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

            if (c == ':' || c == '$')//really strange why ZRANK needs the '$' here
            {
                long i;
                if (long.TryParse(s, out i))
                    return i;
            }
            throw CreateResponseError("Unknown reply on integer response: " + c + s);
        }

        public double ReadDouble()
        {
            var bytes = ReadData();
            return (bytes == null) ? double.NaN : ParseDouble(bytes);
        }

        public static double ParseDouble(byte[] doubleBytes)
        {
            var doubleString = Encoding.UTF8.GetString(doubleBytes);

            double d;
            double.TryParse(doubleString, NumberStyles.Any, CultureInfo.InvariantCulture.NumberFormat, out d);

            return d;
        }

        private byte[] ReadData()
        {
            var r = ReadLine();
            return ParseSingleLine(r);
        }

        private byte[] ParseSingleLine(string r)
        {
            if (log.IsDebugEnabled)
                Log("R: {0}", r);
            if (r.Length == 0)
                throw CreateResponseError("Zero length response");

            char c = r[0];
            if (c == '-')
                throw CreateResponseError(r.StartsWith("-ERR") ? r.Substring(5) : r.Substring(1));

            if (c == '$')
            {
                if (r == "$-1")
                    return null;
                int count;

                if (int.TryParse(r.Substring(1), out count))
                {
                    var retbuf = new byte[count];

                    var offset = 0;
                    while (count > 0)
                    {
                        var readCount = Bstream.Read(retbuf, offset, count);
                        if (readCount <= 0)
                            throw CreateResponseError("Unexpected end of Stream");

                        offset += readCount;
                        count -= readCount;
                    }

                    if (Bstream.ReadByte() != '\r' || Bstream.ReadByte() != '\n')
                        throw CreateResponseError("Invalid termination");

                    return retbuf;
                }
                throw CreateResponseError("Invalid length");
            }

            if (c == ':' || c == '+')
            {
                //match the return value
                return r.Substring(1).ToUtf8Bytes();
            }
            throw CreateResponseError("Unexpected reply: " + r);
        }

        private byte[][] ReadMultiData()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();
            if (log.IsDebugEnabled)
                Log("R: {0}", s);

            switch (c)
            {
                // Some commands like BRPOPLPUSH may return Bulk Reply instead of Multi-bulk
                case '$':
                    var t = new byte[2][];
                    t[1] = ParseSingleLine(string.Concat(char.ToString((char)c), s));
                    return t;

                case '-':
                    throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

                case '*':
                    int count;
                    if (int.TryParse(s, out count))
                    {
                        if (count == -1)
                        {
                            //redis is in an invalid state
                            return new byte[0][];
                        }

                        var result = new byte[count][];

                        for (int i = 0; i < count; i++)
                            result[i] = ReadData();

                        return result;
                    }
                    break;
            }

            throw CreateResponseError("Unknown reply on multi-request: " + c + s);
        }

        private object[] ReadDeeplyNestedMultiData()
        {
            var result = ReadDeeplyNestedMultiDataItem();
            return (object[])result;
        }

        private object ReadDeeplyNestedMultiDataItem()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();
            if (log.IsDebugEnabled)
                Log("R: {0}", s);

            switch (c)
            {
                case '$':
                    return ParseSingleLine(string.Concat(char.ToString((char)c), s));

                case '-':
                    throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

                case '*':
                    int count;
                    if (int.TryParse(s, out count))
                    {
                        var array = new object[count];
                        for (int i = 0; i < count; i++)
                        {
                            array[i] = ReadDeeplyNestedMultiDataItem();
                        }

                        return array;
                    }
                    break;

                default:
                    return s;
            }

            throw CreateResponseError("Unknown reply on multi-request: " + c + s);
        }

        internal RedisData ReadComplexResponse()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();
            if (log.IsDebugEnabled)
                Log("R: {0}", s);

            switch (c)
            {
                case '$':
                    return new RedisData
                    {
                        Data = ParseSingleLine(string.Concat(char.ToString((char)c), s))
                    };

                case '-':
                    throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);

                case '*':
                    int count;
                    if (int.TryParse(s, out count))
                    {
                        var ret = new RedisData { Children = new List<RedisData>() };
                        for (var i = 0; i < count; i++)
                        {
                            ret.Children.Add(ReadComplexResponse());
                        }

                        return ret;
                    }
                    break;

                default:
                    return new RedisData { Data = s.ToUtf8Bytes() };
            }

            throw CreateResponseError("Unknown reply on multi-request: " + c + s);
        }

        internal int ReadMultiDataResultCount()
        {
            int c = SafeReadByte();
            if (c == -1)
                throw CreateNoMoreDataError();

            var s = ReadLine();
            if (log.IsDebugEnabled)
                Log("R: {0}", s);
            if (c == '-')
                throw CreateResponseError(s.StartsWith("ERR") ? s.Substring(4) : s);
            if (c == '*')
            {
                int count;
                if (int.TryParse(s, out count))
                {
                    return count;
                }
            }
            throw CreateResponseError("Unknown reply on multi-request: " + c + s);
        }

        private static void AssertListIdAndValue(string listId, byte[] value)
        {
            if (listId == null)
                throw new ArgumentNullException("listId");
            if (value == null)
                throw new ArgumentNullException("value");
        }

        private static byte[][] MergeCommandWithKeysAndValues(byte[] cmd, byte[][] keys, byte[][] values)
        {
            var firstParams = new[] { cmd };
            return MergeCommandWithKeysAndValues(firstParams, keys, values);
        }

        private static byte[][] MergeCommandWithKeysAndValues(byte[] cmd, byte[] firstArg, byte[][] keys, byte[][] values)
        {
            var firstParams = new[] { cmd, firstArg };
            return MergeCommandWithKeysAndValues(firstParams, keys, values);
        }

        private static byte[][] MergeCommandWithKeysAndValues(byte[][] firstParams,
            byte[][] keys, byte[][] values)
        {
            if (keys == null || keys.Length == 0)
                throw new ArgumentNullException("keys");
            if (values == null || values.Length == 0)
                throw new ArgumentNullException("values");
            if (keys.Length != values.Length)
                throw new ArgumentException("The number of values must be equal to the number of keys");

            var keyValueStartIndex = (firstParams != null) ? firstParams.Length : 0;

            var keysAndValuesLength = keys.Length * 2 + keyValueStartIndex;
            var keysAndValues = new byte[keysAndValuesLength][];

            for (var i = 0; i < keyValueStartIndex; i++)
            {
                keysAndValues[i] = firstParams[i];
            }

            var j = 0;
            for (var i = keyValueStartIndex; i < keysAndValuesLength; i += 2)
            {
                keysAndValues[i] = keys[j];
                keysAndValues[i + 1] = values[j];
                j++;
            }
            return keysAndValues;
        }

        private static byte[][] MergeCommandWithArgs(byte[] cmd, params string[] args)
        {
            var byteArgs = args.ToMultiByteArray();
            return MergeCommandWithArgs(cmd, byteArgs);
        }

        private static byte[][] MergeCommandWithArgs(byte[] cmd, params byte[][] args)
        {
            var mergedBytes = new byte[1 + args.Length][];
            mergedBytes[0] = cmd;
            for (var i = 0; i < args.Length; i++)
            {
                mergedBytes[i + 1] = args[i];
            }
            return mergedBytes;
        }

        private static byte[][] MergeCommandWithArgs(byte[] cmd, byte[] firstArg, params byte[][] args)
        {
            var mergedBytes = new byte[2 + args.Length][];
            mergedBytes[0] = cmd;
            mergedBytes[1] = firstArg;
            for (var i = 0; i < args.Length; i++)
            {
                mergedBytes[i + 2] = args[i];
            }
            return mergedBytes;
        }

        protected byte[][] ConvertToBytes(string[] keys)
        {
            var keyBytes = new byte[keys.Length][];
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                keyBytes[i] = key != null ? key.ToUtf8Bytes() : new byte[0];
            }
            return keyBytes;
        }

        protected byte[][] MergeAndConvertToBytes(string[] keys, string[] args)
        {
            if (keys == null)
                keys = new string[0];
            if (args == null)
                args = new string[0];

            var keysLength = keys.Length;
            var merged = new string[keysLength + args.Length];
            for (var i = 0; i < merged.Length; i++)
            {
                merged[i] = i < keysLength ? keys[i] : args[i - keysLength];
            }

            return ConvertToBytes(merged);
        }

        public long EvalInt(string luaBody, int numberKeysInArgs, params byte[][] keys)
        {
            if (luaBody == null)
                throw new ArgumentNullException("luaBody");

            var cmdArgs = MergeCommandWithArgs(Commands.Eval, luaBody.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectLong(cmdArgs);
        }

        public long EvalShaInt(string sha1, int numberKeysInArgs, params byte[][] keys)
        {
            if (sha1 == null)
                throw new ArgumentNullException("sha1");

            var cmdArgs = MergeCommandWithArgs(Commands.EvalSha, sha1.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectLong(cmdArgs);
        }

        public string EvalStr(string luaBody, int numberKeysInArgs, params byte[][] keys)
        {
            if (luaBody == null)
                throw new ArgumentNullException("luaBody");

            var cmdArgs = MergeCommandWithArgs(Commands.Eval, luaBody.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectData(cmdArgs).FromUtf8Bytes();
        }

        public string EvalShaStr(string sha1, int numberKeysInArgs, params byte[][] keys)
        {
            if (sha1 == null)
                throw new ArgumentNullException("sha1");

            var cmdArgs = MergeCommandWithArgs(Commands.EvalSha, sha1.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectData(cmdArgs).FromUtf8Bytes();
        }

        public byte[][] Eval(string luaBody, int numberKeysInArgs, params byte[][] keys)
        {
            if (luaBody == null)
                throw new ArgumentNullException("luaBody");

            var cmdArgs = MergeCommandWithArgs(Commands.Eval, luaBody.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectMultiData(cmdArgs);
        }

        public byte[][] EvalSha(string sha1, int numberKeysInArgs, params byte[][] keys)
        {
            if (sha1 == null)
                throw new ArgumentNullException("sha1");

            var cmdArgs = MergeCommandWithArgs(Commands.EvalSha, sha1.ToUtf8Bytes(), keys.PrependInt(numberKeysInArgs));
            return SendExpectMultiData(cmdArgs);
        }

        public string CalculateSha1(string luaBody)
        {
            if (luaBody == null)
                throw new ArgumentNullException("luaBody");

            byte[] buffer = Encoding.UTF8.GetBytes(luaBody);
            var cryptoTransformSHA1 = new SHA1CryptoServiceProvider();
            return BitConverter.ToString(cryptoTransformSHA1.ComputeHash(buffer)).Replace("-", "");
        }

        public byte[] ScriptLoad(string luaBody)
        {
            if (luaBody == null)
                throw new ArgumentNullException("luaBody");

            var cmdArgs = MergeCommandWithArgs(Commands.Script, Commands.Load, luaBody.ToUtf8Bytes());
            return SendExpectData(cmdArgs);
        }

        public byte[][] ScriptExists(params byte[][] sha1Refs)
        {
            var keysAndValues = MergeCommandWithArgs(Commands.Script, Commands.Exists, sha1Refs);
            return SendExpectMultiData(keysAndValues);
        }

        public void ScriptFlush()
        {
            SendExpectSuccess(Commands.Script, Commands.Flush);
        }

        public void ScriptKill()
        {
            SendExpectSuccess(Commands.Script, Commands.Kill);
        }

    }

}
