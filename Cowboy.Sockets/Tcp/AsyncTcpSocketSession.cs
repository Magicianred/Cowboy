﻿using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Cowboy.Buffer;
using Cowboy.Logging;

namespace Cowboy.Sockets
{
    public sealed class AsyncTcpSocketSession
    {
        private static readonly ILog _log = Logger.Get<AsyncTcpSocketSession>();
        private readonly TcpClient _tcpClient;
        private readonly TcpSocketServerConfiguration _configuration;
        private readonly IBufferManager _bufferManager;
        private readonly IAsyncTcpSocketServerMessageDispatcher _dispatcher;
        private readonly string _sessionKey;

        public AsyncTcpSocketSession(
            TcpClient tcpClient,
            TcpSocketServerConfiguration configuration,
            IBufferManager bufferManager,
            IAsyncTcpSocketServerMessageDispatcher dispatcher)
        {
            if (tcpClient == null)
                throw new ArgumentNullException("tcpClient");
            if (configuration == null)
                throw new ArgumentNullException("configuration");
            if (bufferManager == null)
                throw new ArgumentNullException("bufferManager");
            if (dispatcher == null)
                throw new ArgumentNullException("dispatcher");

            _tcpClient = tcpClient;
            _configuration = configuration;
            _bufferManager = bufferManager;
            _dispatcher = dispatcher;

            _sessionKey = Guid.NewGuid().ToString();
        }

        public string SessionKey { get { return _sessionKey; } }
        public bool Connected { get { return _tcpClient.Connected; } }
        public EndPoint RemoteEndPoint { get { return _tcpClient.Client.RemoteEndPoint; } }
        public EndPoint LocalEndPoint { get { return _tcpClient.Client.LocalEndPoint; } }

        public async Task Start()
        {
            byte[] receiveBuffer = _bufferManager.BorrowBuffer();
            byte[] sessionBuffer = _bufferManager.BorrowBuffer();
            int sessionBufferCount = 0;

            try
            {
                while (Connected)
                {
                    int receiveCount = await _tcpClient.GetStream().ReadAsync(receiveBuffer, 0, receiveBuffer.Length);

                    if (!_configuration.IsPackingEnabled)
                    {
                        await _dispatcher.Dispatch(this, receiveBuffer, 0, receiveCount);
                    }
                    else
                    {
                        AppendBuffer(receiveBuffer, receiveCount, ref sessionBuffer, ref sessionBufferCount);

                        while (true)
                        {
                            var packetHeader = TcpPacketHeader.ReadHeader(sessionBuffer);
                            if (TcpPacketHeader.HEADER_SIZE + packetHeader.PayloadSize <= sessionBufferCount)
                            {
                                await _dispatcher.Dispatch(this, sessionBuffer, TcpPacketHeader.HEADER_SIZE, packetHeader.PayloadSize);
                                ShiftBuffer(TcpPacketHeader.HEADER_SIZE + packetHeader.PayloadSize, ref sessionBuffer, ref sessionBufferCount);
                            }
                            else
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (SocketException) { }
            finally
            {
                _bufferManager.ReturnBuffer(receiveBuffer);
                _bufferManager.ReturnBuffer(sessionBuffer);

                if (_tcpClient != null)
                    _tcpClient.Dispose();
            }
        }

        public async Task Send(byte[] data)
        {
            await Send(data, 0, data.Length);
        }

        public async Task Send(byte[] data, int offset, int count)
        {
            await _tcpClient.GetStream().WriteAsync(data, offset, count);
        }

        public void Close()
        {
            _tcpClient.Close();
        }

        private void AppendBuffer(byte[] receiveBuffer, int receiveCount, ref byte[] sessionBuffer, ref int sessionBufferCount)
        {
            if (sessionBuffer.Length < (sessionBufferCount + receiveCount))
            {
                byte[] autoExpandedBuffer = _bufferManager.BorrowBuffer();
                if (autoExpandedBuffer.Length < (sessionBufferCount + receiveCount) * 2)
                {
                    _bufferManager.ReturnBuffer(autoExpandedBuffer);
                    autoExpandedBuffer = new byte[(sessionBufferCount + receiveCount) * 2];
                }

                Array.Copy(sessionBuffer, 0, autoExpandedBuffer, 0, sessionBufferCount);

                var discardBuffer = sessionBuffer;
                sessionBuffer = autoExpandedBuffer;
                _bufferManager.ReturnBuffer(discardBuffer);
            }

            Array.Copy(receiveBuffer, 0, sessionBuffer, sessionBufferCount, receiveCount);
            sessionBufferCount = sessionBufferCount + receiveCount;
        }

        private void ShiftBuffer(int shiftStart, ref byte[] sessionBuffer, ref int sessionBufferCount)
        {
            if ((sessionBufferCount - shiftStart) < shiftStart)
            {
                Array.Copy(sessionBuffer, shiftStart, sessionBuffer, 0, sessionBufferCount - shiftStart);
                sessionBufferCount = sessionBufferCount - shiftStart;
            }
            else
            {
                byte[] copyBuffer = _bufferManager.BorrowBuffer();
                if (copyBuffer.Length < (sessionBufferCount - shiftStart))
                {
                    _bufferManager.ReturnBuffer(copyBuffer);
                    copyBuffer = new byte[sessionBufferCount - shiftStart];
                }

                Array.Copy(sessionBuffer, shiftStart, copyBuffer, 0, sessionBufferCount - shiftStart);
                Array.Copy(copyBuffer, 0, sessionBuffer, 0, sessionBufferCount - shiftStart);
                sessionBufferCount = sessionBufferCount - shiftStart;

                _bufferManager.ReturnBuffer(copyBuffer);
            }
        }

        public override string ToString()
        {
            return _sessionKey;
        }
    }
}