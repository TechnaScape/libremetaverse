/*
 * Copyright (c) 2006-2016, openmetaverse.co
 * Copyright (c) 2022-2025, Sjofn LLC.
 * All rights reserved.
 *
 * - Redistribution and use in source and binary forms, with or without
 *   modification, are permitted provided that the following conditions are met:
 *
 * - Redistributions of source code must retain the above copyright notice, this
 *   list of conditions and the following disclaimer.
 * - Neither the name of the openmetaverse.co nor the names
 *   of its contributors may be used to endorse or promote products derived from
 *   this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using LibreMetaverse.Messages.Linden;
using LibreMetaverse.StructuredData;

namespace LibreMetaverse.Http
{
    /// <summary>EventQueueClient manages the polling-based EventQueueGet capability</summary>
    public class EventQueueClient : IDisposable
    {
        // Exponential backoff bounds for transient HTTP/network errors.
        private const int InitialEqRetryDelayMs = 1_000;
        private const int MaxEqRetryDelayMs = 30_000;

        // [SLUnity] A simulator holds a poll for twenty to thirty seconds and then ends it however
        // its proxies happen to: 499, 500, 502 to 504, or -- on this runtime -- a dropped connection
        // that Mono reports as ReceiveFailure. Held this long, any of them means "no events", and the
        // reference re-polls at once (MIN_SECONDS_PASSED, lleventpoll.cpp); only an early end is a
        // failure. Counting the ordinary end as one backed every quiet neighbour off to twenty or
        // thirty seconds, so the first events it sent after the agent crossed into it waited that long.
        private const double MinimumHeldSeconds = 10.0;

        // How long the request being handled was held. Written and read on the polling task only.
        private double _heldSeconds;

        // Milliseconds to wait before the next request; written by RequestCompletedHandler,
        // read by the polling loop and reset only after recovery. Accessed from the EQ task so no
        // Interlocked is needed, but volatile prevents stale reads across the await boundary.
        private volatile int _pendingRetryDelayMs;

        // Failures since the last good poll. [SLUnity]
        private int _consecutiveFailures;

        public delegate void ConnectedCallback();
        public delegate void EventCallback(string eventName, OSDMap body);

        public ConnectedCallback? OnConnected;
        public EventCallback? OnEvent;

        public bool Running => _queueCts != null && !_queueCts.IsCancellationRequested
                               && _eqTask != null && !_eqTask.IsCompleted;

        protected readonly Uri Address;
        protected readonly Simulator Simulator;
        private CancellationTokenSource? _queueCts;
        private Task? _eqTask;

        private readonly object _payloadLock = new object();
        private OSDMap? _reqPayloadMap;
        private byte[]? _reqPayloadBytes;

        public EventQueueClient(Uri eventQueueLocation, Simulator sim)
        {
            Address = eventQueueLocation;
            Simulator = sim;
            _queueCts = new CancellationTokenSource();
        }

        /// <summary>
        /// Dispose resources deterministically
        /// </summary>
        public void Dispose()
        {
            try
            {
                Stop(true);
            }
            catch { /* noop */ }

            // Ensure task is observed/cleaned up
            try
            {
                if (_eqTask != null)
                {
                    if (_eqTask.IsFaulted && _eqTask.Exception != null)
                    {
                        Logger.Error($"EventQueueClient background task faulted during dispose: {_eqTask.Exception}");
                    }
                }
            }
            catch { /* noop */ }

            // Atomically take ownership and dispose
            var oldCts = Interlocked.Exchange(ref _queueCts, null);
            DisposalHelper.SafeCancelAndDispose(oldCts);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Starts event queue polling if it isn't already running.
        /// </summary>
        public void Start()
        {
            if (!Running)
            {
                Create();
            }
        }

        /// <summary>
        /// Returns the next backoff delay using binary exponential back-off with a small
        /// jitter derived from Environment.TickCount so multiple clients don't thunderbird
        /// at the same moment.
        /// </summary>
        private static int NextRetryDelay(int currentMs)
        {
            int next = currentMs == 0 ? InitialEqRetryDelayMs
                                      : Math.Min(MaxEqRetryDelayMs, currentMs * 2);
            // Add 0–12.5% jitter using TickCount as a cheap pseudo-random source.
            next += (int)((uint)Environment.TickCount % (uint)(next / 8 + 1));
            return Math.Min(MaxEqRetryDelayMs, next);
        }

        private void Create()
        {
            // Create an EventQueueGet request
            var eqAck = new EventQueueAck { Done = false };
            var initial = eqAck.Serialize() as OSDMap ?? new OSDMap { ["done"] = OSD.FromBoolean(false) };

            lock (_payloadLock)
            {
                _reqPayloadMap = initial;
                _reqPayloadBytes = OSDParser.SerializeLLSDXmlBytes(_reqPayloadMap);
            }

            // Atomically replace previous CTS and dispose it
            var newCts = new CancellationTokenSource();
            var prev = Interlocked.Exchange(ref _queueCts, newCts);
            DisposalHelper.SafeCancelAndDispose(prev);

            // Capture the token once: Dispose()/a later Create() can cancel-and-dispose
            // this same CTS from another thread while this loop is still running (it only
            // observes cancellation between awaits), and CancellationTokenSource.Token's
            // getter throws ObjectDisposedException once disposed — which is not an
            // OperationCanceledException, so it would fault the task instead of exiting
            // cleanly. Reading IsCancellationRequested on an already-obtained token stays
            // safe after the source is disposed, so re-deriving the token from newCts
            // anywhere below this line must be avoided.
            var token = newCts.Token;

            _pendingRetryDelayMs = 0;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            _connectedNotified = false;

            _eqTask = Task.Run(async () =>
            {
                try
                {
                    // First request is immediate.
                    await ack().ConfigureAwait(false);

                    while (!token.IsCancellationRequested)
                    {
                        int delayMs = _pendingRetryDelayMs;

                        if (delayMs > 0)
                            await Task.Delay(delayMs, token).ConfigureAwait(false);

                        if (token.IsCancellationRequested) break;

                        await ack().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) { }
            }, token);

            async Task ack()
            {
                try
                {
                    byte[] payloadSnapshot;
                    lock (_payloadLock)
                    {
                        payloadSnapshot = _reqPayloadBytes;
                    }

                    // Fallback if for some reason payload is null
                    if (payloadSnapshot == null)
                    {
                        // serialize under lock
                        lock (_payloadLock)
                        {
                            _reqPayloadBytes = OSDParser.SerializeLLSDXmlBytes(_reqPayloadMap ?? new OSDMap());
                            payloadSnapshot = _reqPayloadBytes;
                        }
                    }

                    var held = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        var (response, data) = await Simulator.Client.HttpCapsClient.PostAsync(
                            // These bytes already contain the LLSD XML map. The OSDFormat
                            // overload implicitly converts byte[] to OSDBinary and serializes
                            // it again, hiding ack/done from the server inside a binary value.
                            Address, HttpCapsClient.LLSD_XML, payloadSnapshot, token).ConfigureAwait(false);
                        _heldSeconds = held.Elapsed.TotalSeconds;
                        RequestCompletedHandler(response, data, null);
                    }
                    catch (OperationCanceledException timeout) when (!token.IsCancellationRequested)
                    {
                        _heldSeconds = held.Elapsed.TotalSeconds;
                        RequestCompletedHandler(null, null, timeout);
                    }
                    catch (Exception innerEx) when (!(innerEx is OperationCanceledException))
                    {
                        _heldSeconds = held.Elapsed.TotalSeconds;
                        RequestCompletedHandler(null, null, innerEx);
                    }
                }
                catch (OperationCanceledException)
                {
                    // noop, cancellation is expected when stopping
                }
                catch (Exception ex)
                {
                    Logger.Error($"Exception sending EventQueue POST to {Simulator}: {ex.Message}", ex);
                }
            }
        }

        /// <summary>
        /// Stop the event queue
        /// </summary>
        /// <param name="immediate">quite honestly does nothing.</param>
        public void Stop(bool immediate)
        {
            // Atomically take ownership and cancel/dispose the CTS
            var old = Interlocked.Exchange(ref _queueCts, null);
            DisposalHelper.SafeCancelAndDispose(old);

            // Wait a short time for the background task to finish so resources are cleaned up
            try
            {
                if (_eqTask != null)
                {
                    // Wait up to 2 seconds for the repeating task to stop and observe exceptions
                    DisposalHelper.SafeWaitTask(_eqTask, TimeSpan.FromSeconds(2), (m, ex) =>
                    {
                        if (ex == null)
                        {
                            Logger.Debug($"{m} for {Simulator}");
                        }
                        else
                        {
                            Logger.Error(m, ex);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Exception while waiting for EventQueueClient task to stop: {ex.Message}", ex);
            }
        }

        private void ConnectedResponseHandler(HttpResponseMessage? response)
        {
            if (response?.IsSuccessStatusCode != true) { return; }

            // The event queue is starting up for the first time
            if (OnConnected == null) { return; }
            try
            {
                OnConnected();
            }
            catch (Exception ex)
            {
                Logger.Error(ex.Message, ex);
            }
        }

        /// <summary>
        /// Determine whether an HTTP response payload is likely LLSD/XML and therefore safe to attempt LLSD parsing.
        /// Checks the Content-Type header first (if present) and otherwise peeks up to the first 256 bytes
        /// of the payload to detect HTML/DOCTYPE bodies or LLSD/XML markers. Centralized to keep parsing
        /// decision logic in one place and to provide consistent logging behaviour.
        /// </summary>
        /// <param name="response">The HTTP response message (might be null).</param>
        /// <param name="data">The response body bytes.</param>
        /// <returns>True if the payload should be treated as LLSD/XML and can be parsed; false otherwise.</returns>
        private static bool IsLikelyLLSD(HttpResponseMessage? response, byte[]? data)
        {
            if (data == null || data.Length == 0) return false;

            // Prefer a canonical content-type check when available
            string? mediaType = null;
            try { mediaType = response?.Content?.Headers?.ContentType?.MediaType; } catch { mediaType = null; }
            if (!string.IsNullOrEmpty(mediaType))
            {
                var mt = mediaType!.ToLowerInvariant();
                if (mt.Contains("xml") || mt.Contains("llsd"))
                    return true;
                // Content type explicitly present and not XML-like -> avoid parsing as LLSD
                return false;
            }

            // No content-type header: peek at the start of the body
            string prefix;
            try { prefix = System.Text.Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 256)).TrimStart(); } catch { return false; }

            // Common non-LLSD payloads we want to reject quickly
            if (prefix.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
                prefix.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                prefix.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase))
                return false;

            // Common LLSD/XML markers
            if (prefix.StartsWith("<? LLSD/", StringComparison.Ordinal) ||
                prefix.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase) ||
                prefix.StartsWith("<llsd", StringComparison.OrdinalIgnoreCase))
                return true;

            // If it starts with a '<' and none of the HTML doctype matches above, treat as XML-like
            if (prefix.StartsWith("<"))
                return true;

            return false;
        }

        private long _batches, _events, _transportFailures, _httpFailures;
        private long _invalidResponses, _invalidEvents, _normalTimeouts, _lastSuccessTicks;
        private string _lastFailure = "none";
        private bool _connectedNotified;

        /// <summary>Read-only diagnostics, without capability URLs or event payloads.</summary>
        public string DescribeHealth()
        {
            long ticks = Interlocked.Read(ref _lastSuccessTicks);
            string last = ticks == 0 ? "never" : new DateTime(ticks, DateTimeKind.Utc).ToString("O");
            return $"running={Running} batches={Interlocked.Read(ref _batches)} events={Interlocked.Read(ref _events)} "
                + $"transportFailures={Interlocked.Read(ref _transportFailures)} httpFailures={Interlocked.Read(ref _httpFailures)} "
                + $"invalidResponses={Interlocked.Read(ref _invalidResponses)} invalidEvents={Interlocked.Read(ref _invalidEvents)} "
                + $"idleTimeouts={Interlocked.Read(ref _normalTimeouts)} retryMs={_pendingRetryDelayMs} "
                + $"lastSuccess={last} lastFailure={Volatile.Read(ref _lastFailure)}";
        }

        private static bool IsIdleTimeout(Exception error)
        {
            for (Exception? part = error; part != null; part = part.InnerException)
            {
                if (part is OperationCanceledException) return true;
                if (part is WebException web && (web.Status == WebExceptionStatus.ConnectionClosed
                    || web.Status == WebExceptionStatus.KeepAliveFailure || web.Status == WebExceptionStatus.Timeout)) return true;
#if NET8_0_OR_GREATER
                if (part is HttpRequestException http && http.HttpRequestError == HttpRequestError.ResponseEnded) return true;
#endif
            }
            return false;
        }

        private bool HeldLikeALongPoll => _heldSeconds >= MinimumHeldSeconds;

        // lleventpoll.cpp: a timeout, 500, 502, 503 or 504 held that long is "no events", and its
        // comment names Linden's own 499 as the same thing.
        private static bool IsLongPollEnding(int status)
            => status == 499 || status == 500 || status == 502 || status == 503 || status == 504;

        private void Idle()
        {
            Interlocked.Increment(ref _normalTimeouts);
            _pendingRetryDelayMs = 0;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }

        private static string DescribeException(Exception error)
        {
            var detail = new System.Text.StringBuilder();
            for (Exception? part = error; part != null; part = part.InnerException)
            {
                if (detail.Length > 0) detail.Append(" -> ");
                detail.Append(part.GetType().Name);
                if (part is WebException web) detail.Append('(').Append(web.Status).Append(')');
                if (part is System.Net.Sockets.SocketException socket) detail.Append('(').Append(socket.SocketErrorCode).Append(')');
                detail.Append(": ").Append(part.Message.Replace('\r', ' ').Replace('\n', ' '));
            }
            // Capabilities carry session secrets in their paths. Do not put them in F8 reports.
            return System.Text.RegularExpressions.Regex.Replace(detail.ToString(), @"https?://[^\s'""<>]+", "[URL]");
        }

        private void Failed(string reason)
        {
            Volatile.Write(ref _lastFailure, reason);
            _pendingRetryDelayMs = NextRetryDelay(_pendingRetryDelayMs);

            // [SLUnity] A poll the simulator held and then ended never reaches here (see
            // MinimumHeldSeconds). What does -- an early end, a refusal, a bad batch -- is still
            // usually a blip the next poll carries on from, so only a run of them is worth a
            // warning; the first two are information.
            string held = _heldSeconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
            string line = $"Event queue at {Simulator}: {reason} after {held} s; retry in {_pendingRetryDelayMs} ms";
            if (Interlocked.Increment(ref _consecutiveFailures) <= 2) Logger.Info(line);
            else Logger.Warn(line);
        }

        private void RequestCompletedHandler(HttpResponseMessage? response, byte[]? responseData, Exception? error)
        {
            if (!Simulator.Connected) return;

            if (error != null)
            {
                if (IsIdleTimeout(error) || HeldLikeALongPoll)
                {
                    Idle();
                }
                else
                {
                    Interlocked.Increment(ref _transportFailures);
                    Failed("transport failure before a complete response: " + DescribeException(error));
                }
                return; // Never replace the previous acknowledgement on a failed request.
            }

            if (response?.IsSuccessStatusCode != true)
            {
                int status = response == null ? 0 : (int)response.StatusCode;
                if (status == 404 || status == 410)
                {
                    Interlocked.Increment(ref _httpFailures);
                    Volatile.Write(ref _lastFailure, $"HTTP {status}: capability expired");
                    Logger.Info($"Closing event queue at {Simulator}: HTTP {status}");
                    var source = Volatile.Read(ref _queueCts);
                    try { source?.Cancel(); } catch (ObjectDisposedException) { }
                }
                else if (HeldLikeALongPoll && IsLongPollEnding(status))
                {
                    // The simulator's "no events". No HTTP error is a batch to acknowledge.
                    Idle();
                }
                else
                {
                    Interlocked.Increment(ref _httpFailures);
                    // An early end. No HTTP error response is a new batch to acknowledge.
                    Failed($"HTTP {status} ({responseData?.Length ?? 0} bytes)");
                }
                return;
            }

            OSDMap result;
            OSDArray events;
            OSD ack;
            try
            {
                if (!IsLikelyLLSD(response, responseData))
                    throw new FormatException("non-LLSD or empty response");
                if (!(OSDParser.DeserializeLLSDXml(responseData!) is OSDMap parsed)
                    || !(parsed["events"] is OSDArray batch)
                    || parsed["id"].Type != OSDType.Integer)
                    throw new FormatException("expected an integer id and an events array");
                result = parsed;
                events = batch;
                ack = result["id"];
            }
            catch (Exception parseError)
            {
                Interlocked.Increment(ref _invalidResponses);
                Failed($"invalid LLSD response ({responseData?.Length ?? 0} bytes): {DescribeException(parseError)}");
                return;
            }

            _pendingRetryDelayMs = 0;
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            if (!_connectedNotified)
            {
                _connectedNotified = true;
                ConnectedResponseHandler(response);
            }

            // Already on the background polling task. Dispatch in server order and observe
            // callback failures before advancing the acknowledgement; Task.Run reordered edits.
            foreach (OSD item in events)
            {
                try
                {
                    if (!(item is OSDMap evt) || evt["message"].Type != OSDType.String
                        || string.IsNullOrEmpty(evt["message"].AsString()) || !(evt["body"] is OSDMap body))
                        throw new FormatException("event needs a message name and map body");
                    OnEvent?.Invoke(evt["message"].AsString(), body);
                    Interlocked.Increment(ref _events);
                }
                catch (Exception eventError)
                {
                    Interlocked.Increment(ref _invalidEvents);
                    Volatile.Write(ref _lastFailure, "event dispatch: " + DescribeException(eventError));
                    Logger.Warn($"Event queue at {Simulator}: {Volatile.Read(ref _lastFailure)}");
                    // One bad event must not discard all subsequent appearance/object updates.
                }
            }

            lock (_payloadLock)
            {
                if (_reqPayloadMap == null) _reqPayloadMap = new OSDMap();
                _reqPayloadMap["ack"] = ack;
                _reqPayloadMap["done"] = OSD.FromBoolean(!Simulator.Connected);
                _reqPayloadBytes = OSDParser.SerializeLLSDXmlBytes(_reqPayloadMap);
            }
            Interlocked.Increment(ref _batches);
            Interlocked.Exchange(ref _lastSuccessTicks, DateTime.UtcNow.Ticks);
        }
    }
}
