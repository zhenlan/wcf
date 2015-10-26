﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Runtime;
using System.Runtime.Diagnostics;
using System.Security.Principal;
using System.ServiceModel.Diagnostics;
using System.ServiceModel.Diagnostics.Application;
using System.ServiceModel.Security;
using System.Threading;
using System.Threading.Tasks;

namespace System.ServiceModel.Channels
{
    abstract class WebSocketTransportDuplexSessionChannel : TransportDuplexSessionChannel
    {
        static readonly AsyncCallback s_streamedWriteCallback = Fx.ThunkCallback(StreamWriteCallback);
        readonly WebSocketTransportSettings _webSocketSettings;
        readonly TransferMode _transferMode;
        readonly int _maxBufferSize;
        readonly ConnectionBufferPool _bufferPool;
        readonly ITransportFactorySettings _transportFactorySettings;
        readonly WebSocketCloseDetails _webSocketCloseDetails = new WebSocketCloseDetails();
        Action<object> _waitCallback;
        WebSocket _webSocket;
        WebSocketStream _webSocketStream;
        object _state;
        byte[] _internalBuffer;
        int _cleanupStatus = WebSocketHelper.OperationNotStarted;
        bool _shouldDisposeWebSocketAfterClosed = true;
        Exception _pendingWritingMessageException;

        public WebSocketTransportDuplexSessionChannel(HttpChannelFactory<IDuplexSessionChannel> channelFactory, EndpointAddress remoteAddresss, Uri via, ConnectionBufferPool bufferPool)
            : base(channelFactory, channelFactory, EndpointAddress.AnonymousAddress, channelFactory.MessageVersion.Addressing.AnonymousUri, remoteAddresss, via)
        {
            Fx.Assert(channelFactory.WebSocketSettings != null, "channelFactory.WebSocketTransportSettings should not be null.");
            _webSocketSettings = channelFactory.WebSocketSettings;
            _transferMode = channelFactory.TransferMode;
            _maxBufferSize = channelFactory.MaxBufferSize;
            _bufferPool = bufferPool;
            _transportFactorySettings = channelFactory;
        }

        protected WebSocket WebSocket
        {
            get
            {
                return _webSocket;
            }

            set
            {
                Fx.Assert(value != null, "value should not be null.");
                Fx.Assert(_webSocket == null, "webSocket should not be set before this set call.");
                _webSocket = value;
            }
        }

        protected WebSocketTransportSettings WebSocketSettings
        {
            get { return _webSocketSettings; }
        }

        protected TransferMode TransferMode
        {
            get { return _transferMode; }
        }

        protected int MaxBufferSize
        {
            get
            {
                return _maxBufferSize;
            }
        }

        protected ITransportFactorySettings TransportFactorySettings
        {
            get
            {
                return _transportFactorySettings;
            }
        }

        protected byte[] InternalBuffer
        {
            get
            {
                return _internalBuffer;
            }

            set
            {
                // We allow setting the property to null as long as we don't overwrite an existing non-null 'internalBuffer'. Because otherwise 
                // we get NullRefs in other places. So if you change/remove the assert below, make sure we still assert for this case.
                Fx.Assert(_internalBuffer == null, "internalBuffer should not be set twice.");
                _internalBuffer = value;
            }
        }

        protected override void OnAbort()
        {
            if (TD.WebSocketConnectionAbortedIsEnabled())
            {
                TD.WebSocketConnectionAborted(
                    EventTraceActivity,
                    WebSocket != null ? WebSocket.GetHashCode() : -1);
            }

            Cleanup();
        }

        protected override void CompleteClose(TimeSpan timeout)
        {
            if (TD.WebSocketCloseSentIsEnabled())
            {
                TD.WebSocketCloseSent(
                    WebSocket.GetHashCode(),
                    _webSocketCloseDetails.OutputCloseStatus.ToString(),
                    RemoteAddress != null ? RemoteAddress.ToString() : string.Empty);
            }

            Task closeTask = CloseAsync();
            closeTask.Wait(timeout, WebSocketHelper.ThrowCorrectException, WebSocketHelper.CloseOperation);

            if (TD.WebSocketConnectionClosedIsEnabled())
            {
                TD.WebSocketConnectionClosed(WebSocket.GetHashCode());
            }
        }

        protected byte[] TakeBuffer()
        {
            Fx.Assert(_bufferPool != null, "'bufferPool' MUST NOT be NULL.");
            return _bufferPool.Take();
        }

        protected override void CloseOutputSessionCore(TimeSpan timeout)
        {
            if (TD.WebSocketCloseOutputSentIsEnabled())
            {
                TD.WebSocketCloseOutputSent(
                    WebSocket.GetHashCode(),
                    _webSocketCloseDetails.OutputCloseStatus.ToString(),
                    RemoteAddress != null ? RemoteAddress.ToString() : string.Empty);
            }

            Task task = CloseOutputAsync(CancellationToken.None);
            task.Wait(timeout, WebSocketHelper.ThrowCorrectException, WebSocketHelper.CloseOperation);
        }

        protected override async Task CloseOutputSessionCoreAsync(TimeSpan timeout)
        {
            if (TD.WebSocketCloseOutputSentIsEnabled())
            {
                TD.WebSocketCloseOutputSent(
                    WebSocket.GetHashCode(),
                    _webSocketCloseDetails.OutputCloseStatus.ToString(),
                    RemoteAddress != null ? RemoteAddress.ToString() : string.Empty);
            }

            using (var cts = new CancellationTokenSource(timeout))
            {

                try
                {
                    await CloseOutputAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    if (Fx.IsFatal(ex))
                    {
                        throw;
                    }

                    if (cts.IsCancellationRequested)
                    {
                        throw Fx.Exception.AsError(new TimeoutException(InternalSR.TaskTimedOutError(timeout)));
                    }

                    throw WebSocketHelper.ConvertAndTraceException(ex, timeout, WebSocketHelper.ReceiveOperation);
                }
            }
        }

        protected override void OnClose(TimeSpan timeout)
        {
            try
            {
                base.OnClose(timeout);
            }
            finally
            {
                Cleanup();
            }
        }

        protected internal override async Task OnCloseAsync(TimeSpan timeout)
        {
            try
            {
                await base.OnCloseAsync(timeout);
            }
            finally
            {
                Cleanup();
            }
        }

        protected override void ReturnConnectionIfNecessary(bool abort, TimeSpan timeout)
        {
        }

        protected override AsyncCompletionResult StartWritingBufferedMessage(Message message, ArraySegment<byte> messageData, bool allowOutputBatching, TimeSpan timeout, Action<object> callback, object state)
        {
            Contract.Assert(callback != null, "callback should not be null.");
            ConnectionUtilities.ValidateBufferBounds(messageData);

            TimeoutHelper helper = new TimeoutHelper(timeout);
            WebSocketMessageType outgoingMessageType = GetWebSocketMessageType(message);

            if (TD.WebSocketAsyncWriteStartIsEnabled())
            {
                TD.WebSocketAsyncWriteStart(
                    WebSocket.GetHashCode(),
                    messageData.Count,
                    RemoteAddress != null ? RemoteAddress.ToString() : string.Empty);
            }

            Task task = WebSocket.SendAsync(messageData, outgoingMessageType, true, helper.CancellationToken);
            Contract.Assert(_pendingWritingMessageException == null, "'pendingWritingMessageException' MUST be NULL at this point.");

            if (task.IsCompleted)
            {
                if (TD.WebSocketAsyncWriteStopIsEnabled())
                {
                    TD.WebSocketAsyncWriteStop(WebSocket.GetHashCode());
                }

                _pendingWritingMessageException = WebSocketHelper.CreateExceptionOnTaskFailure(task, timeout, WebSocketHelper.SendOperation);
                return AsyncCompletionResult.Completed;
            }

            HandleSendAsyncCompletion(task, timeout, callback, state);
            return AsyncCompletionResult.Queued;
        }

        protected override void FinishWritingMessage()
        {
            ThrowOnPendingException(ref _pendingWritingMessageException);
            base.FinishWritingMessage();
        }

        protected override AsyncCompletionResult StartWritingStreamedMessage(Message message, TimeSpan timeout, Action<object> callback, object state)
        {
            WebSocketMessageType outgoingMessageType = GetWebSocketMessageType(message);
            WebSocketStream webSocketStream = new WebSocketStream(WebSocket, outgoingMessageType, ((IDefaultCommunicationTimeouts)this).CloseTimeout);

            _waitCallback = callback;
            _state = state;
            _webSocketStream = webSocketStream;
            IAsyncResult result = MessageEncoder.BeginWriteMessage(message, new TimeoutStream(webSocketStream, timeout), s_streamedWriteCallback, this);

            if (!result.CompletedSynchronously)
            {
                return AsyncCompletionResult.Queued;
            }

            MessageEncoder.EndWriteMessage(result);

            webSocketStream.WriteEndOfMessageAsync(callback, state);
            return AsyncCompletionResult.Queued;
        }

        // TODO: Make TimeoutHelper disposeable which disposes it's cancellation token source
        protected override AsyncCompletionResult BeginCloseOutput(TimeSpan timeout, Action<object> callback, object state)
        {
            Fx.Assert(callback != null, "callback should not be null.");

            var helper = new TimeoutHelper(timeout);
            Task task = CloseOutputAsync(helper.CancellationToken);
            Fx.Assert(_pendingWritingMessageException == null, "'pendingWritingMessageException' MUST be NULL at this point.");

            if (task.IsCompleted)
            {
                _pendingWritingMessageException = WebSocketHelper.CreateExceptionOnTaskFailure(task, timeout, WebSocketHelper.CloseOperation);
                return AsyncCompletionResult.Completed;
            }

            HandleCloseOutputAsyncCompletion(task, timeout, callback, state);
            return AsyncCompletionResult.Queued;
        }

        protected override void OnSendCore(Message message, TimeSpan timeout)
        {
            Fx.Assert(message != null, "message should not be null.");

            TimeoutHelper helper = new TimeoutHelper(timeout);
            WebSocketMessageType outgoingMessageType = GetWebSocketMessageType(message);

            if (IsStreamedOutput)
            {
                WebSocketStream webSocketStream = new WebSocketStream(WebSocket, outgoingMessageType, ((IDefaultCommunicationTimeouts)this).CloseTimeout);
                TimeoutStream timeoutStream = new TimeoutStream(webSocketStream, timeout);
                MessageEncoder.WriteMessage(message, timeoutStream);
                webSocketStream.WriteEndOfMessage();
            }
            else
            {
                ArraySegment<byte> messageData = EncodeMessage(message);
                bool success = false;
                try
                {
                    if (TD.WebSocketAsyncWriteStartIsEnabled())
                    {
                        TD.WebSocketAsyncWriteStart(
                            WebSocket.GetHashCode(),
                            messageData.Count,
                            RemoteAddress != null ? RemoteAddress.ToString() : string.Empty);
                    }

                    Task task = WebSocket.SendAsync(messageData, outgoingMessageType, true, helper.CancellationToken);
                    task.Wait(helper.RemainingTime(), WebSocketHelper.ThrowCorrectException, WebSocketHelper.SendOperation);

                    if (TD.WebSocketAsyncWriteStopIsEnabled())
                    {
                        TD.WebSocketAsyncWriteStop(_webSocket.GetHashCode());
                    }

                    success = true;
                }
                finally
                {
                    try
                    {
                        BufferManager.ReturnBuffer(messageData.Array);
                    }
                    catch (Exception ex)
                    {
                        if (Fx.IsFatal(ex) || success)
                        {
                            throw;
                        }

                        FxTrace.Exception.TraceUnhandledException(ex);
                    }
                }
            }
        }

        protected override ArraySegment<byte> EncodeMessage(Message message)
        {
            return MessageEncoder.WriteMessage(message, int.MaxValue, BufferManager, 0);
        }

        protected void Cleanup()
        {
            if (Interlocked.CompareExchange(ref _cleanupStatus, WebSocketHelper.OperationFinished, WebSocketHelper.OperationNotStarted) == WebSocketHelper.OperationNotStarted)
            {
                OnCleanup();
            }
        }

        protected virtual void OnCleanup()
        {
            Fx.Assert(_cleanupStatus == WebSocketHelper.OperationFinished,
                "This method should only be called by this.Cleanup(). Make sure that you never call overriden OnCleanup()-methods directly in subclasses");
            if (_shouldDisposeWebSocketAfterClosed && _webSocket != null)
            {
                _webSocket.Dispose();
            }

            if (_internalBuffer != null)
            {
                _bufferPool.Return(_internalBuffer);
                _internalBuffer = null;
            }
        }

        private static void ThrowOnPendingException(ref Exception pendingException)
        {
            Exception exceptionToThrow = pendingException;

            if (exceptionToThrow != null)
            {
                pendingException = null;
                throw FxTrace.Exception.AsError(exceptionToThrow);
            }
        }

        private Task CloseAsync()
        {
            try
            {
                return WebSocket.CloseAsync(_webSocketCloseDetails.OutputCloseStatus, _webSocketCloseDetails.OutputCloseStatusDescription, CancellationToken.None);
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                throw WebSocketHelper.ConvertAndTraceException(e);
            }
        }

        private Task CloseOutputAsync(CancellationToken cancellationToken)
        {
            try
            {
                return WebSocket.CloseOutputAsync(_webSocketCloseDetails.OutputCloseStatus, _webSocketCloseDetails.OutputCloseStatusDescription, cancellationToken);
            }
            catch (Exception e)
            {
                if (Fx.IsFatal(e))
                {
                    throw;
                }

                throw WebSocketHelper.ConvertAndTraceException(e);
            }
        }

        static WebSocketMessageType GetWebSocketMessageType(Message message)
        {
            return WebSocketDefaults.DefaultWebSocketMessageType;
        }

        private async void HandleCloseOutputAsyncCompletion(Task task, TimeSpan timeout, Action<object> callback, object state)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                _pendingWritingMessageException = WebSocketHelper.CreateExceptionOnTaskFailure(task, timeout, WebSocketHelper.CloseOperation);
            }
            finally
            {
                callback.Invoke(state);
            }
        }

        private async void HandleSendAsyncCompletion(Task task, TimeSpan timeout, Action<object> callback, object state)
        {
            try
            {
                await task;
            }
            catch (Exception)
            {
                _pendingWritingMessageException = WebSocketHelper.CreateExceptionOnTaskFailure(task, timeout,
                    WebSocketHelper.SendOperation);
            }
            finally
            {
                if (TD.WebSocketAsyncWriteStopIsEnabled())
                {
                    TD.WebSocketAsyncWriteStop(WebSocket.GetHashCode());
                }

                callback.Invoke(state);
            }
        }

        static void StreamWriteCallback(IAsyncResult ar)
        {
            if (ar.CompletedSynchronously)
            {
                return;
            }

            WebSocketTransportDuplexSessionChannel thisPtr = (WebSocketTransportDuplexSessionChannel)ar.AsyncState;

            try
            {
                thisPtr.MessageEncoder.EndWriteMessage(ar);
                thisPtr._webSocketStream.WriteEndOfMessage();
                thisPtr._waitCallback.Invoke(thisPtr._state);
            }
            catch (Exception ex)
            {
                if (Fx.IsFatal(ex))
                {
                    throw;
                }
            }
        }

        protected class WebSocketMessageSource : IMessageSource
        {
            MessageEncoder _encoder;
            BufferManager _bufferManager;
            EndpointAddress _localAddress;
            Message _pendingMessage;
            Exception _pendingException;
            WebSocket _webSocket;
            bool _closureReceived;
            bool _useStreaming;
            int _receiveBufferSize;
            int _maxBufferSize;
            long _maxReceivedMessageSize;
            TaskCompletionSource<object> _streamWaitTask;
            IDefaultCommunicationTimeouts _defaultTimeouts;
            WebSocketCloseDetails _closeDetails;
            TimeSpan _asyncReceiveTimeout;
            TaskCompletionSource<object> _receiveTask;
            int _asyncReceiveState;

            public WebSocketMessageSource(WebSocketTransportDuplexSessionChannel webSocketTransportDuplexSessionChannel, WebSocket webSocket,
                    bool useStreaming, IDefaultCommunicationTimeouts defaultTimeouts)
            {
                Initialize(webSocketTransportDuplexSessionChannel, webSocket, useStreaming, defaultTimeouts);

                StartNextReceiveAsync();
            }

            void Initialize(WebSocketTransportDuplexSessionChannel webSocketTransportDuplexSessionChannel, WebSocket webSocket, bool useStreaming, IDefaultCommunicationTimeouts defaultTimeouts)
            {
                _webSocket = webSocket;
                _encoder = webSocketTransportDuplexSessionChannel.MessageEncoder;
                _bufferManager = webSocketTransportDuplexSessionChannel.BufferManager;
                _localAddress = webSocketTransportDuplexSessionChannel.LocalAddress;
                _maxBufferSize = webSocketTransportDuplexSessionChannel.MaxBufferSize;
                _maxReceivedMessageSize = webSocketTransportDuplexSessionChannel.TransportFactorySettings.MaxReceivedMessageSize;
                _receiveBufferSize = Math.Min(WebSocketHelper.GetReceiveBufferSize(_maxReceivedMessageSize), _maxBufferSize);
                _useStreaming = useStreaming;
                _defaultTimeouts = defaultTimeouts;
                _closeDetails = webSocketTransportDuplexSessionChannel._webSocketCloseDetails;
                _asyncReceiveTimeout = _defaultTimeouts.ReceiveTimeout; 
                _asyncReceiveState = AsyncReceiveState.Finished;
            }

            static void OnAsyncReceiveCancelled(object target)
            {
                WebSocketMessageSource messageSource = (WebSocketMessageSource)target;
                messageSource.AsyncReceiveCancelled();
            }

            void AsyncReceiveCancelled()
            {
                if (Interlocked.CompareExchange(ref _asyncReceiveState, AsyncReceiveState.Cancelled, AsyncReceiveState.Started) == AsyncReceiveState.Started)
                {
                    _receiveTask.SetResult(null);
                }
            }

            public async Task<Message> ReceiveAsync(TimeSpan timeout)
            {
                bool waitingResult = await _receiveTask.Task.AwaitWithTimeout(timeout);
                ThrowOnPendingException(ref _pendingException);

                if (!waitingResult)
                {
                    throw FxTrace.Exception.AsError(new TimeoutException(
                               SR.Format(SR.WaitForMessageTimedOut, timeout),
                               TimeoutHelper.CreateEnterTimedOutException(timeout)));
                }

                Message message = GetPendingMessage();

                if (message != null)
                {
                    StartNextReceiveAsync();
                }

                return message;

            }

            // TODO: As we're waiting blocking on a task anyway, should just call ReceiveAsync and block on that task.
            public Message Receive(TimeSpan timeout)
            {
                bool waitingResult = _receiveTask.Task.Wait(timeout);
                ThrowOnPendingException(ref _pendingException);

                if (!waitingResult)
                {
                    throw FxTrace.Exception.AsError(new TimeoutException(
                               SR.Format(SR.WaitForMessageTimedOut, timeout),
                               TimeoutHelper.CreateEnterTimedOutException(timeout)));
                }

                Message message = GetPendingMessage();

                if (message != null)
                {
                    StartNextReceiveAsync();
                }

                return message;
            }

            async Task ReadBufferedMessageAsync()
            {
                byte[] internalBuffer = null;
                try
                {
                    internalBuffer = _bufferManager.TakeBuffer(_receiveBufferSize);

                    int receivedByteCount = 0;
                    bool endOfMessage = false;
                    WebSocketReceiveResult result = null;
                    do
                    {
                        try
                        {

                            if (TD.WebSocketAsyncReadStartIsEnabled())
                            {
                                TD.WebSocketAsyncReadStart(_webSocket.GetHashCode());
                            }

                            result = await _webSocket.ReceiveAsync(
                                                new ArraySegment<byte>(internalBuffer, receivedByteCount, internalBuffer.Length - receivedByteCount),
                                                CancellationToken.None);

                            CheckCloseStatus(result);
                            endOfMessage = result.EndOfMessage;

                            receivedByteCount += result.Count;
                            if (receivedByteCount >= internalBuffer.Length && !result.EndOfMessage)
                            {
                                if (internalBuffer.Length >= _maxBufferSize)
                                {
                                    _pendingException = FxTrace.Exception.AsError(new QuotaExceededException(SR.Format(SR.MaxReceivedMessageSizeExceeded, _maxBufferSize)));
                                    return;
                                }

                                int newSize = (int)Math.Min(((double)internalBuffer.Length) * 2, _maxBufferSize);
                                Fx.Assert(newSize > 0, "buffer size should be larger than zero.");
                                byte[] newBuffer = _bufferManager.TakeBuffer(newSize);
                                Buffer.BlockCopy(internalBuffer, 0, newBuffer, 0, receivedByteCount);
                                _bufferManager.ReturnBuffer(internalBuffer);
                                internalBuffer = newBuffer;
                            }

                            if (TD.WebSocketAsyncReadStopIsEnabled())
                            {
                                TD.WebSocketAsyncReadStop(
                                    _webSocket.GetHashCode(),
                                    receivedByteCount,
                                    string.Empty);
                            }
                        }
                        catch (AggregateException ex)
                        {
                            WebSocketHelper.ThrowCorrectException(ex, TimeSpan.MaxValue, WebSocketHelper.ReceiveOperation);
                        }

                    }
                    while (!endOfMessage && !_closureReceived);

                    byte[] buffer = null;
                    bool success = false;
                    try
                    {
                        buffer = _bufferManager.TakeBuffer(receivedByteCount);
                        Buffer.BlockCopy(internalBuffer, 0, buffer, 0, receivedByteCount);
                        Fx.Assert(result != null, "Result should not be null");
                        _pendingMessage = PrepareMessage(result, buffer, receivedByteCount);
                        success = true;
                    }
                    finally
                    {
                        if (buffer != null && (!success || _pendingMessage == null))
                        {
                            _bufferManager.ReturnBuffer(buffer);
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (Fx.IsFatal(ex))
                    {
                        throw;
                    }

                    _pendingException = WebSocketHelper.ConvertAndTraceException(ex, TimeSpan.MaxValue, WebSocketHelper.ReceiveOperation);
                }
                finally
                {
                    if (internalBuffer != null)
                    {
                        _bufferManager.ReturnBuffer(internalBuffer);
                    }
                }
            }

            public bool WaitForMessage(TimeSpan timeout)
            {
                try
                {
                    Message message = Receive(timeout);
                    _pendingMessage = message;
                    return true;
                }
                catch (TimeoutException exception)
                {
                    if (TD.ReceiveTimeoutIsEnabled())
                    {
                        TD.ReceiveTimeout(exception.Message);
                    }

                    return false;
                }
            }

            public async Task<bool> WaitForMessageAsync(TimeSpan timeout)
            {
                bool waitingResult = await _receiveTask.Task.AwaitWithTimeout(timeout);
                if (waitingResult)
                {
                    Message message = await ReceiveAsync(timeout);
                    _pendingMessage = message;
                    return true;
                }
                if (TD.ReceiveTimeoutIsEnabled())
                {
                    TD.ReceiveTimeout(SR.Format(SR.WaitForMessageTimedOut, timeout));
                }
                return false;
            }

            internal void FinishUsingMessageStream(Exception ex)
            {
                //// The pattern of the task here is:
                //// 1) Only one thread can get the stream and consume the stream. A new task will be created at the moment it takes the stream
                //// 2) Only one another thread can enter the lock and wait on the task
                //// 3) The cleanup on the stream will return the stream to message source. And the cleanup call is limited to be called only once.
                if (ex != null && _pendingException == null)
                {
                    _pendingException = ex;
                }

                _streamWaitTask.SetResult(null);
            }

            internal void CheckCloseStatus(WebSocketReceiveResult result)
            {
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (TD.WebSocketCloseStatusReceivedIsEnabled())
                    {
                        TD.WebSocketCloseStatusReceived(
                            _webSocket.GetHashCode(),
                            result.CloseStatus.ToString());
                    }

                    _closureReceived = true;
                    _closeDetails.InputCloseStatus = result.CloseStatus;
                    _closeDetails.InputCloseStatusDescription = result.CloseStatusDescription;
                }
            }

            async void StartNextReceiveAsync()
            {
                Fx.Assert(_receiveTask == null || _receiveTask.Task.IsCompleted, "this.receiveTask is not completed.");
                _receiveTask = new TaskCompletionSource<object>();
                int currentState = Interlocked.CompareExchange(ref _asyncReceiveState, AsyncReceiveState.Started, AsyncReceiveState.Finished);
                Fx.Assert(currentState == AsyncReceiveState.Finished, "currentState is not AsyncReceiveState.Finished: " + currentState);
                if (currentState != AsyncReceiveState.Finished)
                {
                    throw FxTrace.Exception.AsError(new InvalidOperationException());
                }

                try
                {
                    if (_useStreaming)
                    {
                        if (_streamWaitTask != null)
                        {
                            //// Wait until the previous stream message finished.
                            await _streamWaitTask.Task;
                        }

                        _streamWaitTask = new TaskCompletionSource<object>();
                    }

                    if (_pendingException == null)
                    {
                        if (!_useStreaming)
                        {
                            await ReadBufferedMessageAsync();
                        }
                        else
                        {
                            byte[] buffer = _bufferManager.TakeBuffer(_receiveBufferSize);
                            bool success = false;
                            try
                            {
                                if (TD.WebSocketAsyncReadStartIsEnabled())
                                {
                                    TD.WebSocketAsyncReadStart(_webSocket.GetHashCode());
                                }

                                try
                                {
                                    WebSocketReceiveResult result;
                                    using (CancellationTokenSource cts = new CancellationTokenSource(_asyncReceiveTimeout))
                                    {
                                        result = await _webSocket.ReceiveAsync(
                                                    new ArraySegment<byte>(buffer, 0, _receiveBufferSize),
                                                    cts.Token);
                                    }
                                    CheckCloseStatus(result);
                                    _pendingMessage = PrepareMessage(result, buffer, result.Count);

                                    if (TD.WebSocketAsyncReadStopIsEnabled())
                                    {
                                        TD.WebSocketAsyncReadStop(
                                            _webSocket.GetHashCode(),
                                            result.Count,
                                            String.Empty);
                                    }
                                }
                                catch (AggregateException ex)
                                {
                                    WebSocketHelper.ThrowCorrectException(ex, _asyncReceiveTimeout, WebSocketHelper.ReceiveOperation);
                                }
                                success = true;
                            }
                            catch (Exception ex)
                            {
                                if (Fx.IsFatal(ex))
                                {
                                    throw;
                                }

                                _pendingException = WebSocketHelper.ConvertAndTraceException(ex, _asyncReceiveTimeout, WebSocketHelper.ReceiveOperation);
                            }
                            finally
                            {
                                if (!success)
                                {
                                    _bufferManager.ReturnBuffer(buffer);
                                }
                            }
                        }
                    }
                }
                finally
                {
                    if (Interlocked.CompareExchange(ref _asyncReceiveState, AsyncReceiveState.Finished, AsyncReceiveState.Started) == AsyncReceiveState.Started)
                    {
                        _receiveTask.SetResult(null);
                    }
                }
            }

            Message GetPendingMessage()
            {
                ThrowOnPendingException(ref _pendingException);

                if (_pendingMessage != null)
                {
                    Message pendingMessage = _pendingMessage;
                    _pendingMessage = null;
                    return pendingMessage;
                }

                return null;
            }

            Message PrepareMessage(WebSocketReceiveResult result, byte[] buffer, int count)
            {
                if (result.MessageType != WebSocketMessageType.Close)
                {
                    Message message;
                    if (_useStreaming)
                    {
                        using (var wrappedStream = new MaxMessageSizeStream(
                            new TimeoutStream(
                                new WebSocketStream(
                                    this,
                                    new ArraySegment<byte>(buffer, 0, count),
                                    _webSocket,
                                    result.EndOfMessage,
                                    _bufferManager,
                                    _defaultTimeouts.CloseTimeout),
                                _defaultTimeouts.ReceiveTimeout),
                            _maxReceivedMessageSize))
                        {
                            message = _encoder.ReadMessage(wrappedStream, _maxBufferSize);
                        }
                    }
                    else
                    {
                        ArraySegment<byte> bytes = new ArraySegment<byte>(buffer, 0, count);
                        message = _encoder.ReadMessage(bytes, _bufferManager);
                    }

                    if (message.Version.Addressing != AddressingVersion.None || !_localAddress.IsAnonymous)
                    {
                        _localAddress.ApplyTo(message);
                    }

                    if (message.Version.Addressing == AddressingVersion.None && message.Headers.Action == null)
                    {
                        if (result.MessageType == WebSocketMessageType.Binary)
                        {
                            message.Headers.Action = WebSocketTransportSettings.BinaryMessageReceivedAction;
                        }
                        else
                        {
                            // WebSocketMesssageType should always be binary or text at this moment. The layer below us will help protect this.
                            Fx.Assert(result.MessageType == WebSocketMessageType.Text, "result.MessageType must be WebSocketMessageType.Text.");
                            message.Headers.Action = WebSocketTransportSettings.TextMessageReceivedAction;
                        }
                    }

                    return message;
                }

                return null;
            }

            static class AsyncReceiveState
            {
                internal const int Started = 0;
                internal const int Finished = 1;
                internal const int Cancelled = 2;
            }
        }

        class WebSocketStream : Stream
        {
            readonly WebSocket _webSocket;
            readonly WebSocketMessageSource _messageSource;
            readonly TimeSpan _closeTimeout;
            ArraySegment<byte> _initialReadBuffer;
            bool _endOfMessageReached;
            readonly bool _isForRead;
            bool _endofMessageReceived;
            readonly WebSocketMessageType _outgoingMessageType;
            readonly BufferManager _bufferManager;
            int _messageSourceCleanState;
            int _endOfMessageWritten;
            int _readTimeout;
            int _writeTimeout;
            TimeoutHelper _readTimeoutHelper;
            TimeoutHelper _writeTimeoutHelper;

            public WebSocketStream(
                        WebSocketMessageSource messageSource,
                        ArraySegment<byte> initialBuffer,
                        WebSocket webSocket,
                        bool endofMessageReceived,
                        BufferManager bufferManager,
                        TimeSpan closeTimeout)
                : this(webSocket, WebSocketDefaults.DefaultWebSocketMessageType, closeTimeout)
            {
                Fx.Assert(messageSource != null, "messageSource should not be null.");
                _messageSource = messageSource;
                _initialReadBuffer = initialBuffer;
                _isForRead = true;
                _endofMessageReceived = endofMessageReceived;
                _bufferManager = bufferManager;
                _messageSourceCleanState = WebSocketHelper.OperationNotStarted;
                _endOfMessageWritten = WebSocketHelper.OperationNotStarted;
            }

            public WebSocketStream(
                    WebSocket webSocket,
                    WebSocketMessageType outgoingMessageType,
                    TimeSpan closeTimeout)
            {
                Fx.Assert(webSocket != null, "webSocket should not be null.");
                _webSocket = webSocket;
                _isForRead = false;
                _outgoingMessageType = outgoingMessageType;
                _messageSourceCleanState = WebSocketHelper.OperationFinished;
                _closeTimeout = closeTimeout;
            }

            public override bool CanRead
            {
                get { return _isForRead; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanTimeout
            {
                get
                {
                    return true;
                }
            }

            public override bool CanWrite
            {
                get { return !_isForRead; }
            }

            public override long Length
            {
                get { throw FxTrace.Exception.AsError(new NotSupportedException(InternalSR.SeekNotSupported)); }
            }

            public override long Position
            {
                get
                {
                    throw FxTrace.Exception.AsError(new NotSupportedException(InternalSR.SeekNotSupported));
                }

                set
                {
                    throw FxTrace.Exception.AsError(new NotSupportedException(InternalSR.SeekNotSupported));
                }
            }

            public override int ReadTimeout
            {
                get
                {
                    return _readTimeout;
                }

                set
                {
                    Contract.Assert(value >= 0, "ReadTimeout should not be negative.");
                    _readTimeout = value;
                    _readTimeoutHelper = new TimeoutHelper(TimeoutHelper.FromMilliseconds(_readTimeout));
                }
            }

            public override int WriteTimeout
            {
                get
                {
                    return _writeTimeout;
                }

                set
                {
                    Contract.Assert(value >= 0, "WriteTimeout should not be negative.");
                    _writeTimeout = value;
                    _writeTimeoutHelper = new TimeoutHelper(TimeoutHelper.FromMilliseconds(_readTimeout));
                }
            }

            protected override void Dispose(bool disposing)
            {
                base.Dispose(disposing);
                Cleanup();
            }

            public override void Flush()
            {
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Contract.Assert(_messageSource != null, "messageSource should not be null in read case.");

                if (_readTimeoutHelper.CancellationToken.IsCancellationRequested)
                {
                    throw FxTrace.Exception.AsError(WebSocketHelper.GetTimeoutException(null,
                        _readTimeoutHelper.OriginalTimeout, WebSocketHelper.ReceiveOperation));
                }

                if (_endOfMessageReached)
                {
                    return Task.FromResult(0);
                }

                if (_initialReadBuffer.Count != 0)
                {
                    return Task.FromResult(GetBytesFromInitialReadBuffer(buffer, offset, count));
                }

                if (_endofMessageReceived)
                {
                    _endOfMessageReached = true;
                    Cleanup();
                    return Task.FromResult(0);
                }

                return ReadAsyncCore(buffer, offset, count, cancellationToken);
            }

            private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                int receivedBytes = 0;

                if (TD.WebSocketAsyncReadStartIsEnabled())
                {
                    TD.WebSocketAsyncReadStart(_webSocket.GetHashCode());
                }

                WebSocketReceiveResult result;
                try
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer, offset, count), cancellationToken);
                }
                catch (Exception ex)
                {
                    if (Fx.IsFatal(ex))
                    {
                        throw;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw Fx.Exception.AsError(new TimeoutException(InternalSR.TaskTimedOutError(new TimeSpan(ReadTimeout))));
                    }

                    throw WebSocketHelper.ConvertAndTraceException(ex, new TimeSpan(ReadTimeout), WebSocketHelper.ReceiveOperation);
                }

                if (result.EndOfMessage)
                {
                    _endofMessageReceived = true;
                    _endOfMessageReached = true;
                }

                receivedBytes = result.Count;
                CheckResultAndEnsureNotCloseMessage(_messageSource, result);

                if (TD.WebSocketAsyncReadStopIsEnabled())
                {
                    TD.WebSocketAsyncReadStop(_webSocket.GetHashCode(), receivedBytes, string.Empty);
                }

                if (_endOfMessageReached)
                {
                    Cleanup();
                }

                return receivedBytes;
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                // WebSocketStream is never used directly but is wrapped in a TimeoutStream which calls the Async
                // implementation in the synchronous Read method. 
                throw FxTrace.Exception.AsError(new NotSupportedException("this method should never get called"));
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw FxTrace.Exception.AsError(new NotSupportedException());
            }

            public override void SetLength(long value)
            {
                throw FxTrace.Exception.AsError(new NotSupportedException());
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                // WebSocketStream is never used directly but is wrapped in a TimeoutStream which calls the Async
                // implementation in the synchronous Write method. 
                throw FxTrace.Exception.AsError(new NotSupportedException("this method should never get called"));
            }

            public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                if (_endOfMessageWritten == WebSocketHelper.OperationFinished)
                {
                    throw FxTrace.Exception.AsError(new InvalidOperationException(SR.WebSocketStreamWriteCalledAfterEOMSent));
                }

                if (WriteTimeout <= 0)
                {
                    throw FxTrace.Exception.AsError(WebSocketHelper.GetTimeoutException(null, TimeoutHelper.FromMilliseconds(WriteTimeout), WebSocketHelper.SendOperation));
                }

                if (TD.WebSocketAsyncWriteStartIsEnabled())
                {
                    TD.WebSocketAsyncWriteStart(
                            _webSocket.GetHashCode(),
                            count,
                            string.Empty);
                }

                try
                {
                    await _webSocket.SendAsync(new ArraySegment<byte>(buffer, offset, count), _outgoingMessageType, false, cancellationToken);
                }
                catch (Exception ex)
                {
                    if (Fx.IsFatal(ex))
                    {
                        throw;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        throw Fx.Exception.AsError(new TimeoutException(InternalSR.TaskTimedOutError(new TimeSpan(WriteTimeout))));
                    }

                    throw WebSocketHelper.ConvertAndTraceException(ex, new TimeSpan(WriteTimeout), WebSocketHelper.SendOperation);
                }

                if (TD.WebSocketAsyncWriteStopIsEnabled())
                {
                    TD.WebSocketAsyncWriteStop(_webSocket.GetHashCode());
                }
            }

            private async Task WriteAsyncInternal(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                await TaskHelpers.EnsureDefaultTaskScheduler();
                await WriteAsync(buffer, offset, count, cancellationToken);
            }

            public void WriteEndOfMessage()
            {
                if (TD.WebSocketAsyncWriteStartIsEnabled())
                {
                    TD.WebSocketAsyncWriteStart(
                            _webSocket.GetHashCode(),
                            0,
                            string.Empty);
                }

                var timeoutHelper = new TimeoutHelper(_closeTimeout);

                if (Interlocked.CompareExchange(ref _endOfMessageWritten, WebSocketHelper.OperationFinished, WebSocketHelper.OperationNotStarted) == WebSocketHelper.OperationNotStarted)
                {
                    Task task = _webSocket.SendAsync(new ArraySegment<byte>(Array.Empty<byte>(), 0, 0), _outgoingMessageType, true, timeoutHelper.CancellationToken);
                    task.Wait(timeoutHelper.RemainingTime(), WebSocketHelper.ThrowCorrectException, WebSocketHelper.SendOperation);
                }

                if (TD.WebSocketAsyncWriteStopIsEnabled())
                {
                    TD.WebSocketAsyncWriteStop(_webSocket.GetHashCode());
                }
            }

            public async void WriteEndOfMessageAsync(Action<object> callback, object state)
            {
                if (TD.WebSocketAsyncWriteStartIsEnabled())
                {
                    // TODO: Open bug about not emitting the hostname/port
                    TD.WebSocketAsyncWriteStart(
                        _webSocket.GetHashCode(),
                        0,
                        string.Empty);
                }

                using (var timeoutHelper = new TimeoutHelper(_closeTimeout))
                {
                    try
                    {
                        await
                            _webSocket.SendAsync(new ArraySegment<byte>(Array.Empty<byte>(), 0, 0), _outgoingMessageType,
                                true, timeoutHelper.CancellationToken);

                        if (TD.WebSocketAsyncWriteStopIsEnabled())
                        {
                            TD.WebSocketAsyncWriteStop(_webSocket.GetHashCode());
                        }
                    }
                    catch (Exception ex)
                    {
                        if (Fx.IsFatal(ex))
                        {
                            throw;
                        }

                        if (timeoutHelper.CancellationToken.IsCancellationRequested)
                        {
                            throw Fx.Exception.AsError(
                                new TimeoutException(InternalSR.TaskTimedOutError(timeoutHelper.OriginalTimeout)));
                        }

                        throw WebSocketHelper.ConvertAndTraceException(ex, timeoutHelper.OriginalTimeout,
                            WebSocketHelper.SendOperation);

                    }
                    finally
                    {
                        callback.Invoke(state);
                    }
                }
            }

            static void CheckResultAndEnsureNotCloseMessage(WebSocketMessageSource messageSource, WebSocketReceiveResult result)
            {
                messageSource.CheckCloseStatus(result);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw FxTrace.Exception.AsError(new ProtocolException(SR.WebSocketUnexpectedCloseMessageError));
                }
            }

            int GetBytesFromInitialReadBuffer(byte[] buffer, int offset, int count)
            {
                int bytesToCopy = _initialReadBuffer.Count > count ? count : _initialReadBuffer.Count;
                Buffer.BlockCopy(_initialReadBuffer.Array, _initialReadBuffer.Offset, buffer, offset, bytesToCopy);
                _initialReadBuffer = new ArraySegment<byte>(_initialReadBuffer.Array, _initialReadBuffer.Offset + bytesToCopy, _initialReadBuffer.Count - bytesToCopy);
                return bytesToCopy;
            }

            void Cleanup()
            {
                if (_isForRead)
                {
                    if (Interlocked.CompareExchange(ref _messageSourceCleanState, WebSocketHelper.OperationFinished, WebSocketHelper.OperationNotStarted) == WebSocketHelper.OperationNotStarted)
                    {
                        Exception pendingException = null;
                        try
                        {
                            if (!_endofMessageReceived && (_webSocket.State == WebSocketState.Open || _webSocket.State == WebSocketState.CloseSent))
                            {
                                // Drain the reading stream
                                using (var closeTimeoutHelper = new TimeoutHelper(_closeTimeout))
                                {
                                    do
                                    {
                                        Task<WebSocketReceiveResult> receiveTask =
                                            _webSocket.ReceiveAsync(new ArraySegment<byte>(_initialReadBuffer.Array),
                                                closeTimeoutHelper.CancellationToken);
                                        receiveTask.Wait(closeTimeoutHelper.RemainingTime(),
                                            WebSocketHelper.ThrowCorrectException, WebSocketHelper.ReceiveOperation);
                                        _endofMessageReceived = receiveTask.GetAwaiter().GetResult().EndOfMessage;
                                    } while (!_endofMessageReceived &&
                                             (_webSocket.State == WebSocketState.Open ||
                                              _webSocket.State == WebSocketState.CloseSent));
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (Fx.IsFatal(ex))
                            {
                                throw;
                            }

                            // Not throwing out this exception during stream cleanup. The exception
                            // will be thrown out when we are trying to receive the next message using the same
                            // WebSocket object.
                            pendingException = WebSocketHelper.ConvertAndTraceException(ex, _closeTimeout, WebSocketHelper.CloseOperation);
                        }

                        _bufferManager.ReturnBuffer(_initialReadBuffer.Array);
                        Fx.Assert(_messageSource != null, "messageSource should not be null.");
                        _messageSource.FinishUsingMessageStream(pendingException);
                    }
                }
                else
                {
                    if (Interlocked.CompareExchange(ref _endOfMessageWritten, WebSocketHelper.OperationFinished, WebSocketHelper.OperationNotStarted) == WebSocketHelper.OperationNotStarted)
                    {
                        WriteEndOfMessage();
                    }
                }
            }
        }

        class WebSocketCloseDetails
        {
            WebSocketCloseStatus _outputCloseStatus = WebSocketCloseStatus.NormalClosure;
            string _outputCloseStatusDescription;

            public WebSocketCloseStatus? InputCloseStatus { get; internal set; }

            public string InputCloseStatusDescription { get; internal set; }

            internal WebSocketCloseStatus OutputCloseStatus
            {
                get
                {
                    return _outputCloseStatus;
                }
            }

            internal string OutputCloseStatusDescription
            {
                get
                {
                    return _outputCloseStatusDescription;
                }
            }

            public void SetOutputCloseStatus(WebSocketCloseStatus closeStatus, string closeStatusDescription)
            {
                _outputCloseStatus = closeStatus;
                _outputCloseStatusDescription = closeStatusDescription;
            }
        }
    }
}
